namespace Lingonberry.Vpn;

public class VpnSettings
{
    /// <summary>Directory where gateway configs are written, as seen by proxy-app.</summary>
    public string ConfigDir { get; set; } = "/data/wireguard";

    /// <summary>
    /// Same directory as seen by the Docker daemon (host side). Needed because
    /// proxy-app runs in a container while gateway bind-mounts use host paths.
    /// </summary>
    public string HostConfigDir { get; set; } = "/data/wireguard";

    /// <summary>Gateway image started per customer app.</summary>
    public string GatewayImage { get; set; } = "lingon-vpn-gateway:latest";

    /// <summary>Public host/IP that RPis use as their WireGuard endpoint.</summary>
    public string PublicEndpointHost { get; set; } = "vpn.example.com";

    /// <summary>UDP port for the first app's gateway; each app gets base + N.</summary>
    public int ListenPortBase { get; set; } = 51820;

    /// <summary>Port the customer app listens on on the device.</summary>
    public int DeviceAppPort { get; set; } = 7701;

    /// <summary>Port the rpi-agent HTTP API listens on inside an RPi.</summary>
    public int AgentPort { get; set; } = 7710;

    /// <summary>
    /// MongoDB image used for the per-app arbiter, provisioned when a
    /// customer has exactly two RPis (to avoid split-brain).
    /// </summary>
    public string ArbiterImage { get; set; } = "mongo:7";

    /// <summary>
    /// Replica set name the arbiters run with. Must match what the
    /// customer's devices configure (e.g. "batalj" for a Batalj app).
    /// </summary>
    public string ArbiterReplicaSetName { get; set; } = "batalj";
}
