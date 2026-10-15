using System.Text.Json.Serialization;

namespace CosmosAwsDemo.Lambda.Models;

public class Order
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("customerNumber")]
    public string? CustomerNumber { get; set; }

    [JsonPropertyName("lineItems")]
    public IList<OrderLineItem> LineItems { get; set; } = new List<OrderLineItem>();
}
