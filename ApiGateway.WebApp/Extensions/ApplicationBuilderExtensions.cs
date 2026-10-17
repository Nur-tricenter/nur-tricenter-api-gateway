using Consul;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace ApiGateway.WebApp.Extensions;

/// <summary>
/// Pipeline wiring for the pieces that are not plain middleware: Swagger UI (with a
/// Consul-driven document dropdown), the health endpoints and the correlation id.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>How often the Swagger UI document dropdown is rebuilt from Consul.</summary>
    private static readonly TimeSpan SwaggerRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MUST be kept alive for the lifetime of the process. A <see cref="System.Threading.Timer"/>
    /// with no live reference is garbage-collectable at any moment, and when the finalizer
    /// runs the timer simply stops firing - the dropdown would silently freeze with whatever
    /// services happened to be registered at startup. Holding it in a static field keeps a
    /// root alive for the whole app.
    /// </summary>
    private static System.Threading.Timer? _swaggerRefreshTimer;

    /// <summary>
    /// Swagger UI whose document list mirrors Consul's healthy services.
    /// Development-only: in production the docs must not be reachable.
    /// </summary>
    public static IApplicationBuilder UseSwaggerWithUiWithConsul(this WebApplication app)
    {
        app.UseSwagger();

        var consulClient = app.Services.GetRequiredService<IConsulClient>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApiGateway.WebApp.SwaggerRefresh");

        var uiOptions = new SwaggerUIOptions();
        uiOptions.SwaggerEndpoint("/swagger/v1/swagger.json", "API Gateway");

        // Reading Consul once at startup is not enough: compose-style startup (or simply
        // starting services in the wrong order) brings the gateway up before the backends
        // register. The reference implementation only snapshotted the catalog here, so
        // anything that registered later never appeared in the dropdown for the lifetime of
        // the process. Swashbuckle re-serializes ConfigObject on every request to
        // /swagger/index.js, so mutating Urls in place is enough - no restart needed.
        // `async void` because Timer only accepts an Action<object?> and there is no way to
        // await it; the try/catch below is what keeps a Consul outage from becoming an
        // unhandled-exception process crash.
        async void RefreshSwaggerEndpoints(object? state)
        {
            try
            {
                var serviceNames = await consulClient.Catalog.Services();

                var urls = new List<UrlDescriptor>
                {
                    new() { Url = "/swagger/v1/swagger.json", Name = "API Gateway" }
                };

                foreach (var serviceName in serviceNames.Response.Keys)
                {
                    if (string.Equals(serviceName, "consul", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Same health-filtered lookup as ConsulYarpConfigProvider, for the same
                    // reason: an instance whose check is failing should not be advertised.
                    var healthyInstances = await consulClient.Health.Service(
                        serviceName,
                        tag: null,
                        passingOnly: true);

                    if (healthyInstances.Response.Length == 0)
                    {
                        continue;
                    }

                    var firstInstance = healthyInstances.Response[0].Service;
                    urls.Add(new UrlDescriptor
                    {
                        // Addressed directly to the service, not through the gateway: the
                        // gateway strips its /api/{service} prefix and re-adds /api, which
                        // does not match each service's own swagger path.
                        Url = $"{firstInstance.Address}:{firstInstance.Port}/swagger/v1/swagger.json",
                        Name = $"{serviceName} API"
                    });
                }

                uiOptions.ConfigObject.Urls = urls;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh the Swagger document list from Consul.");
            }
        }

        _swaggerRefreshTimer = new System.Threading.Timer(
            RefreshSwaggerEndpoints,
            null,
            TimeSpan.Zero,
            SwaggerRefreshInterval);

        app.UseSwaggerUI(uiOptions);

        return app;
    }

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
            // pipeline has run, the response has usually already begun (health checks, Swagger
            // UI and every proxied response stream their body out), and Kestrel throws
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