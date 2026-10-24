using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Lingonberry.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lingonberry.Vpn;

/// <summary>
/// Talks to rpi-agents directly over the VPN via their HTTP API, and keeps a
/// cached view of which devices are reachable. Replaces the old WebSocket
/// tunnel: the agent is now the server, the proxy is the client.
/// </summary>
public class DeviceStatusService(
    IServiceScopeFactory scopeFactory,
    IOptions<VpnSettings> options,
    ILogger<DeviceStatusService> logger)
{
    private readonly VpnSettings _settings = options.Value;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<Guid, bool> _connected = new();

    public bool IsConnected(Guid deviceId) =>
        _connected.TryGetValue(deviceId, out var connected) && connected;

    public void MarkDisconnected(Guid deviceId) =>
        _connected.TryRemove(deviceId, out _);

    /// <summary>
    /// Probes every device's agent; called periodically by
    /// DeviceStatusProber. Updates LastSeen for reachable devices.
    /// </summary>
    public async Task ProbeAllAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();

        var devices = await db.Devices
            .Select(d => new { d.Id, d.VpnIp, AppToken = d.App.Token })
            .ToListAsync();

        foreach (var device in devices)
        {
            var connected = await ProbeAsync(device.VpnIp, device.AppToken);
            _connected[device.Id] = connected;

            if (connected)
            {
                var entity = await db.Devices.FindAsync(device.Id);
                if (entity != null) entity.LastSeen = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task<bool> ProbeAsync(string vpnIp, string appToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"http://{vpnIp}:{_settings.AgentPort}/status");
            request.Headers.Add("X-App-Token", appToken);
            using var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Agent probe of {VpnIp} failed", vpnIp);
            return false;
        }
    }

    /// <summary>
    /// Sends a command to a device's agent over the VPN.
    /// Returns the agent's raw JSON response.
    /// </summary>
    public async Task<(bool Ok, string Result)> SendCommandAsync(
        string appToken, string vpnIp, string command)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"http://{vpnIp}:{_settings.AgentPort}/command")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { command }),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Add("X-App-Token", appToken);

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Command to agent {VpnIp} failed", vpnIp);
            return (false, ex.Message);
        }
    }
}

/// <summary>Probes all device agents every 30 seconds.</summary>
public class DeviceStatusProber(DeviceStatusService status, ILogger<DeviceStatusProber> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await status.ProbeAllAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Device probe round failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
