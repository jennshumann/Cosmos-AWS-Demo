using System.Text.Json;
using Amazon.DynamoDBv2;
using CosmosAwsDemo.Lambda.Data;
using CosmosAwsDemo.Lambda.Models;

namespace CosmosAwsDemo.Lambda.Api;

public class Startup
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_ => DynamoDbSettings.FromEnvironment());
        services.AddSingleton(_ => DynamoDbSettings.CreateClient());
        services.AddSingleton<ProductRepository>();
        services.AddSingleton<OrderRepository>();
        services.AddRouting();
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            // Replaces CreateOrder (sync, out param) and CreateOrder2 (async, IAsyncCollector).
            endpoints.MapPost("/orders", async (HttpRequest request, OrderRepository orders, CancellationToken ct) =>
            {
                Order? order;
                try
                {
                    order = await JsonSerializer.DeserializeAsync<Order>(request.Body, WebJson, ct);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = $"Invalid order JSON: {ex.Message}" });
                }

                if (order is null)
                {
                    return Results.BadRequest(new { error = "Request body must be an order." });
                }

                await orders.SaveAsync(order, ct);
                return Results.NoContent();
            });

            // GetProductByIdFromRoute
            endpoints.MapGet("/products/{category}/{id}", async (string category, string id, ProductRepository products, CancellationToken ct) =>
                await products.GetAsync(category, id, ct) is { } product ? Results.Ok(product) : Results.NotFound());

            // GetProducts
            endpoints.MapGet("/products", async (ProductRepository products, CancellationToken ct) =>
                Results.Ok(await products.ListAsync(ct)));

            // GetProductsByDescription
            endpoints.MapGet("/search/{description}", async (string description, ProductRepository products, CancellationToken ct) =>
                Results.Ok(await products.SearchByDescriptionAsync(description, ct)));
        });
    }
}
