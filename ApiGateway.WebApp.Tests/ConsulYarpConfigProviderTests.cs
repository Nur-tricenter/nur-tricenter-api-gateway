using ApiGateway.WebApp.Config;
using Consul;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using Xunit;
using Yarp.ReverseProxy.Configuration;

// Consul also exposes a LogLevel type (its agent log configuration), so the logging one is
// pinned explicitly instead of relying on which namespace wins the lookup.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ApiGateway.WebApp.Tests;

/// <summary>
/// Unit tests for <see cref="ConsulYarpConfigProvider"/>.
/// <para>
/// The provider starts a 30 second timer with <c>TimeSpan.Zero</c> due time, so it refreshes
/// immediately on construction. Every test therefore calls <c>RefreshAsync()</c> explicitly -
/// that makes the assertions deterministic instead of racing the timer - and the single test
/// that specifically covers the "it refreshes on its own" behaviour uses bounded polling.
/// </para>
/// </summary>
public class ConsulYarpConfigProviderTests
{
    private const string HealthyServiceName = "catalog";
    private const string UnhealthyServiceName = "empty-service";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Mock<ILogger<ConsulYarpConfigProvider>> _loggerMock = new();
    private readonly Mock<IConsulClient> _consulClientMock = new();
    private readonly Mock<ICatalogEndpoint> _catalogEndpointMock = new();
    private readonly Mock<IHealthEndpoint> _healthEndpointMock = new();

    public ConsulYarpConfigProviderTests()
    {
        _consulClientMock.SetupGet(client => client.Catalog).Returns(_catalogEndpointMock.Object);
        _consulClientMock.SetupGet(client => client.Health).Returns(_healthEndpointMock.Object);

        // Sensible defaults so the refresh that fires during construction never explodes
        // before a test has arranged its own scenario.
        SetupCatalog([HealthyServiceName, "consul", UnhealthyServiceName]);
        SetupHealthyInstances(HealthyServiceName, ("catalog-1", "10.0.0.1", 8080));
        SetupHealthyInstances(UnhealthyServiceName);
    }

    [Fact]
    public async Task Refresh_WithCatalogPlusConsulAndUnhealthyService_PublishesRouteOnlyForHealthyService()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert
        var config = sut.GetConfig();

        var route = Assert.Single(config.Routes);
        Assert.Equal("catalog-route", route.RouteId);

        var cluster = Assert.Single(config.Clusters);
        Assert.Equal("catalog-cluster", cluster.ClusterId);

        // consul is the discovery server itself: never routed to.
        Assert.DoesNotContain(config.Routes, candidate => candidate.RouteId!.StartsWith("consul", StringComparison.Ordinal));

        // A service with zero healthy instances must not be published at all, otherwise YARP
        // would happily proxy into an instance whose checks are failing.
        Assert.DoesNotContain(config.Routes, candidate => candidate.RouteId!.StartsWith(UnhealthyServiceName, StringComparison.Ordinal));
        Assert.DoesNotContain(config.Clusters, candidate => candidate.ClusterId!.StartsWith(UnhealthyServiceName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_QueriesHealthWithPassingOnlyTrue_AndNeverQueriesConsul()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert: passingOnly is the whole reason a crashed-but-registered instance stops
        // receiving traffic, so it is asserted rather than assumed.
        _healthEndpointMock.Verify(
            endpoint => endpoint.Service(HealthyServiceName, It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _healthEndpointMock.Verify(
            endpoint => endpoint.Service("consul", It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Refresh_WithSingleHealthyService_ConfiguresPrefixTransformsAndCatchAllMatch()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert
        var route = Assert.Single(sut.GetConfig().Routes);

        Assert.Equal("/api/catalog/{**catch-all}", route.Match.Path);
        Assert.Equal("catalog-cluster", route.ClusterId);

        // /api/catalog/api/v1/alimentos -> /api/v1/alimentos
        var transformRemovePrefix = Assert.Single(route.Transforms!, transform => transform.ContainsKey("PathRemovePrefix"));
        Assert.Equal("/api/catalog", transformRemovePrefix["PathRemovePrefix"]);

        var transformPrefix = Assert.Single(route.Transforms!, transform => transform.ContainsKey("PathPrefix"));
        Assert.Equal("/api", transformPrefix["PathPrefix"]);
    }

    [Fact]
    public async Task Refresh_WithSeveralHealthyInstances_AddsEveryInstanceAsDestination()
    {
        // Arrange
        SetupHealthyInstances(HealthyServiceName, ("catalog-1", "10.0.0.1", 8080), ("catalog-2", "10.0.0.2", 8080));
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert
        var cluster = Assert.Single(sut.GetConfig().Clusters);
        Assert.NotNull(cluster.Destinations);

        Assert.Equal(2, cluster.Destinations.Count);
        Assert.Equal("10.0.0.1:8080", cluster.Destinations["catalog-1"].Address);
        Assert.Equal("10.0.0.2:8080", cluster.Destinations["catalog-2"].Address);
    }

    [Fact]
    public async Task Refresh_WhenConsulFails_KeepsPreviousConfigAndLogsTheError()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // The constructor's immediate (TimeSpan.Zero) refresh is still in flight at this point.
        // If it lands after we capture the current config it would swap in a new snapshot and
        // make the identity assertion below flaky, so wait until the provider is quiescent -
        // bounded polling, not a fixed sleep. The next automatic tick is 30s away, so once
        // stable nothing else can move the config under us.
        await WaitForQuiescenceAsync(sut, PollTimeout);
        Assert.Single(sut.GetConfig().Routes);

        _catalogEndpointMock
            .Setup(endpoint => endpoint.Services())
            .ThrowsAsync(new ConsulRequestException { StatusCode = HttpStatusCode.InternalServerError });

        var configBeforeFailingRefresh = sut.GetConfig();

        // Act - a discovery outage must degrade the gateway, never crash it.
        await sut.RefreshAsync();

        // Assert
        Assert.Same(configBeforeFailingRefresh, sut.GetConfig());
        Assert.Single(sut.GetConfig().Routes);

        _loggerMock.Verify(
            logger => logger.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Refresh_WithNewConfig_SignalsChangeOnThePreviousSnapshot()
    {
        // Arrange
        using var sut = CreateSut("Development");
        var previousConfig = sut.GetConfig();

        // Act
        await sut.RefreshAsync();

        // Assert: YARP subscribes to the change token of the config instance it READ, so the
        // OLD token is the one that must be cancelled. Not doing this freezes the gateway on
        // whatever it discovered at startup.
        Assert.True(previousConfig.ChangeToken.HasChanged);
        Assert.False(sut.GetConfig().ChangeToken.HasChanged);
    }

    [Fact]
    public async Task Refresh_InDevelopment_AcceptsAnyServerCertificate()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert
        var cluster = Assert.Single(sut.GetConfig().Clusters);

        Assert.NotNull(cluster.HttpClient);
        Assert.True(cluster.HttpClient.DangerousAcceptAnyServerCertificate);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Refresh_OutsideDevelopment_LeavesHttpClientValidationUntouched(string environmentName)
    {
        // Arrange
        using var sut = CreateSut(environmentName);

        // Act
        await sut.RefreshAsync();

        // Assert: accepting any certificate outside Development would defeat TLS entirely.
        var cluster = Assert.Single(sut.GetConfig().Clusters);

        Assert.Null(cluster.HttpClient);
    }

    [Fact]
    public async Task Refresh_WithoutHealthyServices_PublishesEmptyConfig()
    {
        // Arrange
        SetupCatalog([]);
        SetupHealthyInstances(HealthyServiceName);
        SetupHealthyInstances(UnhealthyServiceName);
        using var sut = CreateSut("Development");

        // Act
        await sut.RefreshAsync();

        // Assert
        Assert.Empty(sut.GetConfig().Routes);
        Assert.Empty(sut.GetConfig().Clusters);
    }

    [Fact]
    public async Task Construction_RefreshesOnItsOwn_WithoutAManualTrigger()
    {
        // Arrange
        using var sut = CreateSut("Development");

        // Act - no RefreshAsync() call: this is the TimeSpan.Zero due time in the constructor
        // doing the work. Bounded polling (not a fixed sleep) keeps it from being flaky.
        await WaitUntilAsync(() => sut.GetConfig().Routes.Count > 0, PollTimeout);

        // Assert
        Assert.Equal("catalog-route", Assert.Single(sut.GetConfig().Routes).RouteId);
    }

    private ConsulYarpConfigProvider CreateSut(string environmentName)
    {
        var environmentMock = new Mock<IHostEnvironment>();
        environmentMock.SetupGet(environment => environment.EnvironmentName).Returns(environmentName);

        return new ConsulYarpConfigProvider(
            _loggerMock.Object,
            _consulClientMock.Object,
            environmentMock.Object);
    }

    private void SetupCatalog(string[] serviceNames)
    {
        // Consul returns the tags of each service as the dictionary value; the gateway only
        // cares about the keys.
        _catalogEndpointMock
            .Setup(endpoint => endpoint.Services())
            .ReturnsAsync(new QueryResult<Dictionary<string, string[]>>
            {
                Response = serviceNames.ToDictionary(name => name, _ => Array.Empty<string>())
            });
    }

    private void SetupHealthyInstances(string serviceName, params (string Id, string Address, int Port)[] instances)
    {
        _healthEndpointMock
            .Setup(endpoint => endpoint.Service(serviceName, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult<ServiceEntry[]>
            {
                Response = instances
                    .Select(instance => new ServiceEntry
                    {
                        Service = new AgentService
                        {
                            ID = instance.Id,
                            Service = instance.Id,
                            Address = instance.Address,
                            Port = instance.Port
                        }
                    })
                    .ToArray()
            });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Waits until the provider stops swapping configs: two consecutive reads, separated by a
    /// poll interval, return the same snapshot AND that snapshot is already populated.
    /// </summary>
    private static async Task WaitForQuiescenceAsync(ConsulYarpConfigProvider sut, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        IProxyConfig? previous = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);

            var current = sut.GetConfig();

            if (previous is not null && ReferenceEquals(previous, current) && current.Routes.Count > 0)
            {
                return;
            }

            previous = current;
        }

        throw new TimeoutException($"The provider did not settle within {timeout}.");
    }
}
