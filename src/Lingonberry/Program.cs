using Lingonberry.Data;
using Lingonberry.Vpn;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "proxy.db";
builder.Services.AddDbContext<ProxyDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));

// YARP: public traffic is forwarded to the app's RPis over the VPN.
builder.Services.AddSingleton<DockerSocketClient>();
builder.Services.AddSingleton<VpnProxyConfigProvider>();
builder.Services.AddSingleton<DeviceStatusService>();
builder.Services.AddHostedService<DeviceStatusProber>();
builder.Services.AddSingleton<VpnProvisioningService>();
builder.Services.Configure<VpnSettings>(builder.Configuration.GetSection("Vpn"));
builder.Services.AddReverseProxy()
    .Services.AddSingleton<IProxyConfigProvider>(
        sp => sp.GetRequiredService<VpnProxyConfigProvider>());

var app = builder.Build();

// Ensure DB is created and YARP is configured from it on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
    db.Database.EnsureCreated();
}
await app.Services.GetRequiredService<VpnProxyConfigProvider>().ReloadAsync();

// ─── Admin API ─────────────────────────────────────────────────────────────
var admin = app.MapGroup("/admin/api").AddEndpointFilter(AdminAuthFilter);

admin.MapGet("/apps", async (ProxyDbContext db) =>
    await db.Apps.Select(a => new
    {
        a.Id, a.Name, a.Domain, a.CreatedAt,
        a.WgSubnet, a.WgListenPort, a.WgServerPublicKey
    }).ToListAsync());

admin.MapPost("/apps", async (CreateAppRequest req, VpnProvisioningService vpn) =>
{
    try
    {
        var created = await vpn.CreateAppAsync(req.Name, req.Domain);
        return Results.Ok(new
        {
            created.Id, created.Name, created.Domain, created.Token, created.CreatedAt,
            created.WgSubnet, created.WgListenPort, created.WgServerPublicKey
        });
    }
    catch (DbUpdateException)
    {
        return Results.Json(new { error = "An app with this domain already exists" }, statusCode: 409);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

admin.MapDelete("/apps/{id:guid}", async (Guid id, VpnProvisioningService vpn) =>
{
    var removed = await vpn.RemoveAppAsync(id);
    return removed ? Results.NoContent() : Results.NotFound();
});

admin.MapGet("/apps/{id:guid}/devices", async (Guid id, ProxyDbContext db, DeviceStatusService deviceStatus) =>
{
    var devices = await db.Devices
        .Where(d => d.AppId == id)
        .Select(d => new
        {
            d.Id, d.Hostname, d.LastSeen, d.CurrentVersion, d.VpnIp,
            Connected = deviceStatus.IsConnected(d.Id)
        })
        .ToListAsync();

    return Results.Ok(devices);
});

// Register a device (RPi) in an app's VPN. Called by the setup script /
// admin UI. Authorized either by the admin key or by the app's token.
admin.MapPost("/apps/{appId:guid}/devices",
    async (Guid appId, RegisterDeviceRequest req, HttpContext ctx, ProxyDbContext db, VpnProvisioningService vpn) =>
    {
        var app = await db.Apps.FindAsync(appId);
        if (app == null) return Results.NotFound();

        if (!IsAdminAuthorized(ctx) && !HasAppToken(ctx, app.Token))
        {
            return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
        }

        if (string.IsNullOrWhiteSpace(req.Hostname) || string.IsNullOrWhiteSpace(req.WgPublicKey))
        {
            return Results.Json(new { error = "Hostname and WgPublicKey are required" }, statusCode: 400);
        }

        // WireGuard public keys are base64-encoded 32-byte values.
        try
        {
            if (Convert.FromBase64String(req.WgPublicKey).Length != 32)
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            return Results.Json(new { error = "WgPublicKey is not a valid WireGuard public key" }, statusCode: 400);
        }

        try
        {
            var (device, clientConfig) = await vpn.RegisterDeviceAsync(
                appId, req.Hostname, req.WgPublicKey, req.Version);
            return Results.Ok(new { device.Id, device.Hostname, device.VpnIp, ClientConfig = clientConfig });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    });

// Remove a device: revokes its WireGuard peer and frees its VPN IP.
admin.MapDelete("/apps/{appId:guid}/devices/{deviceId:guid}",
    async (Guid appId, Guid deviceId, HttpContext ctx, ProxyDbContext db, VpnProvisioningService vpn) =>
    {
        var app = await db.Apps.FindAsync(appId);
        if (app == null) return Results.NotFound();

        if (!IsAdminAuthorized(ctx) && !HasAppToken(ctx, app.Token))
        {
            return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
        }

        var removed = await vpn.RemoveDeviceAsync(appId, deviceId);
        return removed ? Results.NoContent() : Results.NotFound();
    });

// Commands are sent to the device's agent directly over the VPN.
admin.MapPost("/apps/{appId:guid}/devices/{deviceId:guid}/command",
    async (Guid appId, Guid deviceId, SendCommandRequest req, ProxyDbContext db, DeviceStatusService deviceStatus) =>
    {
        var target = await db.Devices
            .Where(d => d.Id == deviceId && d.AppId == appId)
            .Select(d => new { d.VpnIp, AppToken = d.App.Token })
            .FirstOrDefaultAsync();
        if (target == null) return Results.NotFound();

        var (ok, result) = await deviceStatus.SendCommandAsync(
            target.AppToken, target.VpnIp, req.Command);
        return ok
            ? Results.Text(result, "application/json")
            : Results.Problem(result, statusCode: 502);
    });

admin.MapPost("/apps/{appId:guid}/command",
    async (Guid appId, SendCommandRequest req, ProxyDbContext db, DeviceStatusService deviceStatus) =>
    {
        var targets = await db.Devices
            .Where(d => d.AppId == appId)
            .Select(d => new { d.Hostname, d.VpnIp, AppToken = d.App.Token })
            .ToListAsync();

        var results = new Dictionary<string, string>();
        foreach (var target in targets)
        {
            var (ok, result) = await deviceStatus.SendCommandAsync(
                target.AppToken, target.VpnIp, req.Command);
            results[target.Hostname] = ok ? "sent" : $"failed: {result}";
        }

        return Results.Ok(results);
    });

// ─── Device API (called by rpi-agents over the VPN/public internet) ────────

// Self-registration: an agent that boots without a WireGuard config
// registers itself and receives everything needed to render its wg0.conf.
app.MapPost("/api/devices/register", async (RegisterDeviceRequest req, HttpContext ctx, ProxyDbContext db, VpnProvisioningService vpn) =>
{
    var token = ParseBearerToken(ctx);
    var app = token == null
        ? null
        : await db.Apps.FirstOrDefaultAsync(a => a.Token == token);
    if (app == null)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    }

    if (string.IsNullOrWhiteSpace(req.Hostname) || string.IsNullOrWhiteSpace(req.WgPublicKey))
    {
        return Results.Json(new { error = "Hostname and WgPublicKey are required" }, statusCode: 400);
    }

    try
    {
        if (Convert.FromBase64String(req.WgPublicKey).Length != 32)
        {
            throw new FormatException();
        }
    }
    catch (FormatException)
    {
        return Results.Json(new { error = "WgPublicKey is not a valid WireGuard public key" }, statusCode: 400);
    }

    try
    {
        var (device, clientConfig) = await vpn.RegisterDeviceAsync(
            app.Id, req.Hostname, req.WgPublicKey, req.Version);
        return Results.Ok(new { device.Id, device.Hostname, device.VpnIp, ClientConfig = clientConfig });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// Peer list: used by the agents to configure the MongoDB replica set.
app.MapGet("/api/devices", async (HttpContext ctx, ProxyDbContext db) =>
{
    var token = ParseBearerToken(ctx);
    var app = token == null
        ? null
        : await db.Apps.Include(a => a.Devices).FirstOrDefaultAsync(a => a.Token == token);
    if (app == null)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    }

    // Gateway = hub VPN IP; the arbiter (if any) listens on it.
    var gatewayIp = app.WgSubnet.Replace(".0/24", ".1");

    return Results.Ok(new
    {
        app.WgSubnet,
        GatewayIp = gatewayIp,
        ArbiterVpnIp = app.Devices.Count >= 2 ? gatewayIp : null,
        Devices = app.Devices
            .OrderBy(d => d.VpnIp)
            .Select(d => new { d.Hostname, d.VpnIp, d.WgPublicKey })
    });
});

// ─── Device status ingest (called by rpi-agents) ────────────────────────────
app.MapPost("/api/device-status", async (DeviceStatusReport report, HttpContext ctx, ProxyDbContext db) =>
{
    var token = ParseBearerToken(ctx);
    if (token == null)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    }

    var app = await db.Apps.FirstOrDefaultAsync(a => a.Token == token);
    if (app == null)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    }

    var device = !string.IsNullOrEmpty(report.WgPublicKey)
        ? await db.Devices.FirstOrDefaultAsync(d =>
            d.AppId == app.Id && d.WgPublicKey == report.WgPublicKey)
        : await db.Devices.FirstOrDefaultAsync(d =>
            d.AppId == app.Id && d.Hostname == report.Hostname);
    if (device == null)
    {
        return Results.Json(new { error = "Unknown device" }, statusCode: 404);
    }

    device.LastSeen = DateTime.UtcNow;
    if (!string.IsNullOrEmpty(report.Version)) device.CurrentVersion = report.Version;
    if (!string.IsNullOrEmpty(report.Hostname)) device.Hostname = report.Hostname;
    await db.SaveChangesAsync();

    return Results.Ok();
});

// ─── Public forwarding to RPis (YARP) ──────────────────────────────────────
app.MapReverseProxy();

app.Run();

// ─── Helpers ───────────────────────────────────────────────────────────────
static string? ParseBearerToken(HttpContext ctx)
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return auth["Bearer ".Length..].Trim();
    return null;
}

static bool IsAdminAuthorized(HttpContext ctx)
{
    var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
    if (string.IsNullOrEmpty(adminKey)) return true; // No key configured = open (dev mode)
    return ctx.Request.Headers["X-Admin-Key"].ToString() == adminKey;
}

static bool HasAppToken(HttpContext ctx, string appToken) =>
    ctx.Request.Headers.Authorization.ToString() == $"Bearer {appToken}";

static async ValueTask<object?> AdminAuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    if (IsAdminAuthorized(ctx.HttpContext))
    {
        return await next(ctx);
    }

    // Device endpoints under /apps/{appId}/devices accept the app token.
    var method = ctx.HttpContext.Request.Method;
    var path = ctx.HttpContext.Request.Path.ToString();
    if ((method == "POST" && path.StartsWith("/admin/api/apps") && path.EndsWith("/devices")) ||
        (method == "DELETE" && path.StartsWith("/admin/api/apps") && path.Contains("/devices/")))
    {
        return await next(ctx);
    }

    return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
}

record CreateAppRequest(string Name, string Domain);
record RegisterDeviceRequest(string Hostname, string WgPublicKey, string? Version);
record SendCommandRequest(string Command);
record DeviceStatusReport(string Hostname, string? WgPublicKey, string? Version);
