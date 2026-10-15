using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Lambda.Data;

public class InventoryUpdateService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _productsTableName;

    public InventoryUpdateService(IAmazonDynamoDB dynamoDb, string productsTableName)
    {
        _dynamoDb = dynamoDb;
        _productsTableName = productsTableName;
    }

    /// <summary>
    /// Atomically decrements availableInventory with ADD (no read-modify-write race). The condition stops ADD from
    /// creating a new product item when the line item points at a product that doesn't exist.
    /// </summary>
    /// <returns>false if the product doesn't exist.</returns>
    public async Task<bool> DecrementAvailableInventoryAsync(OrderLineItem lineItem, CancellationToken ct = default)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _productsTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["category"] = new() { S = lineItem.Category },
                    ["id"] = new() { S = lineItem.ProductId },
                },
                UpdateExpression = "ADD availableInventory :qty",
                ConditionExpression = "attribute_exists(id)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":qty"] = new() { N = (-lineItem.Quantity).ToString(CultureInfo.InvariantCulture) },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }
}
