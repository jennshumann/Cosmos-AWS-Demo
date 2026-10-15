using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Lambda.Data;

public class ProductRepository
{
    /// <summary>Lower-cased copy of description written by the seed/migration scripts for case-insensitive search.</summary>
    public const string DescriptionLowerAttribute = "descriptionLower";

    private const int PageSize = 25;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public ProductRepository(IAmazonDynamoDB dynamoDb, DynamoDbSettings settings)
    {
        _dynamoDb = dynamoDb;
        _tableName = settings.ProductsTableName;
    }

    public async Task<Product?> GetAsync(string category, string id, CancellationToken ct = default)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["category"] = new() { S = category },
                ["id"] = new() { S = id },
            },
        }, ct);

        return response.Item is { Count: > 0 } item ? ItemMapper.ToProduct(item) : null;
    }

    /// <summary>
    /// Lists the whole catalog. There is no key condition that selects "every product", so this is a paginated
    /// Scan, the DynamoDB equivalent of the original ReadDocumentFeedAsync loop (MaxItemCount = 25).
    /// </summary>
    public Task<List<Product>> ListAsync(CancellationToken ct = default) =>
        ScanAllAsync(new ScanRequest { TableName = _tableName, Limit = PageSize }, ct);

    /// <summary>
    /// Case-insensitive substring match, mirroring Cosmos SQL CONTAINS(c.description, @d, true).
    /// DynamoDB contains() is case-sensitive, so it matches against the lower-cased copy; items written
    /// without that attribute fall back to a case-sensitive match on description.
    /// </summary>
    public Task<List<Product>> SearchByDescriptionAsync(string text, CancellationToken ct = default) =>
        ScanAllAsync(new ScanRequest
        {
            TableName = _tableName,
            Limit = PageSize,
            FilterExpression = "contains(#dl, :lower) OR contains(#d, :raw)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#dl"] = DescriptionLowerAttribute,
                ["#d"] = "description",
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":lower"] = new() { S = text.ToLowerInvariant() },
                [":raw"] = new() { S = text },
            },
        }, ct);

    private async Task<List<Product>> ScanAllAsync(ScanRequest request, CancellationToken ct)
    {
        var products = new List<Product>();
        do
        {
            var page = await _dynamoDb.ScanAsync(request, ct);
            if (page.Items is not null)
            {
                products.AddRange(page.Items.Select(ItemMapper.ToProduct));
            }

            request.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });

        return products;
    }
}
