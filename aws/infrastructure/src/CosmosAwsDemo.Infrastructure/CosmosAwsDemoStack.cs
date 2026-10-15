using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AwsApigatewayv2Integrations;
using Constructs;
using Attribute = Amazon.CDK.AWS.DynamoDB.Attribute;
using ApiHttpMethod = Amazon.CDK.AWS.Apigatewayv2.HttpMethod;

namespace CosmosAwsDemo.Infrastructure;

public class CosmosAwsDemoStackProps : StackProps
{
    /// <summary>Output of `dotnet publish` for aws/src/CosmosAwsDemo.Lambda (cdk.json "build" produces it).</summary>
    public required string LambdaPackagePath { get; init; }

    /// <summary>Set false (-c ordersStreamEnabled=false) while bulk-importing historical orders.</summary>
    public bool OrdersStreamEnabled { get; init; } = true;
}

public class CosmosAwsDemoStack : Stack
{
    private const string Assembly = "CosmosAwsDemo.Lambda";

    public CosmosAwsDemoStack(Construct scope, string id, CosmosAwsDemoStackProps props) : base(scope, id, props)
    {
        if (!File.Exists(Path.Combine(props.LambdaPackagePath, $"{Assembly}.dll")))
        {
            throw new InvalidOperationException(
                $"Lambda package not found at {props.LambdaPackagePath}. Run `cdk synth`/`cdk deploy` from aws/infrastructure " +
                "(cdk.json's build step publishes it) or run the dotnet publish command from aws/README.md.");
        }

        // Demo stack: tables are deleted with the stack. Switch to RemovalPolicy.RETAIN for real data.
        var products = new Table(this, "ProductsTable", new TableProps
        {
            PartitionKey = new Attribute { Name = "category", Type = AttributeType.STRING },
            SortKey = new Attribute { Name = "id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        var orders = new Table(this, "OrdersTable", new TableProps
        {
            PartitionKey = new Attribute { Name = "id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Stream = StreamViewType.NEW_IMAGE,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        var code = Code.FromAsset(props.LambdaPackagePath);

        var apiFunction = new Function(this, "ApiFunction", new FunctionProps
        {
            Description = "HTTP API: POST /orders, GET /products, GET /products/{category}/{id}, GET /search/{description}",
            Runtime = Runtime.DOTNET_8,
            Handler = $"{Assembly}::{Assembly}.LambdaEntryPoint::FunctionHandlerAsync",
            Code = code,
            MemorySize = 512,
            Timeout = Duration.Seconds(30),
            Environment = new Dictionary<string, string>
            {
                ["PRODUCTS_TABLE_NAME"] = products.TableName,
                ["ORDERS_TABLE_NAME"] = orders.TableName,
            },
            LogGroup = NewLogGroup("ApiFunctionLogs"),
        });
        products.Grant(apiFunction, "dynamodb:GetItem", "dynamodb:Scan");
        orders.Grant(apiFunction, "dynamodb:PutItem");

        var streamFunction = new Function(this, "OrdersStreamFunction", new FunctionProps
        {
            Description = "Orders stream consumer: decrements product availableInventory per line item",
            Runtime = Runtime.DOTNET_8,
            Handler = $"{Assembly}::{Assembly}.OrdersStreamFunction::FunctionHandler",
            Code = code,
            MemorySize = 512,
            Timeout = Duration.Seconds(60),
            Environment = new Dictionary<string, string>
            {
                ["PRODUCTS_TABLE_NAME"] = products.TableName,
            },
            LogGroup = NewLogGroup("OrdersStreamFunctionLogs"),
        });
        products.Grant(streamFunction, "dynamodb:UpdateItem");

        var streamFailures = new Queue(this, "OrdersStreamDlq", new QueueProps
        {
            RetentionPeriod = Duration.Days(14),
            Encryption = QueueEncryption.SQS_MANAGED,
            EnforceSSL = true,
        });

        // Grants the function stream read (DescribeStream/GetRecords/GetShardIterator/ListStreams) on this stream only.
        streamFunction.AddEventSource(new DynamoEventSource(orders, new DynamoEventSourceProps
        {
            StartingPosition = StartingPosition.TRIM_HORIZON,
            BatchSize = 100,
            BisectBatchOnError = true,
            RetryAttempts = 5,
            ReportBatchItemFailures = true,
            OnFailure = new SqsDlq(streamFailures),
            Enabled = props.OrdersStreamEnabled,
            Filters = new[]
            {
                FilterCriteria.Filter(new Dictionary<string, object> { ["eventName"] = FilterRule.IsEqual("INSERT") }),
            },
        }));

        var api = new HttpApi(this, "HttpApi", new HttpApiProps
        {
            ApiName = "cosmos-aws-demo",
            Description = "Retail demo API (port of the Azure Functions HTTP triggers)",
        });
        var integration = new HttpLambdaIntegration("ApiIntegration", apiFunction);
        AddRoute(api, "/orders", ApiHttpMethod.POST, integration);
        AddRoute(api, "/products", ApiHttpMethod.GET, integration);
        AddRoute(api, "/products/{category}/{id}", ApiHttpMethod.GET, integration);
        AddRoute(api, "/search/{description}", ApiHttpMethod.GET, integration);

        _ = new CfnOutput(this, "ApiUrl", new CfnOutputProps { Value = api.ApiEndpoint });
        _ = new CfnOutput(this, "ProductsTableName", new CfnOutputProps { Value = products.TableName });
        _ = new CfnOutput(this, "OrdersTableName", new CfnOutputProps { Value = orders.TableName });
        _ = new CfnOutput(this, "OrdersStreamArn", new CfnOutputProps { Value = orders.TableStreamArn! });
        _ = new CfnOutput(this, "OrdersStreamDlqUrl", new CfnOutputProps { Value = streamFailures.QueueUrl });
    }

    private LogGroup NewLogGroup(string id) => new(this, id, new LogGroupProps
    {
        Retention = RetentionDays.TWO_WEEKS,
        RemovalPolicy = RemovalPolicy.DESTROY,
    });

    private static void AddRoute(HttpApi api, string path, ApiHttpMethod method, HttpRouteIntegration integration) =>
        api.AddRoutes(new AddRoutesOptions { Path = path, Methods = new[] { method }, Integration = integration });
}
