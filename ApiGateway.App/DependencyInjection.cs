using ApiGateway.App.Config;
using Consul;
using Yarp.ReverseProxy.Configuration;

namespace ApiGateway.App;

/// <summary>
/// Composition root helpers. Kept out of <c>Program.cs</c> so the registration order reads
/// as a list of decisions instead of a wall of extension calls.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Configuration key holding the Consul agent/base URL.</summary>
    public const string ServiceDiscoveryAddressKey = "ServiceRegistration:ServiceDiscoveryAddress";

    /// <summary>Configuration key holding the gateway's own health endpoint, consumed by the Health Checks UI.</summary>
    public const string SelfHealthEndpointKey = "HealthChecksUI:SelfHealthEndpoint";

    /// <summary>Fallback used when <see cref="SelfHealthEndpointKey"/> is absent (matches <c>launchSettings.json</c>).</summary>
    public const string DefaultSelfHealthEndpoint = "http://localhost:8080/health";

    public static IServiceCollection AddServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddConsulServiceDiscovery(configuration);
        services.AddReverseProxyWithConsul();
        services.AddGatewayHealthChecks(configuration);

        return services;
    }

    private static IServiceCollection AddConsulServiceDiscovery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rawAddress = configuration[ServiceDiscoveryAddressKey];

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            // Fail fast, and say WHICH key is wrong. The reference implementation does
            // `new Uri(value!)`, which turns a missing key into a bare ArgumentNullException
            // pointing at Consul's own source file - useless when you are staring at
            // appsettings.json trying to work out which setting is missing.
            throw new InvalidOperationException(
                $"Configuration key '{ServiceDiscoveryAddressKey}' is required but was not found. " +
                "Add it to appsettings.Development.json, e.g. \"ServiceDiscoveryAddress\": \"http://localhost:8500\".");
        }

        if (!Uri.TryCreate(rawAddress, UriKind.Absolute, out var consulAddress))
        {
            throw new InvalidOperationException(
                $"Configuration key '{ServiceDiscoveryAddressKey}' has an invalid value '{rawAddress}'. " +
                "It must be an absolute URI, e.g. 'http://localhost:8500'.");
        }

        services.AddSingleton<IConsulClient>(_ => new ConsulClient(cfg => cfg.Address = consulAddress));

        return services;
    }

    private static IServiceCollection AddReverseProxyWithConsul(this IServiceCollection services)
    {
        // IProxyConfigProvider must be registered BEFORE AddReverseProxy(): YARP resolves
        // it while building the proxy pipeline, so registering it afterwards leaves the
        // default (appsettings-only) provider in place and Consul routes are never published.
        services.AddSingleton<IProxyConfigProvider, ConsulYarpConfigProvider>();
        services.AddReverseProxy();

        return services;
    }

    private static IServiceCollection AddGatewayHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHealthChecks();

        var selfEndpoint = configuration[SelfHealthEndpointKey] ?? DefaultSelfHealthEndpoint;

        // NOTE: the reference implementation enumerates Consul here and calls
        //   services.BuildServiceProvider().GetServices<IConsulClient>()
        //       .SelectMany(c => c.Agent.Services().Result ...)
        // Two things are wrong with that:
        //   1. BuildServiceProvider() here builds a SECOND container, separate from the one
        //      the app runs on (never disposed, singletons duplicated).
        //   2. .Result blocks a thread on an async HTTP call DURING service registration,
        //      so an unreachable Consul turns "start the gateway" into a startup crash -
        //      exactly the opposite of what a gateway should do.
        // We register the gateway's own health endpoint only. Downstream services can be
        // added explicitly through the HealthChecksUI configuration section instead.
        services.AddHealthChecksUI(setup =>
        {
            setup.AddHealthCheckEndpoint("ApiGateway", selfEndpoint);
            setup.SetEvaluationTimeInSeconds(10);
        })
        .AddInMemoryStorage();

        return services;
    }
}