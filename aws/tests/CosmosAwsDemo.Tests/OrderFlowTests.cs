using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Tests;

/// <summary>
/// Parity for CreateOrder/CreateOrder2 + OrdersChangeFeedProcessor:
/// POST /orders -> Orders table -> DynamoDB stream -> OrdersStreamFunction -> availableInventory decremented.
/// </summary>
public class OrderFlowTests : IClassFixture<DynamoDbLocalFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DynamoDbLocalFixture _fx;

    public OrderFlowTests(DynamoDbLocalFixture fx) => _fx = fx;

    [Fact]
    public async Task CreateOrder_StreamTriggersInventoryDecrement()
    {
        var hoodieBefore = await InventoryAsync("Apparel", "COG-001");
        var bottleBefore = await InventoryAsync("Drinkware", "COG-003");
        var orderId = $"order-{Guid.NewGuid():N}";

        var response = await PostOrderAsync(new
        {
            id = orderId,
            customerNumber = "CUST-42",
            lineItems = new[]
            {
                new { productId = "COG-001", category = "Apparel", quantity = 2 },
                new { productId = "COG-003", category = "Drinkware", quantity = 1 },
            },
        });
        Assert.Equal((int)HttpStatusCode.NoContent, response.StatusCode);

        var stored = await _fx.DynamoDb.GetItemAsync(_fx.OrdersTable,
            new Dictionary<string, AttributeValue> { ["id"] = new() { S = orderId } });
        Assert.Equal("CUST-42", stored.Item["customerNumber"].S);
        Assert.Equal(2, stored.Item["lineItems"].L.Count);

        var records = await RecordsForAsync(orderId);
        var record = Assert.Single(records);
        Assert.Equal("INSERT", record.EventName);

        var result = await _fx.StreamFunction.FunctionHandler(StreamTestHarness.ToEvent(records), new TestLambdaContext());

        Assert.Empty(result.BatchItemFailures);
        Assert.Equal(hoodieBefore - 2, await InventoryAsync("Apparel", "COG-001"));
        Assert.Equal(bottleBefore - 1, await InventoryAsync("Drinkware", "COG-003"));

        var api = JsonSerializer.Deserialize<Product>((await _fx.InvokeApiAsync("GET", "/products/Apparel/COG-001")).Body, Json)!;
        Assert.Equal(hoodieBefore - 2, api.AvailableInventory);
    }

    [Fact]
    public async Task CreateOrder_WithoutId_GeneratesOne()
    {
        var customer = $"CUST-{Guid.NewGuid():N}";

        var response = await PostOrderAsync(new
        {
            customerNumber = customer,
            lineItems = new[] { new { productId = "COG-004", category = "Accessories", quantity = 1 } },
        });

        Assert.Equal((int)HttpStatusCode.NoContent, response.StatusCode);
        var scan = await _fx.DynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _fx.OrdersTable,
            FilterExpression = "customerNumber = :c",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":c"] = new() { S = customer } },
        });
        var item = Assert.Single(scan.Items);
        Assert.True(Guid.TryParse(item["id"].S, out _));
    }

    [Fact]
    public async Task CreateOrder_InvalidJson_Returns400()
    {
        var response = await _fx.InvokeApiAsync("POST", "/orders", "{ not json");

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StreamHandler_UnknownProduct_IsSkippedWithoutCreatingAnItem()
    {
        var socksBefore = await InventoryAsync("Apparel", "COG-005");
        var orderId = $"order-{Guid.NewGuid():N}";

        await PostOrderAsync(new
        {
            id = orderId,
            customerNumber = "CUST-7",
            lineItems = new[]
            {
                new { productId = "COG-999", category = "Apparel", quantity = 1 },
                new { productId = "COG-005", category = "Apparel", quantity = 3 },
            },
        });
        var result = await _fx.StreamFunction.FunctionHandler(
            StreamTestHarness.ToEvent(await RecordsForAsync(orderId)), new TestLambdaContext());

        Assert.Empty(result.BatchItemFailures);
        Assert.Equal(socksBefore - 3, await InventoryAsync("Apparel", "COG-005"));
        var phantom = await _fx.DynamoDb.GetItemAsync(_fx.ProductsTable, ProductKey("Apparel", "COG-999"));
        Assert.False(phantom.IsItemSet);
    }

    [Fact]
    public async Task StreamHandler_IgnoresModifyOfExistingOrder()
    {
        var orderId = $"order-{Guid.NewGuid():N}";
        var order = new
        {
            id = orderId,
            customerNumber = "CUST-9",
            lineItems = new[] { new { productId = "COG-004", category = "Accessories", quantity = 5 } },
        };

        await PostOrderAsync(order);
        await _fx.StreamFunction.FunctionHandler(
            StreamTestHarness.ToEvent(await RecordsForAsync(orderId)), new TestLambdaContext());
        var afterInsert = await InventoryAsync("Accessories", "COG-004");

        // Upsert of the same id with changed data -> MODIFY stream record. (An identical put emits no record at all.)
        await PostOrderAsync(order with { customerNumber = "CUST-9-UPDATED" });
        var records = await RecordsForAsync(orderId, expected: 2);
        var modify = Assert.Single(records, r => r.EventName == "MODIFY");
        await _fx.StreamFunction.FunctionHandler(StreamTestHarness.ToEvent(new[] { modify }), new TestLambdaContext());

        Assert.Equal(afterInsert, await InventoryAsync("Accessories", "COG-004"));
    }

    [Fact]
    public async Task ConcurrentOrders_DecrementAtomically()
    {
        const int orderCount = 25;
        var before = await InventoryAsync("Apparel", "COG-002");
        var ids = Enumerable.Range(0, orderCount).Select(_ => $"order-{Guid.NewGuid():N}").ToHashSet();

        await Task.WhenAll(ids.Select(id => PostOrderAsync(new
        {
            id,
            customerNumber = "CUST-RACE",
            lineItems = new[] { new { productId = "COG-002", category = "Apparel", quantity = 1 } },
        })));

        var records = await StreamTestHarness.WaitForRecordsAsync(_fx.Streams, _fx.OrdersStreamArn,
            r => ids.Contains(StreamTestHarness.OrderId(r)!), orderCount);

        // One invocation per record, all in parallel: the original read-modify-write would lose updates here.
        var results = await Task.WhenAll(records.Select(r =>
            _fx.StreamFunction.FunctionHandler(StreamTestHarness.ToEvent(new[] { r }), new TestLambdaContext())));

        Assert.All(results, r => Assert.Empty(r.BatchItemFailures));
        Assert.Equal(before - orderCount, await InventoryAsync("Apparel", "COG-002"));
    }

    private Task<Amazon.Lambda.APIGatewayEvents.APIGatewayHttpApiV2ProxyResponse> PostOrderAsync(object order) =>
        _fx.InvokeApiAsync("POST", "/orders", JsonSerializer.Serialize(order, Json));

    private Task<List<Amazon.Lambda.DynamoDBEvents.DynamoDBEvent.DynamodbStreamRecord>> RecordsForAsync(string orderId, int expected = 1) =>
        StreamTestHarness.WaitForRecordsAsync(_fx.Streams, _fx.OrdersStreamArn,
            r => StreamTestHarness.OrderId(r) == orderId, expected);

    private async Task<int> InventoryAsync(string category, string id)
    {
        var response = await _fx.DynamoDb.GetItemAsync(_fx.ProductsTable, ProductKey(category, id));
        return int.Parse(response.Item["availableInventory"].N);
    }

    private static Dictionary<string, AttributeValue> ProductKey(string category, string id) => new()
    {
        ["category"] = new() { S = category },
        ["id"] = new() { S = id },
    };
}
