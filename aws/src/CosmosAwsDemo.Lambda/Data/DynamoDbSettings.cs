using Amazon.DynamoDBv2;

namespace CosmosAwsDemo.Lambda.Data;

public sealed record DynamoDbSettings(string ProductsTableName, string OrdersTableName)
{
    public const string ProductsTableVariable = "PRODUCTS_TABLE_NAME";
    public const string OrdersTableVariable = "ORDERS_TABLE_NAME";
    public const string EndpointVariable = "DYNAMODB_ENDPOINT";

    public static DynamoDbSettings FromEnvironment() => new(
        Required(ProductsTableVariable),
        Environment.GetEnvironmentVariable(OrdersTableVariable) ?? string.Empty);

    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is not set.");

    /// <summary>
    /// Uses the regular AWS credential/region chain. DYNAMODB_ENDPOINT points the client at DynamoDB Local.
    /// </summary>
    public static IAmazonDynamoDB CreateClient()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        if (string.IsNullOrEmpty(endpoint))
        {
            return new AmazonDynamoDBClient();
        }

        return new AmazonDynamoDBClient(new AmazonDynamoDBConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1",
        });
    }
}
