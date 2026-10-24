namespace Lingonberry.Models;

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    public App App { get; set; } = null!;
    public string Hostname { get; set; } = "";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public string? CurrentVersion { get; set; }

    // WireGuard identity of this device within its app's VPN.
    public string WgPublicKey { get; set; } = "";
    public string VpnIp { get; set; } = "";
}
