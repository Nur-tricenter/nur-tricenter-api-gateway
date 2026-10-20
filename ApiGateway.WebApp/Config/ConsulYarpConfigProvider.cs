using Consul;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

// Both Consul and YARP expose a RouteConfig / DestinationConfig, so the YARP ones are
// aliased explicitly instead of being resolved by luck.
using DestinationConfig = Yarp.ReverseProxy.Configuration.DestinationConfig;
using RouteConfig = Yarp.ReverseProxy.Configuration.RouteConfig;

namespace ApiGateway.WebApp.Config;

/// <summary>
/// Bridges Consul service discovery into YARP: every 30 seconds it asks Consul which
/// services exist AND which of their instances are healthy, then rebuilds the whole route
/// table from that snapshot.
/// </summary>
/// <remarks>
/// Design constraints that shaped this class:
/// <list type="bullet">
/// <item>A discovery outage must never take the gateway down. Consul being unreachable
/// degrades the gateway to "last known good routes", it does not fail the process.</item>
/// <item>Routes disappear as soon as a service has no healthy instance left, so a crashed
/// backend stops receiving traffic without anyone editing configuration.</item>
/// <item>The whole config is swapped atomically (immutable snapshot + change token) because
/// YARP reads it concurrently from many request threads.</item>
/// </list>
/// </remarks>
public class ConsulYarpConfigProvider : IProxyConfigProvider, IDisposable
{
    /// <summary>How often the route table is rebuilt from Consul.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<ConsulYarpConfigProvider> _logger;
    private readonly IConsulClient _consulClient;
    private readonly IHostEnvironment _environment;
    private readonly Timer _timer;

    // Volatile because Refresh runs on a timer thread while GetConfig runs on request
    // threads: without it the JIT/thread cache could hand back a stale reference.
    private volatile ConsulProxyConfig _config;

    public ConsulYarpConfigProvider(
        ILogger<ConsulYarpConfigProvider> logger,
        IConsulClient consulClient,
        IHostEnvironment environment)
    {
        _logger = logger;
        _consulClient = consulClient;
        _environment = environment;
        _config = new ConsulProxyConfig([], [], new CancellationTokenSource());

        // DueTime = TimeSpan.Zero so the very first snapshot is taken at startup instead of
        // 30 seconds later - otherwise the gateway would reject traffic in its first window.
        _timer = new Timer(Refresh, null, TimeSpan.Zero, RefreshInterval);
    }

    /// <summary>Immutable snapshot YARP pulls routes and clusters from.</summary>
    public IProxyConfig GetConfig() => _config;

    /// <summary>
    /// Timer callback. Deliberately <c>async void</c>: <see cref="Timer"/> takes an
    /// <c>Action&lt;object?&gt;</c> and there is no way to await it. All exceptions are
    /// swallowed here - an escaping exception on a timer callback would tear the process
    /// down with an unhandled-exception crash.
    /// </summary>
    private async void Refresh(object? state) => await RefreshAsync();

    /// <summary>
    /// Rebuilds the route table from Consul. Safe to call directly (tests and, later, an
    /// admin endpoint). Never throws.
    /// </summary>
    internal async Task RefreshAsync()
    {
        try
        {
            // Catalog.Services() only returns the service NAMES the agent knows about; it
            // carries no health information whatsoever. Health.Service(name, tag: null,
            // passingOnly: true) is what actually filters instances whose checks are
            // failing, which is what makes a crashed-but-still-registered instance stop
            // receiving traffic automatically.
            var serviceNames = await _consulClient.Catalog.Services();

            var routes = new Dictionary<string, RouteConfig>(StringComparer.OrdinalIgnoreCase);
            var clusters = new Dictionary<string, ClusterConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var serviceName in serviceNames.Response.Keys)
            {
                // Consul registers itself; proxying to it would be pointless.
                if (string.Equals(serviceName, "consul", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var healthyInstances = await _consulClient.Health.Service(
                    serviceName,
                    tag: null,
                    passingOnly: true);

                // No healthy instance => no route at all. This is intentional: the route
                // disappears from the table and YARP answers 404, instead of proxying into
                // a black hole and burning the caller's timeout.
                if (healthyInstances.Response.Length == 0)
                {
                    _logger.LogWarning(
                        "Service {ServiceName} has no healthy instances; its route will not be published.",
                        serviceName);
                    continue;
                }

                var clusterId = $"{serviceName}-cluster";
                var routePrefix = $"/api/{serviceName}";
                // /api/catalog/api/v1/foods -> /api/v1/foods
                var routePrefixPlusCatchAll = $"{routePrefix}/{{**catch-all}}";

                var destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var instance in healthyInstances.Response)
                {
                    destinations[instance.Service.ID] = new DestinationConfig
                    {
                        // The scheme is not part of Consul's registration, so the gateway
                        // supplies it. Downstream services listen on plain HTTP in dev: the
                        // Development certificate escape in BuildHttpClientConfig accepts any
                        // certificate, and a future HTTPS migration can decide whether to
                        // prefix with http or https per environment.
                        Address = $"http://{instance.Service.Address}:{instance.Service.Port}"
                    };
                }

                clusters[clusterId] = new ClusterConfig
                {
                    ClusterId = clusterId,
                    Destinations = destinations,
                    HttpClient = BuildHttpClientConfig()
                };

                routes[clusterId] = new RouteConfig
                {
                    RouteId = $"{serviceName}-route",
                    ClusterId = clusterId,
                    Match = new RouteMatch { Path = routePrefixPlusCatchAll },
                    Transforms =
                    [
                        new Dictionary<string, string> { { "PathRemovePrefix", routePrefix } },
                        new Dictionary<string, string> { { "PathPrefix", "/api" } }
                    ]
                };
            }

            SwapConfig(routes.Values.ToList(), clusters.Values.ToList());
        }
        catch (Exception ex)
        {
            // Consul unreachable, DNS failing, malformed payload... whatever it is, the
            // previously published config keeps serving traffic. That is the whole point of
            // a gateway: discovery is a convenience, availability is not optional.
            _logger.LogError(ex, "Failed to refresh the YARP configuration from Consul; keeping the previous route table.");
        }
    }

    /// <summary>
    /// Publishes a new immutable snapshot and signals the OLD one, because YARP holds a
    /// subscription on the change token of the config instance it read. Cancelling only the
    /// new token (the common mistake) means nothing ever fires and YARP keeps the very
    /// first snapshot forever.
    /// </summary>
    private void SwapConfig(List<RouteConfig> routes, List<ClusterConfig> clusters)
    {
        var previousConfig = _config;
        _config = new ConsulProxyConfig(routes, clusters, new CancellationTokenSource());
        previousConfig.SignalChange();

        _logger.LogInformation(
            "YARP configuration refreshed from Consul: {RouteCount} route(s) and {ClusterCount} cluster(s) built from healthy instances only.",
            routes.Count,
            clusters.Count);
    }

    private HttpClientConfig? BuildHttpClientConfig()
    {
        // Development escape hatch: local services are registered with the ASP.NET Core
        // HTTPS development certificate, which is self-signed. Fully validating it would
        // require the machine's trusted-root store to contain that cert, and the account
        // running this process may have no permission to write to it. Rather than forcing
        // an OS-level trust change on every developer machine, accept the certificate
        // explicitly - and ONLY in Development. Never enable this against a real cluster:
        // it disables server certificate validation entirely, so any MITM would be trusted.
        if (!_environment.IsDevelopment())
        {
            return null;
        }

        _logger.LogWarning(
            "Development environment detected: downstream HTTPS certificates are accepted without validation.");

        return new HttpClientConfig { DangerousAcceptAnyServerCertificate = true };
    }

    public void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Immutable YARP configuration snapshot. Each instance owns the
    /// <see cref="CancellationTokenSource"/> that backs its <see cref="ChangeToken"/>.
    /// </summary>
    private sealed class ConsulProxyConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _changeTokenSource;

        public ConsulProxyConfig(
            IReadOnlyList<RouteConfig> routes,
            IReadOnlyList<ClusterConfig> clusters,
            CancellationTokenSource changeTokenSource)
        {
            Routes = routes;
            Clusters = clusters;
            _changeTokenSource = changeTokenSource;
            ChangeToken = new CancellationChangeToken(changeTokenSource.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void SignalChange() => _changeTokenSource.Cancel();
    }
}