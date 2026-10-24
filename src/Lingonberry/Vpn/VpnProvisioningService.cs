using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingonberry.Data;
using Lingonberry.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lingonberry.Vpn;

/// <summary>
/// Everything the client (RPi) needs to render its own wg-quick config.
/// </summary>
public record DeviceClientConfig(
    string VpnIp,
    string ServerPublicKey,
    string EndpointHost,
    int ListenPort,
    string AllowedIps,
    int PersistentKeepalive);

/// <summary>
/// Provisions one WireGuard VPN per customer app:
/// - App creation: server key pair, subnet, UDP port, gateway container.
/// - Device add/remove/replace: gateway config is always re-rendered from
///   the database, so the gateway is a pure function of app + devices.
///
/// The gateway container runs with network_mode: host and manages the
/// WireGuard interface in the host network namespace.
/// </summary>
public class VpnProvisioningService(
    IServiceScopeFactory scopeFactory,
    DockerSocketClient docker,
    IOptions<VpnSettings> options,
    VpnProxyConfigProvider proxyConfig,
    DeviceStatusService deviceStatus,
    ILogger<VpnProvisioningService> logger)
{
    private readonly VpnSettings _settings = options.Value;

    // ─── App lifecycle ───────────────────────────────────────────────────

    public async Task<App> CreateAppAsync(string name, string domain)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();

        var usedSubnets = await db.Apps
            .Where(a => a.WgSubnet != "")
            .Select(a => a.WgSubnet)
            .ToListAsync();

        var (subnet, port) = AllocateNetwork(usedSubnets);
        var (privateKey, publicKey) = WireGuardKeyGenerator.Generate();

        var app = new App
        {
            Name = name,
            Domain = domain,
            Token = RandomNumberGenerator.GetHexString(32),
            WgSubnet = subnet,
            WgListenPort = port,
            WgServerPrivateKey = privateKey,
            WgServerPublicKey = publicKey
        };

        db.Apps.Add(app);
        await db.SaveChangesAsync();

        await WriteGatewayConfigAsync(app, app.Devices);
        await EnsureGatewayContainerAsync(app);

        logger.LogInformation("Provisioned VPN {Subnet} (UDP {Port}) for app {AppName}",
            subnet, port, name);
        await proxyConfig.ReloadAsync();
        return app;
    }

    public async Task<bool> RemoveAppAsync(Guid appId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();

        var app = await db.Apps
            .Include(a => a.Devices)
            .FirstOrDefaultAsync(a => a.Id == appId);
        if (app == null) return false;

        await TryRemoveContainerAsync(GatewayContainerName(app.Id));
        await RemoveArbiterContainerAsync(app.Id);
        DeleteConfigDir(app.Id);

        db.Apps.Remove(app);
        await db.SaveChangesAsync();

        foreach (var device in app.Devices)
        {
            deviceStatus.MarkDisconnected(device.Id);
        }

        logger.LogInformation("Deprovisioned VPN {Subnet} for app {AppName}", app.WgSubnet, app.Name);
        await proxyConfig.ReloadAsync();
        return true;
    }

    // ─── Device lifecycle (add / remove / replace) ───────────────────────

    public async Task<(Device Device, DeviceClientConfig Config)> RegisterDeviceAsync(
        Guid appId, string hostname, string wgPublicKey, string? version)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();

        var app = await db.Apps
            .Include(a => a.Devices)
            .FirstOrDefaultAsync(a => a.Id == appId)
            ?? throw new InvalidOperationException($"Unknown app {appId}");

        // Idempotent registration: re-registering with the same WireGuard
        // public key (re-run of setup script, restored SD card) keeps the
        // same device and VPN IP instead of allocating a new peer.
        var device = app.Devices.FirstOrDefault(d => d.WgPublicKey == wgPublicKey);
        if (device == null)
        {
            device = new Device
            {
                AppId = app.Id,
                Hostname = hostname,
                WgPublicKey = wgPublicKey,
                VpnIp = AllocateDeviceIp(app)
            };
            db.Devices.Add(device);
        }
        else
        {
            device.Hostname = hostname;
        }

        device.LastSeen = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(version)) device.CurrentVersion = version;
        await db.SaveChangesAsync();

        await WriteGatewayConfigAsync(app, app.Devices);
        await RestartGatewayAsync(app);

        // Two RPis in the same customer VPN need an arbiter to elect a
        // primary if one of them goes down.
        if (app.Devices.Count >= 2)
        {
            await EnsureArbiterContainerAsync(app);
        }

        logger.LogInformation("Registered device {Hostname} ({VpnIp}) in app {AppName}",
            hostname, device.VpnIp, app.Name);
        await proxyConfig.ReloadAsync();
        return (device, BuildClientConfig(app, device));
    }

    public async Task<bool> RemoveDeviceAsync(Guid appId, Guid deviceId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();

        var app = await db.Apps
            .Include(a => a.Devices)
            .FirstOrDefaultAsync(a => a.Id == appId);
        var device = app?.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (app == null || device == null) return false;

        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        deviceStatus.MarkDisconnected(device.Id);

        // Gateway config is re-rendered without the removed peer, which
        // revokes its access immediately. The VPN IP is freed for reuse.
        await WriteGatewayConfigAsync(app, app.Devices);
        await RestartGatewayAsync(app);

        // No arbiter needed below two devices.
        if (app.Devices.Count < 2)
        {
            await RemoveArbiterContainerAsync(app.Id);
        }

        logger.LogInformation("Removed device {Hostname} ({VpnIp}) from app {AppName}",
            device.Hostname, device.VpnIp, app.Name);
        await proxyConfig.ReloadAsync();
        return true;
    }

    // ─── Gateway config + container ─────────────────────────────────────

    private async Task WriteGatewayConfigAsync(App app, IEnumerable<Device> devices)
    {
        var dir = ConfigDir(app.Id);
        var iface = InterfaceName(app.Id);
        Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"Address = {GatewayIp(app.WgSubnet)}/24");
        sb.AppendLine($"ListenPort = {app.WgListenPort}");
        sb.AppendLine($"PrivateKey = {app.WgServerPrivateKey}");

        foreach (var device in devices.OrderBy(d => d.VpnIp))
        {
            sb.AppendLine();
            sb.AppendLine($"# {device.Hostname} ({device.VpnIp})");
            sb.AppendLine("[Peer]");
            sb.AppendLine($"PublicKey = {device.WgPublicKey}");
            sb.AppendLine($"AllowedIPs = {device.VpnIp}/32");
        }

        var path = Path.Combine(dir, $"{iface}.conf");
        await File.WriteAllTextAsync(path, sb.ToString());
        // Contains the server's private key - lock it down.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task EnsureGatewayContainerAsync(App app)
    {
        var name = GatewayContainerName(app.Id);

        // Remove any stale container so the gateway starts fresh from
        // the current config.
        await TryRemoveContainerAsync(name);

        var create = new
        {
            Image = _settings.GatewayImage,
            Env = new[] { $"IFACE={InterfaceName(app.Id)}" },
            HostConfig = new
            {
                NetworkMode = "host",
                Privileged = true,
                Binds = new[] { $"{HostConfigDir(app.Id)}:/config" },
                RestartPolicy = new { Name = "unless-stopped" }
            }
        };

        await docker.PostAsync($"/containers/create?name={name}", create);
        await docker.PostAsync($"/containers/{name}/start");
    }

    private async Task RestartGatewayAsync(App app)
    {
        var name = GatewayContainerName(app.Id);
        try
        {
            await docker.PostAsync($"/containers/{name}/restart?t=5");
        }
        catch (InvalidOperationException ex)
        {
            // Container missing (e.g. removed by hand) - recreate it.
            logger.LogWarning(ex, "Gateway container {Name} missing, recreating", name);
            await EnsureGatewayContainerAsync(app);
        }
    }

    // ─── Arbiter (needed when a customer has exactly two RPis) ───────────

    private string ArbiterContainerName(Guid appId) => $"lingon-arb-{ShortId(appId)}";

    private async Task EnsureArbiterContainerAsync(App app)
    {
        var name = ArbiterContainerName(app.Id);
        // The arbiter binds the app's gateway VPN IP - unique per app,
        // so port 27017 never collides between customers.
        var bindIp = GatewayIp(app.WgSubnet);

        try
        {
            var json = await docker.GetAsync($"/containers/{name}/json");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("State").GetProperty("Running").GetBoolean())
            {
                return; // already up
            }
            await TryRemoveContainerAsync(name);
        }
        catch (InvalidOperationException)
        {
            // not created yet
        }

        var create = new
        {
            Image = _settings.ArbiterImage,
            Cmd = new[]
            {
                "mongod", "--replSet", _settings.ArbiterReplicaSetName,
                "--bind_ip", bindIp, "--port", "27017"
            },
            HostConfig = new
            {
                NetworkMode = "host",
                RestartPolicy = new { Name = "unless-stopped" }
            }
        };

        try
        {
            await docker.PostAsync($"/containers/create?name={name}", create);
        }
        catch (InvalidOperationException ex)
        {
            // Most likely a missing image - pull it and try once more.
            logger.LogWarning(ex, "Arbiter create failed for {Name}, pulling {Image}",
                name, _settings.ArbiterImage);
            await docker.PullImageAsync(_settings.ArbiterImage);
            await docker.PostAsync($"/containers/create?name={name}", create);
        }

        await docker.PostAsync($"/containers/{name}/start");
        logger.LogInformation("Arbiter for app {AppName} listening on {BindIp}:27017",
            app.Name, bindIp);
    }

    private async Task RemoveArbiterContainerAsync(Guid appId)
    {
        await TryRemoveContainerAsync(ArbiterContainerName(appId));
    }

    private async Task TryRemoveContainerAsync(string name)
    {
        // Stop gracefully first so the entrypoint's TERM trap runs
        // wg-quick down and removes the host interface. Stopping a
        // "unless-stopped" container also stops the restart loop.
        try
        {
            await docker.PostAsync($"/containers/{name}/stop?t=10");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not stop container {Name}", name);
        }

        try
        {
            // force is required: the daemon refuses to remove a
            // restarting container otherwise.
            await docker.DeleteAsync($"/containers/{name}?v=1&force=1");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not remove container {Name}", name);
        }
    }

    private void DeleteConfigDir(Guid appId)
    {
        try
        {
            var dir = ConfigDir(appId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete config dir for app {AppId}", appId);
        }
    }

    // ─── Naming and allocation ───────────────────────────────────────────

    private string GatewayContainerName(Guid appId) => $"lingon-vpn-{ShortId(appId)}";
    private string InterfaceName(Guid appId) => $"wg-{ShortId(appId)}";
    private static string ShortId(Guid appId) => appId.ToString("N")[..8];

    private string ConfigDir(Guid appId) => Path.Combine(_settings.ConfigDir, ShortId(appId));
    private string HostConfigDir(Guid appId) => Path.Combine(_settings.HostConfigDir, ShortId(appId));

    private (string Subnet, int Port) AllocateNetwork(ICollection<string> usedSubnets)
    {
        for (var n = 1; n < 65_000; n++)
        {
            var subnet = $"10.{n >> 8}.{n & 255}.0/24";
            if (usedSubnets.Contains(subnet)) continue;
            return (subnet, _settings.ListenPortBase + n);
        }
        throw new InvalidOperationException("No free VPN subnets available");
    }

    private static string GatewayIp(string subnet) => subnet.Replace(".0/24", ".1");

    private static string AllocateDeviceIp(App app)
    {
        var used = app.Devices.Select(d => d.VpnIp).ToHashSet();
        var ipBase = app.WgSubnet[..^4]; // "10.0.1." from "10.0.1.0/24"
        for (var host = 2; host <= 254; host++)
        {
            var ip = $"{ipBase}{host}";
            if (!used.Contains(ip)) return ip;
        }
        throw new InvalidOperationException($"No free IPs in subnet {app.WgSubnet}");
    }

    private DeviceClientConfig BuildClientConfig(App app, Device device) =>
        new(
            device.VpnIp,
            app.WgServerPublicKey,
            _settings.PublicEndpointHost,
            app.WgListenPort,
            app.WgSubnet,
            25);
}
