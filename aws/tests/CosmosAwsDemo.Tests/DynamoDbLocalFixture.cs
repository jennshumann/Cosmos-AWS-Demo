using System.Text.Json;
using AmazonDynamoDBStreamsClient = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsClient;
using AmazonDynamoDBStreamsConfig = Amazon.DynamoDBStreams.AmazonDynamoDBStreamsConfig;
using IAmazonDynamoDBStreams = Amazon.DynamoDBStreams.IAmazonDynamoDBStreams;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using CosmosAwsDemo.Lambda;
using CosmosAwsDemo.Lambda.Data;

namespace CosmosAwsDemo.Tests;

/// <summary>
/// Creates a fresh Products/Orders table pair in DynamoDB Local (same keys and stream settings as the CDK stack),
/// seeds Products from seed/products.json, and exposes the API Lambda entry point for direct invocation.
/// </summary>
public sealed class DynamoDbLocalFixture : IAsyncLifetime
{
    public static readonly string Endpoint =
        Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_ENDPOINT") ?? "http://localhost:8000";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public string ProductsTable => $"Products-{_suffix}";
    public string OrdersTable => $"Orders-{_suffix}";
    public IAmazonDynamoDB DynamoDb { get; private set; } = null!;
    public IAmazonDynamoDBStreams Streams { get; private set; } = null!;
    public LambdaEntryPoint Api { get; private set; } = null!;
    public OrdersStreamFunction StreamFunction { get; private set; } = null!;
    public string OrdersStreamArn { get; private set; } = string.Empty;
    public IReadOnlyList<JsonElement> SeedProducts { get; private set; } = Array.Empty<JsonElement>();

    public async Task InitializeAsync()
    {
        SetDefault("AWS_ACCESS_KEY_ID", "local");
        SetDefault("AWS_SECRET_ACCESS_KEY", "local");
        SetDefault("AWS_REGION", "us-east-1");
        Environment.SetEnvironmentVariable(DynamoDbSettings.EndpointVariable, Endpoint);
        Environment.SetEnvironmentVariable(DynamoDbSettings.ProductsTableVariable, ProductsTable);
        Environment.SetEnvironmentVariable(DynamoDbSettings.OrdersTableVariable, OrdersTable);

        DynamoDb = DynamoDbSettings.CreateClient();
        Streams = new AmazonDynamoDBStreamsClient(new AmazonDynamoDBStreamsConfig
        {
            ServiceURL = Endpoint,
            AuthenticationRegion = "us-east-1",
        });

        await DynamoDb.CreateTableAsync(new CreateTableRequest
        {
            TableName = ProductsTable,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = new List<KeySchemaElement> { new("category", KeyType.HASH), new("id", KeyType.RANGE) },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new("category", ScalarAttributeType.S),
                new("id", ScalarAttributeType.S),
            },
        });

        var orders = await DynamoDb.CreateTableAsync(new CreateTableRequest
        {
            TableName = OrdersTable,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = new List<KeySchemaElement> { new("id", KeyType.HASH) },
            AttributeDefinitions = new List<AttributeDefinition> { new("id", ScalarAttributeType.S) },
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
        });
        OrdersStreamArn = orders.TableDescription.LatestStreamArn;

        await SeedProductsAsync();

        Api = new LambdaEntryPoint();
        StreamFunction = new OrdersStreamFunction(DynamoDb, ProductsTable);
    }

    public async Task DisposeAsync()
    {
        await DynamoDb.DeleteTableAsync(ProductsTable);
        await DynamoDb.DeleteTableAsync(OrdersTable);
        DynamoDb.Dispose();
        Streams.Dispose();
    }

    public Task<APIGatewayHttpApiV2ProxyResponse> InvokeApiAsync(string method, string path, string? body = null)
    {
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RouteKey = "$default",
            RawPath = path,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = body,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = method, Path = path },
            },
        };
        return Api.FunctionHandlerAsync(request, new TestLambdaContext());
    }

    public static string FindRepoFile(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relative)} above {AppContext.BaseDirectory}");
    }

    /// <summary>Same item shape that aws/migration/seed-dynamodb.sh writes.</summary>
    private async Task SeedProductsAsync()
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(FindRepoFile("seed", "products.json")));
        SeedProducts = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

        var writes = SeedProducts.Select(p =>
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["id"] = new() { S = p.GetProperty("id").GetString() },
                ["category"] = new() { S = p.GetProperty("category").GetString() },
                ["description"] = new() { S = p.GetProperty("description").GetString() },
                [ProductRepository.DescriptionLowerAttribute] = new() { S = p.GetProperty("description").GetString()!.ToLowerInvariant() },
                ["unitPrice"] = new() { N = p.GetProperty("unitPrice").GetRawText() },
                ["availableInventory"] = new() { N = p.GetProperty("availableInventory").GetRawText() },
            };
            return new WriteRequest { PutRequest = new PutRequest { Item = item } };
        }).ToList();

        await DynamoDb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>> { [ProductsTable] = writes },
        });
    }

    private static void SetDefault(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
