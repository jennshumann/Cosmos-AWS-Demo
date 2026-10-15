using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Lambda.Data;

public class OrderRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public OrderRepository(IAmazonDynamoDB dynamoDb, DynamoDbSettings settings)
    {
        _dynamoDb = dynamoDb;
        _tableName = settings.OrdersTableName;
    }

    /// <summary>
    /// Upserts the order (same semantics as the Cosmos output binding). Like the binding, a missing id is generated.
    /// </summary>
    public async Task<Order> SaveAsync(Order order, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(order.Id))
        {
            order.Id = Guid.NewGuid().ToString();
        }

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = ItemMapper.ToItem(order),
        }, ct);

        return order;
    }
}
