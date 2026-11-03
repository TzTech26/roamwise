using System.Text;
using Roamwise.Core;
using Roamwise.Wifi;
using Tmds.DBus.Protocol;

namespace Roamwise.Linux.Platform;

/// <summary>
/// Wi-Fi on Linux. NetworkManager (which any desktop user may query) provides the adapter, the connection,
/// saved networks and scans. wpa_supplicant, which NetworkManager drives underneath, provides what NetworkManager
/// doesn't expose: signal in dBm, the raw beacon data (load, width, names, 6 GHz neighbors) and a roam to a
/// chosen BSSID without disconnecting. Its D-Bus API is root-only by default, so until the one-time setup
/// (see <see cref="SwitchPermission"/>) Roamwise reads through NetworkManager alone and can't switch.
/// </summary>
public sealed class LinuxWifi : IWifiBackend
{
    private const string Nm = "org.freedesktop.NetworkManager";
    private const string NmPath = "/org/freedesktop/NetworkManager";
    private const string NmDevice = "org.freedesktop.NetworkManager.Device";
    private const string NmWireless = "org.freedesktop.NetworkManager.Device.Wireless";
    private const string NmAp = "org.freedesktop.NetworkManager.AccessPoint";
    private const string Wpa = "fi.w1.wpa_supplicant1";
    private const string WpaPath = "/fi/w1/wpa_supplicant1";
    private const string WpaIface = "fi.w1.wpa_supplicant1.Interface";
    private const string WpaBss = "fi.w1.wpa_supplicant1.BSS";

    private const uint DeviceTypeWifi = 2;
    private const uint DeviceStateActivated = 100;

    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Bus? _bus;
    private string? _wpaInterface;          // supplicant object for the adapter, when we're allowed to use it
    private string? _wpaProblem;
    private string _watched = "";
    private string _nmVersion = "unknown";

    public event Action? ConnectionChanged;

    public bool CanSwitch => _wpaInterface is not null;
    public bool SupportsLowLatency => false;

    /// <summary>Why switching isn't available, for diagnostics ("access denied", "not running"...).</summary>
    public string? SwitchProblem => _wpaProblem;

    private async Task<Bus> BusAsync()
    {
        if (_bus is not null) return _bus;
        await _connect.WaitAsync();
        try
        {
            if (_bus is not null) return _bus;
            try { _bus = await Bus.SystemAsync(); }
            catch (Exception ex) when (ex is ConnectException or IOException or System.Net.Sockets.SocketException or ArgumentException)
            {
                throw new WifiException(WifiErrorKind.ServiceDown, "Can't reach the system bus, so Wi-Fi details aren't available.");
            }
            _ = WatchAsync(_stop.Token);
            return _bus;
        }
        finally { _connect.Release(); }
    }

    private static WifiException NmMissing(DBusException ex) =>
        ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
            ? new WifiException(WifiErrorKind.ServiceDown, "NetworkManager isn't running. Roamwise needs it to manage Wi-Fi.")
            : new WifiException(WifiErrorKind.Other, $"NetworkManager: {ex.ErrorMessage}");

    // ---- Adapters ----

    public async Task<IReadOnlyList<WifiAdapter>> GetAdaptersAsync(CancellationToken ct)
    {
        var bus = await BusAsync();
        try
        {
            var devices = await bus.CallAsync(Nm, NmPath, Nm, "GetDevices", null, null, static (m, _) => m.GetBodyReader().ReadArrayOfObjectPath());
            var result = new List<WifiAdapter>();
            foreach (var path in devices.Select(d => d.ToString()))
            {
                var p = await bus.GetAllAsync(Nm, path, NmDevice);
                if (p.Num("DeviceType") != DeviceTypeWifi) continue;
                string name = p.Str("Interface");
                string driver = p.Str("Driver");
                result.Add(new WifiAdapter(path, name, driver.Length > 0 ? $"{name}, {driver} driver" : name, p.Num("State") == DeviceStateActivated));
            }

            // Pick the supplicant interface for the adapter we'll use (connected first, like the engine does).
            var main = result.OrderByDescending(a => a.IsConnected).FirstOrDefault();
            if (main is not null) await FindSupplicantAsync(bus, main.Name);
            _nmVersion = Props.Unwrap(await bus.GetAsync(Nm, NmPath, Nm, "Version")).GetString();
            return result;
        }
        catch (DBusException ex) { throw NmMissing(ex); }
    }

    private async Task FindSupplicantAsync(Bus bus, string ifname)
    {
        try
        {
            _wpaInterface = await bus.CallAsync(Wpa, WpaPath, Wpa, "GetInterface", "s",
                (ref MessageWriter w) => w.WriteString(ifname), static (m, _) => m.GetBodyReader().ReadObjectPathAsString());
            _wpaProblem = null;
        }
        catch (DBusException ex)
        {
            _wpaInterface = null;
            _wpaProblem = ex.ErrorName switch
            {
                "org.freedesktop.DBus.Error.AccessDenied" => "access denied (one-time setup not done)",
                "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" => "wpa_supplicant isn't on D-Bus (iwd or another Wi-Fi daemon in use?)",
                _ => $"{ex.ErrorName}: {ex.ErrorMessage}",
            };
        }
    }

    private async Task EnsureWifiOnAsync(Bus bus)
    {
        var on = await bus.GetAsync(Nm, NmPath, Nm, "WirelessEnabled");
        if (Props.Num(on) == 0) throw new WifiException(WifiErrorKind.WifiOff, "Wi-Fi is turned off.");
    }

    // ---- Scanning ----

    public async Task ScanAsync(WifiAdapter adapter, TimeSpan timeout, CancellationToken ct)
    {
        var bus = await BusAsync();
        try
        {
            await EnsureWifiOnAsync(bus);
            long before = await LastScanAsync(bus, adapter);
            try
            {
                await bus.CallAsync(Nm, adapter.Id, NmWireless, "RequestScan", "a{sv}", (ref MessageWriter w) =>
                {
                    var dict = w.WriteDictionaryStart();
                    w.WriteDictionaryEnd(dict);
                });
            }
            catch (DBusException)
            {
                // Rate-limited or already scanning; the latest results are still useful.
                return;
            }

            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(250, ct);
                if (await LastScanAsync(bus, adapter) != before) return;
            }
        }
        catch (DBusException ex) { throw NmMissing(ex); }
    }

    /// <summary>When the last scan finished (NetworkManager 1.12+); -1 where unknown, and then a scan just waits out its timeout.</summary>
    private static async Task<long> LastScanAsync(Bus bus, WifiAdapter adapter)
    {
        try { return Props.Num(await bus.GetAsync(Nm, adapter.Id, NmWireless, "LastScan"), -1); }
        catch (DBusException) { return -1; }
    }

    public async Task<IReadOnlyList<AccessPoint>> GetAccessPointsAsync(WifiAdapter adapter, CancellationToken ct)
    {
        var bus = await BusAsync();
        await EnsureWifiOnAsync(bus);
        if (_wpaInterface is string wpa)
        {
            try { return await SupplicantAccessPointsAsync(bus, wpa); }
            catch (DBusException) { _wpaInterface = null; } // fell back below; setup may have been removed
        }
        try { return await NetworkManagerAccessPointsAsync(bus, adapter); }
        catch (DBusException ex) { throw NmMissing(ex); }
    }

    /// <summary>Full detail: dBm, frequency and the beacon's information elements.</summary>
    private static async Task<IReadOnlyList<AccessPoint>> SupplicantAccessPointsAsync(Bus bus, string wpa)
    {
        var paths = Props.Paths(await bus.GetAsync(Wpa, wpa, WpaIface, "BSSs"));
        var result = new List<AccessPoint>(paths.Count);
        foreach (var path in paths)
        {
            Dictionary<string, VariantValue> p;
            try { p = await bus.GetAllAsync(Wpa, path, WpaBss); }
            catch (DBusException) { continue; } // expired between the two calls
            if (p.Num("Age", 0) > 90) continue; // not heard for a while; the supplicant keeps them up to 3 minutes

            byte[] bssid = p.Bytes("BSSID");
            if (bssid.Length != 6) continue;
            int mhz = (int)p.Num("Frequency");
            var (band, channel) = InfoElements.FromFrequency(mhz);
            byte[] ies = p.Bytes("IEs");
            InfoElements.Parsed parsed;
            try { parsed = InfoElements.Parse(ies); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                // One access point with odd beacon data shouldn't hide the rest; keep the data so it can be fixed.
                DebugLog.Unexpected(new InvalidDataException($"Beacon data from {Mac.Format(bssid)} on {mhz} MHz couldn't be read: {Convert.ToHexString(ies)}", ex));
                parsed = InfoElements.Parse([]);
            }
            int rssi = (int)p.Num("Signal", -100);

            result.Add(new AccessPoint
            {
                Ssid = DecodeSsid(p.Bytes("SSID")),
                BssidBytes = bssid,
                Rssi = rssi,
                LinkQuality = Math.Clamp(2 * (rssi + 100), 0, 100),
                FrequencyMHz = mhz,
                Band = band,
                Channel = channel,
                WidthMHz = band == WifiBand.Ghz24 ? Math.Min(parsed.WidthMHz, 40) : parsed.WidthMHz,
                Generation = parsed.Generation,
                ChannelUtilization = parsed.ChannelUtilization,
                StationCount = parsed.StationCount,
                BroadcastName = parsed.Name,
                Manufacturer = parsed.Manufacturer,
                Model = parsed.Model,
                DeviceMac = parsed.DeviceMac,
                VendorElements = parsed.VendorElements,
                Neighbors = parsed.Neighbors,
            });
        }
        return result;
    }

    /// <summary>What any user can read: signal as a percentage, frequency, and channel width on NetworkManager 1.46+.</summary>
    private static async Task<IReadOnlyList<AccessPoint>> NetworkManagerAccessPointsAsync(Bus bus, WifiAdapter adapter)
    {
        var paths = await bus.CallAsync(Nm, adapter.Id, NmWireless, "GetAllAccessPoints", null, null,
            static (m, _) => m.GetBodyReader().ReadArrayOfObjectPath());
        var result = new List<AccessPoint>(paths.Length);
        foreach (var path in paths.Select(x => x.ToString()))
        {
            Dictionary<string, VariantValue> p;
            try { p = await bus.GetAllAsync(Nm, path, NmAp); }
            catch (DBusException) { continue; }
            if (Mac.Parse(p.Str("HwAddress")) is not { } bssid) continue;
            int mhz = (int)p.Num("Frequency");
            var (band, channel) = InfoElements.FromFrequency(mhz);
            int strength = (int)p.Num("Strength");
            int width = (int)p.Num("Bandwidth", 0);

            result.Add(new AccessPoint
            {
                Ssid = DecodeSsid(p.Bytes("Ssid")),
                BssidBytes = bssid,
                Rssi = StrengthToDbm(strength),
                LinkQuality = strength,
                FrequencyMHz = mhz,
                Band = band,
                Channel = channel,
                WidthMHz = width > 0 ? width : 20,
                Generation = WifiGeneration.Unknown,
            });
        }
        return result;
    }

    /// <summary>NetworkManager maps −100…−40 dBm linearly onto 0…100 %; undo that.</summary>
    public static int StrengthToDbm(int percent) => (int)Math.Round(-40 - (100 - Math.Clamp(percent, 0, 100)) * 0.6);

    // ---- The current connection ----

    public async Task<WifiConnection?> GetConnectionAsync(WifiAdapter adapter, CancellationToken ct)
    {
        var bus = await BusAsync();
        try
        {
            var device = await bus.GetAllAsync(Nm, adapter.Id, NmDevice);
            if (device.Num("State") != DeviceStateActivated) return null;
            var wireless = await bus.GetAllAsync(Nm, adapter.Id, NmWireless);
            string apPath = wireless.Path("ActiveAccessPoint");
            if (apPath is "" or "/") return null;
            var ap = await bus.GetAllAsync(Nm, apPath, NmAp);
            if (Mac.Parse(ap.Str("HwAddress")) is not { } bssid) return null;

            string profile = "";
            string active = device.Path("ActiveConnection");
            if (active is not ("" or "/"))
            {
                try { profile = Props.Unwrap(await bus.GetAsync(Nm, active, "org.freedesktop.NetworkManager.Connection.Active", "Id")).GetString(); }
                catch (DBusException) { }
            }

            int mbps = (int)(wireless.Num("Bitrate") / 1000);
            string ssid = DecodeSsid(ap.Bytes("Ssid"));
            return new WifiConnection
            {
                ProfileName = profile.Length > 0 ? profile : ssid,
                Ssid = ssid,
                BssidBytes = bssid,
                SignalQuality = (int)ap.Num("Strength"),
                RxRateMbps = mbps,
                TxRateMbps = mbps,
            };
        }
        catch (DBusException ex) { throw NmMissing(ex); }
    }

    public async Task<int?> GetRssiAsync(WifiAdapter adapter, CancellationToken ct)
    {
        if (_wpaInterface is not string wpa) return null;
        var bus = await BusAsync();
        try
        {
            var poll = await bus.CallAsync(Wpa, wpa, WpaIface, "SignalPoll", null, null,
                static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue());
            return poll.TryGetValue("rssi", out var r) ? (int)Props.Num(r) : null;
        }
        catch (DBusException) { return null; }
    }

    public async Task<IReadOnlyList<string>> GetSavedNetworksAsync(WifiAdapter adapter, CancellationToken ct)
    {
        var bus = await BusAsync();
        var names = new List<string>();
        try
        {
            var paths = await bus.CallAsync(Nm, NmPath + "/Settings", Nm + ".Settings", "ListConnections", null, null,
                static (m, _) => m.GetBodyReader().ReadArrayOfObjectPath());
            foreach (var path in paths.Select(x => x.ToString()))
            {
                try
                {
                    var ssid = await bus.CallAsync(Nm, path, Nm + ".Settings.Connection", "GetSettings", null, null, static (m, _) => ReadSsid(m));
                    if (ssid is { Length: > 0 }) names.Add(ssid);
                }
                catch (DBusException) { }
            }
        }
        catch (DBusException ex) { throw NmMissing(ex); }
        return names;
    }

    /// <summary>Reads the SSID out of a connection's settings (a{sa{sv}}), or null for non-Wi-Fi connections.</summary>
    private static string? ReadSsid(Message m)
    {
        var reader = m.GetBodyReader();
        var end = reader.ReadDictionaryStart();
        string? type = null, ssid = null;
        while (reader.HasNext(end))
        {
            string group = reader.ReadString();
            var values = reader.ReadDictionaryOfStringToVariantValue();
            if (group == "connection" && values.TryGetValue("type", out var t)) type = Props.Unwrap(t).GetString();
            if (group == "802-11-wireless") ssid = DecodeSsid(values.Bytes("ssid"));
        }
        return type == "802-11-wireless" ? ssid : null;
    }

    // ---- Switching ----

    public async Task<bool> ConnectToBssidAsync(WifiAdapter adapter, WifiConnection connection, byte[] bssid, TimeSpan timeout, CancellationToken ct)
    {
        if (_wpaInterface is not string wpa) throw new WifiException(WifiErrorKind.NoPermission, "Roamwise isn't allowed to switch access points yet.");
        var bus = await BusAsync();
        try
        {
            // Roam reassociates within the same network, like the driver does on its own, without dropping the profile.
            await bus.CallAsync(Wpa, wpa, WpaIface, "Roam", "s", (ref MessageWriter w) => w.WriteString(Mac.Format(bssid)));
        }
        catch (DBusException ex)
        {
            throw new WifiException(WifiErrorKind.Other, $"wpa_supplicant refused the switch: {ex.ErrorMessage}");
        }

        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(300, ct);
            try
            {
                var current = Props.Unwrap(await bus.GetAsync(Wpa, wpa, WpaIface, "CurrentBSS")).GetObjectPathAsString();
                if (current is not ("" or "/") && Mac.Equal(Props.Bytes(await bus.GetAsync(Wpa, current, WpaBss, "BSSID")), bssid))
                    return true;
            }
            catch (DBusException) { }
        }
        return false;
    }

    // ---- Events: poll the link every couple of seconds; cheaper to get right than signal subscriptions ----

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                var bus = _bus!;
                var devices = await bus.CallAsync(Nm, NmPath, Nm, "GetDevices", null, null, static (m, _) => m.GetBodyReader().ReadArrayOfObjectPath());
                var sb = new StringBuilder();
                foreach (var path in devices.Select(d => d.ToString()))
                {
                    var p = await bus.GetAllAsync(Nm, path, NmDevice);
                    if (p.Num("DeviceType") != DeviceTypeWifi) continue;
                    sb.Append(path).Append(p.Num("State"));
                    sb.Append(Props.Unwrap(await bus.GetAsync(Nm, path, NmWireless, "ActiveAccessPoint")).GetObjectPathAsString());
                }
                string state = sb.ToString();
                if (_watched.Length > 0 && state != _watched) ConnectionChanged?.Invoke();
                _watched = state;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is DBusException or DisconnectedException or InvalidOperationException) { }
        }
    }

    // ---- Everything else ----

    public string? SetLowLatency(WifiAdapter adapter, bool on) => null;

    public long? TrafficBytes(WifiAdapter adapter)
    {
        string dir = $"/sys/class/net/{adapter.Name}/statistics";
        try { return long.Parse(File.ReadAllText($"{dir}/rx_bytes").Trim()) + long.Parse(File.ReadAllText($"{dir}/tx_bytes").Trim()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    /// <summary>On battery when a battery exists and no mains or USB-C supply is online.</summary>
    public bool OnBattery()
    {
        try
        {
            bool battery = false, plugged = false;
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/power_supply"))
            {
                string type = Read(Path.Combine(dir, "type"));
                if (type == "Battery") battery = true;
                else if (type is "Mains" or "USB" && Read(Path.Combine(dir, "online")) == "1") plugged = true;
            }
            return battery && !plugged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        static string Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }

    // Read after GetAdaptersAsync. Blocking on a bus call here would deadlock: this can run on the bus reader thread.
    public IEnumerable<string> DiagnosticsHeader() =>
        [
            $"NetworkManager {_nmVersion}",
            $"Desktop: {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")} on {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}",
            $"wpa_supplicant: {(_wpaInterface is not null ? $"available ({_wpaInterface})" : _wpaProblem ?? "not checked yet")}",
        ];

    private static string DecodeSsid(byte[] bytes) => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 32));

    public void Dispose()
    {
        _stop.Cancel();
        _bus?.Dispose();
    }
}
