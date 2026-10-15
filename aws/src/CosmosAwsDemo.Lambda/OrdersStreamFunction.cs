using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using CosmosAwsDemo.Lambda.Data;
using CosmosAwsDemo.Lambda.Models;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CosmosAwsDemo.Lambda;

/// <summary>
/// Replaces OrdersChangeFeedProcessor: triggered by the Orders table stream, decrements product inventory.
/// Handler: CosmosAwsDemo.Lambda::CosmosAwsDemo.Lambda.OrdersStreamFunction::FunctionHandler
/// </summary>
public class OrdersStreamFunction
{
    private readonly InventoryUpdateService _inventory;

    public OrdersStreamFunction()
        : this(DynamoDbSettings.CreateClient(), DynamoDbSettings.Required(DynamoDbSettings.ProductsTableVariable))
    {
    }

    public OrdersStreamFunction(IAmazonDynamoDB dynamoDb, string productsTableName)
    {
        _inventory = new InventoryUpdateService(dynamoDb, productsTableName);
    }

    /// <summary>
    /// Records are processed in order. On an unexpected error the failing record is reported via
    /// ReportBatchItemFailures so Lambda retries from that record instead of replaying the whole batch.
    /// </summary>
    public async Task<StreamsEventResponse> FunctionHandler(DynamoDBEvent dynamoEvent, ILambdaContext context)
    {
        var response = new StreamsEventResponse { BatchItemFailures = new List<StreamsEventResponse.BatchItemFailure>() };

        foreach (var record in dynamoEvent.Records ?? new List<DynamoDBEvent.DynamodbStreamRecord>())
        {
            // The event source mapping already filters to INSERT; this guard keeps direct invocations consistent.
            if (record.EventName != "INSERT" || record.Dynamodb?.NewImage is null)
            {
                continue;
            }

            try
            {
                var order = ToOrder(record.Dynamodb.NewImage);
                foreach (var lineItem in order.LineItems)
                {
                    if (!await _inventory.DecrementAvailableInventoryAsync(lineItem))
                    {
                        context.Logger.LogWarning(
                            $"Order {order.Id}: product {lineItem.Category}/{lineItem.ProductId} not found; inventory not updated.");
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Failed to process stream record {record.Dynamodb.SequenceNumber}: {ex}");
                response.BatchItemFailures.Add(new StreamsEventResponse.BatchItemFailure
                {
                    ItemIdentifier = record.Dynamodb.SequenceNumber,
                });
                break;
            }
        }

        return response;
    }

    public static Order ToOrder(Dictionary<string, DynamoDBEvent.AttributeValue> image) => new()
    {
        Id = image.TryGetValue("id", out var id) ? id.S : null,
        CustomerNumber = image.TryGetValue("customerNumber", out var customer) ? customer.S : null,
        LineItems = image.TryGetValue("lineItems", out var lineItems) && lineItems.L is not null
            ? lineItems.L.Select(li => new OrderLineItem
            {
                ProductId = li.M["productId"].S,
                Category = li.M["category"].S,
                Quantity = int.Parse(li.M["quantity"].N, CultureInfo.InvariantCulture),
            }).ToList()
            : new List<OrderLineItem>(),
    };
}
