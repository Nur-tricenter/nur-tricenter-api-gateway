using System.Net;
using ApiGateway.App.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ApiGateway.App.Tests;

/// <summary>
/// Drives the real CORS registration (<see cref="CorsExtensions"/>) plus the correlation-id middleware
/// on an in-memory server, in the same order as <c>Program.cs</c>. A plain endpoint stands in for
/// <c>MapReverseProxy</c>: CORS runs before the endpoint either way.
/// </summary>
public class CorsExtensionsTests
{
    private const string DemoOrigin = "http://localhost:15100";

    private static async Task<WebApplication> StartAsync(params string[] origins)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            origins.Select((origin, i) => new KeyValuePair<string, string?>($"{CorsExtensions.AllowedOriginsKey}:{i}", origin)));
        builder.Services.AddGatewayCors(builder.Configuration);

        var app = builder.Build();
        app.UseRouting();
        app.UseGatewayCors();
        app.UseCorrelationId();
        app.MapMethods("/api/catalog/v1/plans", ["GET", "POST"], () => Results.Ok("proxied"));

        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, "/api/catalog/v1/plans")
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "POST" },
                { "Access-Control-Request-Headers", "authorization,content-type" }
            }
        };

    [Fact]
    public void GetAllowedOrigins_TrimsBlanksAndTrailingSlashes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CorsExtensions.AllowedOriginsKey}:0"] = " http://localhost:15100/ ",
            [$"{CorsExtensions.AllowedOriginsKey}:1"] = "",
            [$"{CorsExtensions.AllowedOriginsKey}:2"] = "http://LOCALHOST:15100"
        }).Build();

        Assert.Equal([DemoOrigin], CorsExtensions.GetAllowedOrigins(configuration));
    }

    [Fact]
    public async Task Preflight_FromAllowedOrigin_IsAnsweredByTheGateway()
    {
        await using var app = await StartAsync(DemoOrigin);

        var response = await app.GetTestClient().SendAsync(Preflight(DemoOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DemoOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Contains("authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task ActualRequest_FromAllowedOrigin_ExposesTheCorrelationId()
    {
        await using var app = await StartAsync(DemoOrigin);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/plans") { Headers = { { "Origin", DemoOrigin } } };

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DemoOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-Correlation-Id", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task Request_FromOtherOrigin_GetsNoCorsHeaders()
    {
        await using var app = await StartAsync(DemoOrigin);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/plans") { Headers = { { "Origin", "http://evil.example" } } };

        var response = await app.GetTestClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task NoOriginsConfigured_CorsIsOff()
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/plans") { Headers = { { "Origin", DemoOrigin } } };

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
