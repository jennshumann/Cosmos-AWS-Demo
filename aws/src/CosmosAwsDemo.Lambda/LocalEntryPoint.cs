using CosmosAwsDemo.Lambda.Api;

namespace CosmosAwsDemo.Lambda;

/// <summary>Runs the same API on Kestrel for local development (e.g. against DynamoDB Local).</summary>
public static class LocalEntryPoint
{
    public static void Main(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(web => web.UseStartup<Startup>())
            .Build()
            .Run();
}
