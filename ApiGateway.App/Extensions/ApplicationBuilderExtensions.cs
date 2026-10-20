using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace ApiGateway.App.Extensions;

/// <summary>
/// Pipeline wiring for the pieces that are not plain middleware: the health endpoints and
/// the correlation id.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately no Swagger UI and no OpenAPI document, matching the reference architecture
/// (<c>ms2026-store-api-gateway</c>), which gives the gateway one job — route <c>/api/{servicio}/**</c>
/// from Consul's health-filtered catalog — and no documentation endpoint.
/// </para>
/// <para>
/// The reason is not only fidelity. A reverse proxy has no controllers, so an OpenAPI document
/// generated from the app's own endpoints is empty. One synthesised from the route table would
/// describe paths and their upstream hosts but not the operations behind them: accurate about
/// where traffic goes, useless about what it expects to receive. Each microservice publishes its
/// own contract, which is where a consumer should look for that. A single empty "Authorize" prompt
/// on every operation, meanwhile, reads as "this route is protected" when nothing is protected.
/// </para>
/// </remarks>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Maps <c>/health</c> (machine-readable, Health Checks UI format) and the
    /// <c>/health-ui</c> dashboard on top of it.
    /// </summary>
    public static IApplicationBuilder UseHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
        });

        app.MapHealthChecksUI(options => options.UIPath = "/health-ui");

        return app;
    }

    /// <summary>
    /// Propagates a correlation id. If the caller already sent <c>X-Correlation-Id</c> it is
    /// honoured (trace continuity across services); otherwise the gateway mints one and
    /// echoes it back on the response so a client can quote it in a bug report.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this WebApplication app)
    {
        const string headerName = "X-Correlation-Id";

        app.Use(async (context, next) =>
        {
            if (string.IsNullOrWhiteSpace(context.Request.Headers[headerName]))
            {
                context.Request.Headers[headerName] = Guid.NewGuid().ToString();
            }

            // OnStarting, NOT an assignment after `await next()`: by the time the rest of the
            // pipeline has run, the response has usually already begun (the health check writes
            // its body, and every proxied response streams its body out), and Kestrel throws
            // "Headers are read-only, response has already started" on any late write.
            // OnStarting runs once, immediately before the first byte is flushed, while the
            // header dictionary is still mutable.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[headerName] = context.Request.Headers[headerName];
                return Task.CompletedTask;
            });

            await next();
        });

        return app;
    }
}