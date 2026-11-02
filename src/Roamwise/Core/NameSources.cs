using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>What each name source needs from the user, for the settings page.</summary>
public sealed record NameSourceInfo(
    NameSourceKind Kind, string Title,
    string? AddressLabel, string? AddressHint,
    string? UserLabel, string? SecretLabel, string? ExtraLabel, string? ExtraHint,
    string Help);

/// <summary>
/// Pulls access point names from the systems that know them: wireless controllers and cloud dashboards
/// (read-only, with credentials the user creates), or a list exported from anything else.
/// </summary>
public sealed partial class NameSources
{
    public static readonly IReadOnlyList<NameSourceInfo> All =
    [
        new(NameSourceKind.None, "Don't look up names", null, null, null, null, null, null,
            "Roamwise uses the names you type, then any name an access point announces about itself."),
        new(NameSourceKind.Unifi, "Ubiquiti UniFi", "Controller address", "192.168.1.1", null, "API key", null, null,
            "In UniFi Network, open Settings, then Control Plane, then Integrations, and create an API key."),
        new(NameSourceKind.Omada, "TP-Link Omada", "Controller address", "https://192.168.0.2:8043", "Client ID", "Client secret", "Omada ID", "From the Open API page",
            "In the Omada controller, open Global view, then Settings, then Platform Integration, and add an Open API application in Client mode. Its page shows the client ID, client secret and Omada ID."),
        new(NameSourceKind.Meraki, "Cisco Meraki", null, null, null, "API key", "Organization ID", "Optional",
            "In the Meraki dashboard, open your profile and generate an API key. Leave the organization empty to read all of them."),
        new(NameSourceKind.Mist, "Juniper Mist", "Cloud", "api.mist.com", null, "API token", "Organization ID", "Optional",
            "In the Mist portal, open My Account and create an API token. Use api.eu.mist.com, api.gc1.mist.com and so on if your org lives in another region."),
        new(NameSourceKind.Ruckus, "Ruckus SmartZone", "Controller address", "https://192.168.1.10:8443", "User name", "Password", null, null,
            "Use a read-only SmartZone or virtual SmartZone administrator account."),
        new(NameSourceKind.File, "Import a list (any brand)", "File", "Choose a .csv or .txt file", null, null, null, null,
            "Export the device list from any controller or app (Aruba, Omada, eero, Netgear, Ruckus Unleashed, OpenWrt…) or type one yourself. Each line needs a MAC address and a name, for example: 1c:0b:8b:52:27:63, Living room."),
    ];

    public static NameSourceInfo Info(NameSourceKind kind) => All.First(i => i.Kind == kind);

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;

    public NameSources(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
    }

    public bool IsConfigured => _settings.NameSource switch
    {
        NameSourceKind.None => false,
        NameSourceKind.File => _settings.ControllerAddress.Length > 0,
        NameSourceKind.Meraki => _settings.ControllerSecretProtected.Length > 0,
        _ => _settings.ControllerSecretProtected.Length > 0 && (_settings.ControllerAddress.Length > 0 || _settings.NameSource == NameSourceKind.Mist),
    };

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Trim()), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));

    /// <summary>Fetches the device list and stores it. Returns a sentence for the settings page.</summary>
    public async Task<(bool ok, string message)> SyncAsync(CancellationToken ct = default)
    {
        if (_settings.NameSource == NameSourceKind.None)
        {
            _settings.SetNamedDevices([]);
            return (true, "Not looking up names.");
        }
        if (!IsConfigured) return (false, "Fill in the fields above first.");

        string secret = "";
        if (_settings.ControllerSecretProtected.Length > 0)
        {
            try { secret = Unprotect(_settings.ControllerSecretProtected); }
            catch (CryptographicException) { return (false, "The saved key can't be read on this Windows account. Enter it again."); }
        }

        try
        {
            var devices = _settings.NameSource switch
            {
                NameSourceKind.Unifi => await UnifiAsync(secret, ct),
                NameSourceKind.Omada => await OmadaAsync(secret, ct),
                NameSourceKind.Meraki => await MerakiAsync(secret, ct),
                NameSourceKind.Mist => await MistAsync(secret, ct),
                NameSourceKind.Ruckus => await RuckusAsync(secret, ct),
                NameSourceKind.File => await ImportFileAsync(ct),
                _ => [],
            };

            _settings.SetNamedDevices(devices);
            var names = devices.Select(d => d.Name).Where(n => n.Length > 0).Distinct().Take(4).ToList();
            string list = names.Count == 0 ? "" : $": {string.Join(", ", names)}{(devices.Count > names.Count ? ", …" : "")}";
            return devices.Count == 0
                ? (false, "Connected, but no devices came back.")
                : (true, $"Found {devices.Count} {(devices.Count == 1 ? "device" : "devices")}{list}.");
        }
        catch (NameSourceException ex) { return (false, ex.Message); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, "The key or password was rejected. Check that it's complete and still valid.");
        }
        catch (HttpRequestException ex) { return (false, $"Couldn't connect: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (false, "The server didn't answer in time."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (false, "The server answered with something Roamwise couldn't read.");
        }
        catch (IOException ex) { return (false, $"Couldn't read the file: {ex.Message}"); }
    }

    /// <summary>Background refresh; quiet unless something breaks.</summary>
    public async Task RefreshIfStaleAsync(TimeSpan maxAge)
    {
        if (!IsConfigured || _settings.NamedDevices.Count > 0 && _settings.ControllerSyncedAt is DateTime t && DateTime.Now - t < maxAge) return;
        var (ok, message) = await SyncAsync();
        if (!ok) _log.Add(ActivityKind.Problem, $"Access point names: {message}");
    }

    private sealed class NameSourceException(string message) : Exception(message);

    // ---- Ubiquiti UniFi: Network Integration API ----

    private async Task<List<ControllerDevice>> UnifiAsync(string key, CancellationToken ct)
    {
        var origin = Origin(_settings.ControllerAddress);
        using var http = Client(origin, ("X-API-KEY", key));
        // UniFi OS consoles serve the API under /proxy/network; self-hosted Network servers don't.
        foreach (string basePath in new[] { "/proxy/network/integration/v1", "/integration/v1" })
        {
            using var sites = await http.GetAsync(new Uri(origin, basePath + "/sites?limit=200"), ct);
            if (sites.StatusCode == HttpStatusCode.NotFound) continue;
            sites.EnsureSuccessStatusCode();

            var devices = new List<ControllerDevice>();
            foreach (var site in Items(await sites.Content.ReadFromJsonAsync<JsonElement>(ct)))
            {
                var list = await http.GetFromJsonAsync<JsonElement>(new Uri(origin, $"{basePath}/sites/{Str(site, "id")}/devices?limit=200"), ct);
                devices.AddRange(Items(list).Select(d => Device(d, "macAddress", "name", "model", "Ubiquiti")).OfType<ControllerDevice>());
            }
            return devices;
        }
        throw new NameSourceException("That address doesn't look like a UniFi Network controller.");
    }

    // ---- TP-Link Omada: Open API (client credentials) ----

    private async Task<List<ControllerDevice>> OmadaAsync(string clientSecret, CancellationToken ct)
    {
        var origin = Origin(_settings.ControllerAddress);
        string omadacId = _settings.ControllerExtra;
        if (omadacId.Length == 0) throw new NameSourceException("Add the Omada ID from the controller's Open API page.");
        using var http = Client(origin);

        var tokenBody = new { omadacId, client_id = _settings.ControllerUser, client_secret = clientSecret };
        using var tokenResponse = await http.PostAsJsonAsync(new Uri(origin, "/openapi/authorize/token?grant_type=client_credentials"), tokenBody, ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (token.TryGetProperty("errorCode", out var code) && code.GetInt32() != 0)
            throw new NameSourceException($"Omada refused the client ID or secret: {Str(token, "msg")}");
        string accessToken = Str(token.GetProperty("result"), "accessToken") ?? throw new NameSourceException("Omada didn't return a token.");
        http.DefaultRequestHeaders.Add("Authorization", $"AccessToken={accessToken}");

        var sites = await http.GetFromJsonAsync<JsonElement>(new Uri(origin, $"/openapi/v1/{omadacId}/sites?page=1&pageSize=100"), ct);
        var devices = new List<ControllerDevice>();
        foreach (var site in OmadaItems(sites))
        {
            var list = await http.GetFromJsonAsync<JsonElement>(
                new Uri(origin, $"/openapi/v1/{omadacId}/sites/{Str(site, "siteId")}/devices?page=1&pageSize=1000"), ct);
            devices.AddRange(OmadaItems(list).Select(d => Device(d, "mac", "name", "model", "TP-Link")).OfType<ControllerDevice>());
        }
        return devices;

        static IEnumerable<JsonElement> OmadaItems(JsonElement e) =>
            e.TryGetProperty("result", out var r) && r.TryGetProperty("data", out var data) ? data.EnumerateArray() : [];
    }

    // ---- Cisco Meraki: Dashboard API ----

    private async Task<List<ControllerDevice>> MerakiAsync(string key, CancellationToken ct)
    {
        var origin = new Uri("https://api.meraki.com");
        using var http = Client(origin, ("Authorization", $"Bearer {key}"));
        var orgIds = _settings.ControllerExtra.Length > 0
            ? [_settings.ControllerExtra]
            : Items(await http.GetFromJsonAsync<JsonElement>(new Uri(origin, "/api/v1/organizations"), ct)).Select(o => Str(o, "id")!).ToList();

        var devices = new List<ControllerDevice>();
        foreach (string org in orgIds)
        {
            var list = await http.GetFromJsonAsync<JsonElement>(
                new Uri(origin, $"/api/v1/organizations/{org}/devices?perPage=1000&productTypes[]=wireless"), ct);
            devices.AddRange(Items(list).Select(d => Device(d, "mac", "name", "model", "Cisco Meraki")).OfType<ControllerDevice>());
        }
        return devices;
    }

    // ---- Juniper Mist: org inventory ----

    private async Task<List<ControllerDevice>> MistAsync(string token, CancellationToken ct)
    {
        var origin = Origin(_settings.ControllerAddress.Length > 0 ? _settings.ControllerAddress : "api.mist.com");
        using var http = Client(origin, ("Authorization", $"Token {token}"));
        var orgIds = new List<string>();
        if (_settings.ControllerExtra.Length > 0) orgIds.Add(_settings.ControllerExtra);
        else
        {
            var self = await http.GetFromJsonAsync<JsonElement>(new Uri(origin, "/api/v1/self"), ct);
            if (self.TryGetProperty("privileges", out var privileges))
                orgIds.AddRange(privileges.EnumerateArray().Select(p => Str(p, "org_id")).OfType<string>().Distinct());
        }
        if (orgIds.Count == 0) throw new NameSourceException("This token can't see any organization. Add the organization ID.");

        var devices = new List<ControllerDevice>();
        foreach (string org in orgIds)
        {
            var list = await http.GetFromJsonAsync<JsonElement>(new Uri(origin, $"/api/v1/orgs/{org}/inventory?type=ap&limit=1000"), ct);
            devices.AddRange(Items(list).Select(d => Device(d, "mac", "name", "model", "Juniper Mist")).OfType<ControllerDevice>());
        }
        return devices;
    }

    // ---- Ruckus SmartZone: public REST API ----

    private async Task<List<ControllerDevice>> RuckusAsync(string password, CancellationToken ct)
    {
        var origin = Origin(_settings.ControllerAddress);
        using var http = Client(origin);
        foreach (string version in new[] { "v11_1", "v10_0", "v9_1", "v8_0" })
        {
            using var login = await http.PostAsJsonAsync(new Uri(origin, $"/wsg/api/public/{version}/serviceTicket"),
                new { username = _settings.ControllerUser, password }, ct);
            if (login.StatusCode == HttpStatusCode.NotFound) continue;
            login.EnsureSuccessStatusCode();
            string ticket = Str(await login.Content.ReadFromJsonAsync<JsonElement>(ct), "serviceTicket")
                            ?? throw new NameSourceException("SmartZone didn't return a session.");

            var list = await http.GetFromJsonAsync<JsonElement>(
                new Uri(origin, $"/wsg/api/public/{version}/aps?listSize=1000&serviceTicket={Uri.EscapeDataString(ticket)}"), ct);
            return Items(list).Select(d => Device(d, "mac", "name", "model", "Ruckus")).OfType<ControllerDevice>().ToList();
        }
        throw new NameSourceException("That address doesn't look like a SmartZone controller.");
    }

    // ---- Any brand: a CSV or text list ----

    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])|(?<![0-9A-Fa-f.])[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}(?![0-9A-Fa-f])")]
    private static partial Regex MacPattern();

    private async Task<List<ControllerDevice>> ImportFileAsync(CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(_settings.ControllerAddress, ct);
        return ParseList(lines);
    }

    /// <summary>
    /// Each line: a MAC somewhere, and the name in the first other column that isn't a number, IP or MAC.
    /// Handles CSV exports with headers and quoted fields as well as hand-typed "mac, name" lines.
    /// </summary>
    public static List<ControllerDevice> ParseList(IEnumerable<string> lines)
    {
        var devices = new List<ControllerDevice>();
        foreach (string line in lines)
        {
            var match = MacPattern().Match(line);
            if (!match.Success) continue;
            var fields = line.Split(',', ';', '\t')
                .Select(f => f.Trim().Trim('"').Trim())
                .Where(f => f.Length > 0 && !MacPattern().IsMatch(f) && !IPAddress.TryParse(f, out _) && !double.TryParse(f, out _))
                .ToList();
            if (fields.Count == 0) continue;
            devices.Add(new ControllerDevice(match.Value, fields[0], fields.Count > 1 ? fields[1] : null));
        }
        return devices;
    }

    // ---- Shared ----

    private static Uri Origin(string address) => new(address.Contains("://") ? address : "https://" + address);

    private static HttpClient Client(Uri origin, params (string name, string value)[] headers)
    {
        var handler = new HttpClientHandler
        {
            // On-premises controllers usually have self-signed certificates. Accept that only for the address
            // the user typed; cloud APIs (Meraki, Mist) must present a valid certificate.
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || (IsPrivate(origin) && string.Equals(request.RequestUri?.Host, origin.Host, StringComparison.OrdinalIgnoreCase)),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Roamwise/{Updater.CurrentVersion}");
        foreach (var (name, value) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        return http;
    }

    /// <summary>A LAN address or local host name, where self-signed certificates are normal.</summary>
    private static bool IsPrivate(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host, out var ip)) return !uri.Host.Contains('.') || uri.Host.EndsWith(".local") || uri.Host.EndsWith(".lan");
        byte[] b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127
            : ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
        : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray()
        : root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray()
        : [];

    private static ControllerDevice? Device(JsonElement d, string macField, string nameField, string modelField, string maker)
    {
        string? mac = Str(d, macField);
        return Mac.Parse(mac) is null ? null : new ControllerDevice(mac!, Str(d, nameField) ?? "", Str(d, modelField), maker);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null
            : null;
}
