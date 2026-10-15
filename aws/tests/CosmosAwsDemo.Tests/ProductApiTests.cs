using System.Net;
using System.Text.Json;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Tests;

/// <summary>Parity for GetProducts, GetProductByIdFromRoute and GetProductsByDescription.</summary>
public class ProductApiTests : IClassFixture<DynamoDbLocalFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DynamoDbLocalFixture _fx;

    public ProductApiTests(DynamoDbLocalFixture fx) => _fx = fx;

    [Fact]
    public async Task GetProducts_ReturnsAllFiveSeedProducts()
    {
        var response = await _fx.InvokeApiAsync("GET", "/products");

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        var products = JsonSerializer.Deserialize<List<Product>>(response.Body, Json)!;
        Assert.Equal(5, products.Count);

        foreach (var seed in _fx.SeedProducts)
        {
            var product = Assert.Single(products, p => p.Id == seed.GetProperty("id").GetString());
            Assert.Equal(seed.GetProperty("category").GetString(), product.Category);
            Assert.Equal(seed.GetProperty("description").GetString(), product.Description);
            Assert.Equal(seed.GetProperty("unitPrice").GetDecimal(), product.UnitPrice);
            Assert.Equal(seed.GetProperty("availableInventory").GetInt32(), product.AvailableInventory);
        }

        Assert.DoesNotContain("descriptionLower", response.Body);
    }

    [Fact]
    public async Task GetProductByCategoryAndId_ReturnsProduct()
    {
        var response = await _fx.InvokeApiAsync("GET", "/products/Apparel/COG-001");

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        var product = JsonSerializer.Deserialize<Product>(response.Body, Json)!;
        Assert.Equal("COG-001", product.Id);
        Assert.Equal("Cognition hoodie", product.Description);
        Assert.Equal("Apparel", product.Category);
        Assert.Equal(65.00m, product.UnitPrice);
        Assert.Equal(120, product.AvailableInventory);
    }

    [Theory]
    [InlineData("/products/Drinkware/COG-001")] // right id, wrong partition key (same as Cosmos)
    [InlineData("/products/Apparel/NOPE-999")]
    public async Task GetProductByCategoryAndId_NotFound_Returns404(string path)
    {
        var response = await _fx.InvokeApiAsync("GET", path);

        Assert.Equal((int)HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("hoodie", new[] { "COG-001" })]
    [InlineData("water", new[] { "COG-003" })]
    [InlineData("s", new[] { "COG-002", "COG-004", "COG-005" })] // substring, not prefix/word match
    [InlineData("HOODIE", new[] { "COG-001" })] // CONTAINS(..., true) is case-insensitive
    [InlineData("T-Shirt", new[] { "COG-002" })]
    [InlineData("cognition", new[] { "COG-001", "COG-002", "COG-003", "COG-004", "COG-005" })]
    [InlineData("crew socks", new[] { "COG-005" })]
    [InlineData("mug", new string[0])]
    public async Task SearchByDescription_MatchesSubstringCaseInsensitively(string term, string[] expectedIds)
    {
        var response = await _fx.InvokeApiAsync("GET", $"/search/{Uri.EscapeDataString(term)}");

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        var ids = JsonSerializer.Deserialize<List<Product>>(response.Body, Json)!.Select(p => p.Id).Order();
        Assert.Equal(expectedIds.Order(), ids);
    }
}
