using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Roamwise.Core;

/// <summary>
/// Reads access point names from a UniFi Network controller through its official Integration API,
/// using an API key the user creates in UniFi Network (Settings › Control Plane › Integrations).
/// Read-only: Roamwise never changes anything on the controller.
/// </summary>
public sealed class UnifiController
{
    // UniFi OS consoles (UDM, UCG, Cloud Key Gen2+) serve the API under /proxy/network; self-hosted servers don't.
    private static readonly string[] BasePaths = ["/proxy/network/integration/v1", "/integration/v1"];

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;

    public UnifiController(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
    }

    public bool IsConfigured => _settings.UnifiHost.Length > 0 && _settings.UnifiApiKeyProtected.Length > 0;

    public static string Protect(string apiKey) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));

    /// <summary>Fetches devices and stores their names. Returns a sentence for the settings page.</summary>
    public async Task<(bool ok, string message)> SyncAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "Add the controller address and an API key first.");

        string key;
        try { key = Unprotect(_settings.UnifiApiKeyProtected); }
        catch (CryptographicException) { return (false, "The saved API key can't be read on this account. Paste it again."); }

        string host = _settings.UnifiHost;
        var origin = new Uri(host.Contains("://") ? host : "https://" + host);
        using var http = CreateClient(origin, key);

        try
        {
            foreach (string basePath in BasePaths)
            {
                using var sitesResponse = await http.GetAsync(new Uri(origin, basePath + "/sites?limit=200"), ct);
                if (sitesResponse.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                if (sitesResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    return (false, "The controller rejected the API key. Check that it's copied in full and still exists.");
                sitesResponse.EnsureSuccessStatusCode();

                var devices = new List<ControllerDevice>();
                foreach (string siteId in Ids(await sitesResponse.Content.ReadAsStringAsync(ct)))
                {
                    using var devResponse = await http.GetAsync(new Uri(origin, $"{basePath}/sites/{siteId}/devices?limit=200"), ct);
                    devResponse.EnsureSuccessStatusCode();
                    devices.AddRange(Devices(await devResponse.Content.ReadAsStringAsync(ct)));
                }

                _settings.SetControllerDevices(devices);
                var names = devices.Select(d => d.Name).Where(n => n.Length > 0).Take(4).ToList();
                string list = names.Count == 0 ? "" : $": {string.Join(", ", names)}{(devices.Count > names.Count ? ", …" : "")}";
                return (true, $"Connected. Found {devices.Count} {(devices.Count == 1 ? "device" : "devices")}{list}.");
            }
            return (false, "That address doesn't look like a UniFi Network controller (no Integration API found).");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Couldn't reach {origin.Host}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"{origin.Host} didn't answer in time.");
        }
        catch (JsonException)
        {
            return (false, "The controller answered with something Roamwise couldn't read.");
        }
    }

    /// <summary>Background refresh; quiet unless something breaks.</summary>
    public async Task RefreshIfStaleAsync(TimeSpan maxAge)
    {
        if (!IsConfigured || _settings.ControllerSyncedAt is DateTime t && DateTime.Now - t < maxAge) return;
        var (ok, message) = await SyncAsync();
        if (!ok) _log.Add(ActivityKind.Problem, $"UniFi names: {message}");
    }

    private static HttpClient CreateClient(Uri origin, string key)
    {
        var handler = new HttpClientHandler
        {
            // Local UniFi consoles use self-signed certificates. Accept them for the address the user entered only.
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || string.Equals(request.RequestUri?.Host, origin.Host, StringComparison.OrdinalIgnoreCase),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-API-KEY", key);
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        return http;
    }

    private static IEnumerable<string> Ids(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var site in Items(doc.RootElement))
            if (site.TryGetProperty("id", out var id) && id.GetString() is string s) yield return s;
    }

    private static List<ControllerDevice> Devices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<ControllerDevice>();
        foreach (var d in Items(doc.RootElement))
        {
            string? mac = Str(d, "macAddress") ?? Str(d, "mac");
            if (mac is null) continue;
            result.Add(new ControllerDevice(mac, Str(d, "name") ?? "", Str(d, "model")));
        }
        return result;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
        : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray()
        : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
