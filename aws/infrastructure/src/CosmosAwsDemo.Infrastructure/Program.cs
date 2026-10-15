using Amazon.CDK;
using CosmosAwsDemo.Infrastructure;

var app = new App();

new CosmosAwsDemoStack(app, "CosmosAwsDemoStack", new CosmosAwsDemoStackProps
{
    Env = new Amazon.CDK.Environment
    {
        Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
        Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION"),
    },
    LambdaPackagePath = app.Node.TryGetContext("lambdaPackagePath") as string
        ?? Path.GetFullPath(Path.Combine("..", "src", "CosmosAwsDemo.Lambda", "bin", "lambda-publish")),
    OrdersStreamEnabled = !string.Equals(app.Node.TryGetContext("ordersStreamEnabled") as string, "false",
        StringComparison.OrdinalIgnoreCase),
    Description = "AWS port of the Cosmos DB + Azure Functions retail demo (DynamoDB, Lambda, HTTP API).",
});

app.Synth();
