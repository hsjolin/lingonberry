using Lingonberry.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;

namespace Lingonberry.Vpn;

/// <summary>
/// YARP configuration backed by the database: one cluster per app
/// (destinations = the app's devices, load balanced round-robin with
/// active health checks) and one route per app matched on its domain.
/// Reloaded by VpnProvisioningService whenever apps or devices change.
/// </summary>
public class VpnProxyConfigProvider(
    IServiceScopeFactory scopeFactory,
    IOptions<VpnSettings> options) : IProxyConfigProvider
{
    private readonly VpnSettings _settings = options.Value;
    private volatile Snapshot _snapshot = new()
    {
        Routes = [],
        Clusters = [],
        ChangeToken = new CancellationChangeToken(CancellationToken.None)
    };
    private volatile CancellationTokenSource _cts = new();

    public IProxyConfig GetConfig() => _snapshot;

    public IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

    public async Task ReloadAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        var apps = await db.Apps.Include(a => a.Devices).ToListAsync();

        var clusters = new List<ClusterConfig>();
        var routes = new List<RouteConfig>();

        foreach (var app in apps.Where(a =>
                     !string.IsNullOrEmpty(a.Domain) && a.Devices.Count > 0))
        {
            var clusterId = app.Id.ToString();

            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                LoadBalancingPolicy = LoadBalancingPolicies.RoundRobin,
                Destinations = app.Devices.ToDictionary(
                    d => d.Id.ToString(),
                    d => new DestinationConfig
                    {
                        Address = $"http://{d.VpnIp}:{_settings.DeviceAppPort}"
                    }),
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = TimeSpan.FromSeconds(30),
                        Timeout = TimeSpan.FromSeconds(3),
                        Path = "/api/v1/health"
                    }
                }
            });

            routes.Add(new RouteConfig
            {
                RouteId = $"app-{clusterId}",
                ClusterId = clusterId,
                // Admin API and device-status endpoints (default order) must
                // win over these host-matched catch-all routes.
                Order = 100,
                Match = new RouteMatch { Hosts = new[] { app.Domain } }
            });
        }

        var newCts = new CancellationTokenSource();
        var oldCts = _cts;
        _snapshot = new Snapshot
        {
            Routes = routes,
            Clusters = clusters,
            ChangeToken = new CancellationChangeToken(newCts.Token)
        };
        _cts = newCts;
        oldCts.Cancel();
    }

    private sealed class Snapshot : IProxyConfig
    {
        public required IReadOnlyList<RouteConfig> Routes { get; init; }
        public required IReadOnlyList<ClusterConfig> Clusters { get; init; }
        public required IChangeToken ChangeToken { get; init; }
    }
}
