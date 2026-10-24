using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lingonberry.Vpn;

/// <summary>
/// Minimal Docker Engine API client over the docker unix socket.
/// Only the handful of operations needed to manage per-app gateway
/// containers: create, start, restart, remove.
/// </summary>
public class DockerSocketClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _apiVersion;

    public DockerSocketClient()
    {
        var socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET")
            ?? "/var/run/docker.sock";

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/") };
    }

    public Task<string> PostAsync(string path, object? body = null) =>
        SendAsync(HttpMethod.Post, path, body);

    public Task<string> GetAsync(string path) =>
        SendAsync(HttpMethod.Get, path);

    public Task<string> DeleteAsync(string path) =>
        SendAsync(HttpMethod.Delete, path);

    /// <summary>
    /// Pulls an image. The request streams until the pull completes,
    /// so this blocks for the duration of the download.
    /// </summary>
    public Task PullImageAsync(string image) =>
        SendAsync(HttpMethod.Post, $"/images/create?fromImage={Uri.EscapeDataString(image)}");

    private async Task<string> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var version = await GetApiVersionAsync();
        using var request = new HttpRequestMessage(method, $"/v{version}{path}");
        if (body != null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Docker API {method} {path} failed ({(int)response.StatusCode}): {json}");
        }

        return json;
    }

    private async Task<string> GetApiVersionAsync()
    {
        if (_apiVersion != null) return _apiVersion;

        using var response = await _http.GetAsync("/version");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _apiVersion = doc.RootElement.GetProperty("ApiVersion").GetString() ?? "1.44";
        return _apiVersion;
    }

    public void Dispose() => _http.Dispose();
}
