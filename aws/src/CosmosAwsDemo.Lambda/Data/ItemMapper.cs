using System.Globalization;
using Amazon.DynamoDBv2.Model;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Lambda.Data;

public static class ItemMapper
{
    public static Product ToProduct(IDictionary<string, AttributeValue> item) => new()
    {
        Id = item.TryGetValue("id", out var id) ? id.S : string.Empty,
        Category = item.TryGetValue("category", out var category) ? category.S : string.Empty,
        Description = item.TryGetValue("description", out var description) ? description.S : null,
        UnitPrice = item.TryGetValue("unitPrice", out var price) && price.N is not null
            ? decimal.Parse(price.N, CultureInfo.InvariantCulture)
            : 0m,
        AvailableInventory = item.TryGetValue("availableInventory", out var inventory) && inventory.N is not null
            ? int.Parse(inventory.N, CultureInfo.InvariantCulture)
            : 0,
    };

    public static Dictionary<string, AttributeValue> ToItem(Order order)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["id"] = new() { S = order.Id },
            ["lineItems"] = new()
            {
                L = order.LineItems.Select(li => new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["productId"] = new() { S = li.ProductId },
                        ["category"] = new() { S = li.Category },
                        ["quantity"] = new() { N = li.Quantity.ToString(CultureInfo.InvariantCulture) },
                    },
                }).ToList(),
            },
        };

        if (order.CustomerNumber is not null)
        {
            item["customerNumber"] = new AttributeValue { S = order.CustomerNumber };
        }

        return item;
    }
}
