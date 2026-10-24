namespace Lingonberry.Models;

public class App
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // WireGuard network for this app's devices. One app = one VPN,
    // isolated from other apps' VPNs.
    public string WgSubnet { get; set; } = "";
    public int WgListenPort { get; set; }
    public string WgServerPrivateKey { get; set; } = "";
    public string WgServerPublicKey { get; set; } = "";

    public ICollection<Device> Devices { get; set; } = [];
}
