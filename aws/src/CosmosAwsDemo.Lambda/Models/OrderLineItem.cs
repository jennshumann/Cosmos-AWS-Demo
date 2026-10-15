using System.Text.Json.Serialization;

namespace CosmosAwsDemo.Lambda.Models;

public class OrderLineItem
{
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}
