using CosmosAwsDemo.Lambda.Api;

namespace CosmosAwsDemo.Lambda;

/// <summary>
/// API Gateway HTTP API (payload v2) entry point.
/// Handler: CosmosAwsDemo.Lambda::CosmosAwsDemo.Lambda.LambdaEntryPoint::FunctionHandlerAsync
/// </summary>
public class LambdaEntryPoint : Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction
{
    protected override void Init(IWebHostBuilder builder) => builder.UseStartup<Startup>();
}
