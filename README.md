# Lingonberry

Self-hosted reverse proxy for apps deployed on customers' Raspberry Pi
devices (or any device that can run WireGuard + Docker).

Each customer ("app") gets its own WireGuard VPN with a dedicated subnet.
The proxy terminates TLS, forwards public HTTP traffic to the customer's
devices over the VPN (load balanced), and provides an admin API for
provisioning devices, sending commands and monitoring status. Devices
connect outward — no firewall openings on the customer side.

Originally built for (and used by) [Batalj](https://github.com/hsjolin/batalj),
but it is app-agnostic: point a device agent at the API and it works.

## How it works

```
                     Internet
                        │
              ┌─────────▼─────────┐
              │   Proxy host      │  docker compose:
              │  caddy (TLS)      │  caddy, lingon-proxy-app
              │  lingon-proxy-app │
              │   ├─ YARP         │──┐
              │   └─ provisioning │  │ one lingon-vpn-* gateway
              └───────────────────┘  │ container per app
                                        │
     Customer A ───── VPN A ────────────┤─────── VPN B ─── Customer B
     RPi A1 ⇄ RPi A2 (device-to-        │
     device traffic via the hub)        └── RPi B1
```

- **One WireGuard VPN per app**: dedicated `10.x.y.0/24` subnet, UDP port
  and server key pair. Gateway containers run with `network_mode: host`
  and create the interface `wg-<appId>` in the host netns. iptables rules
  isolate apps from each other.
- **YARP forwarding**: each app's domain routes to its devices'
  `http://<vpnIp>:<DeviceAppPort>` with round-robin load balancing and an
  active health check. Configuration is reloaded from the database
  whenever apps or devices change.
- **Arbiter**: when an app has exactly two devices, the proxy runs a
  per-app MongoDB arbiter container (`--replSet`), bound to the app's
  gateway VPN IP, so the devices can elect a primary if one goes down.
  The replica set name is configurable (`ArbiterReplicaSetName`).

## Device API

Agents on the devices authenticate with their app's Bearer token:

| Endpoint | Description |
|---|---|
| `POST /api/devices/register` | Self-registration; returns VPN IP + client config (everything needed to render a wg-quick config) |
| `GET /api/devices` | The app's devices, subnet, gateway IP, arbiter IP |
| `POST /api/device-status` | Periodic presence report |

Registration is idempotent per WireGuard public key: re-registering keeps
the device and its VPN IP (re-run of setup, restored SD card).

## Admin API (`/admin/api`, `X-Admin-Key` header)

| Endpoint | Description |
|---|---|
| `POST /apps` | Create app + provision VPN (keys, subnet, gateway container) |
| `GET /apps` | List apps |
| `DELETE /apps/{id}` | Decommission app (gateway, arbiter, config, DB row) |
| `GET /apps/{id}/devices` | Devices with VPN IP and connected status |
| `POST /apps/{id}/devices` | Register device (admin key **or** app Bearer token) |
| `DELETE /apps/{id}/devices/{deviceId}` | Revoke device (removes WireGuard peer, frees IP) |
| `POST /apps/{id}/devices/{deviceId}/command` | Send command to one device via VPN |
| `POST /apps/{id}/command` | Broadcast command to all devices |

The proxy probes each device's agent (`http://<vpnIp>:<AgentPort>/status`)
every 30 seconds to determine connected status and to deliver commands.

## Quick start

```bash
# 1. Build the per-app gateway image
docker compose --profile build build

# 2. Configure
cp .env.example .env
# edit .env: PUBLIC_ENDPOINT_HOST (the host devices use as their
# WireGuard endpoint), ADMIN_KEY and an absolute HOST_DATA_DIR

# 3. Run
docker compose up -d
```

The `Caddyfile` is an example — replace `example.com` with your domain
(Caddy handles Let's Encrypt automatically).

For development without Docker:

```bash
export ASPNETCORE_URLS=http://0.0.0.0:8080
export ADMIN_KEY=dev
export Vpn__PublicEndpointHost=$(hostname -I | awk '{print $1}')
export Vpn__ConfigDir=$PWD/data/wireguard
export Vpn__HostConfigDir=$PWD/data/wireguard
dotnet run --no-launch-profile --project src/Lingonberry
```

(`wireguard-tools` must be installed on the host in this mode — the
Docker image includes it.)

## Configuration

| Environment variable | Default | Description |
|---|---|---|
| `DATABASE_PATH` | `proxy.db` | SQLite database path |
| `ADMIN_KEY` | *(unset = open, dev only)* | Admin API key |
| `DOCKER_SOCKET` | `/var/run/docker.sock` | Docker API socket |
| `Vpn__ConfigDir` | `/data/wireguard` | Gateway configs, as seen by the app |
| `Vpn__HostConfigDir` | `/data/wireguard` | Same directory as seen by the Docker daemon (**absolute**) |
| `Vpn__GatewayImage` | `lingon-vpn-gateway:latest` | Gateway image |
| `Vpn__ArbiterImage` | `mongo:7` | Arbiter image |
| `Vpn__ArbiterReplicaSetName` | `batalj` | Replica set name for arbiters |
| `Vpn__PublicEndpointHost` | `vpn.example.com` | Host/IP devices use as WireGuard endpoint |
| `Vpn__ListenPortBase` | `51820` | First app's UDP port (each app gets base + n) |
| `Vpn__DeviceAppPort` | `7701` | App port on each device |
| `Vpn__AgentPort` | `7710` | Agent API port on each device |

## Notes

- The proxy needs Docker socket access — whoever holds `ADMIN_KEY`
  effectively controls the host.
- Open the apps' UDP ports (`ListenPortBase` + n) in the host firewall.
- WireGuard server private keys are stored in the SQLite database;
  gateway config files are written with `0600`.
