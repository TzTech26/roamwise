namespace Roamwise.Wifi;

public enum WifiErrorKind { Other, LocationDenied, WifiOff, ServiceDown, NoPermission }

/// <summary>A Wi-Fi operation failed. <see cref="Kind"/> tells the engine which screen to show.</summary>
public class WifiException(WifiErrorKind kind, string message) : Exception(message)
{
    public WifiErrorKind Kind { get; } = kind;
}

/// <summary>
/// What the roaming engine needs from the operating system: scan, read access points and the current link,
/// and reconnect pinned to one BSSID. Windows implements it with the Native Wifi API, Linux with NetworkManager
/// and wpa_supplicant over D-Bus.
/// </summary>
public interface IWifiBackend : IDisposable
{
    /// <summary>Raised on any thread when the adapter connects, disconnects or roams.</summary>
    event Action? ConnectionChanged;

    /// <summary>False when the system lets Roamwise read access points but not switch between them.</summary>
    bool CanSwitch { get; }

    /// <summary>Whether <see cref="SetLowLatency"/> does anything on this system.</summary>
    bool SupportsLowLatency { get; }

    Task<IReadOnlyList<WifiAdapter>> GetAdaptersAsync(CancellationToken ct);

    /// <summary>Asks for a fresh scan and waits for it to finish (or for the timeout).</summary>
    Task ScanAsync(WifiAdapter adapter, TimeSpan timeout, CancellationToken ct);

    Task<IReadOnlyList<AccessPoint>> GetAccessPointsAsync(WifiAdapter adapter, CancellationToken ct);

    Task<WifiConnection?> GetConnectionAsync(WifiAdapter adapter, CancellationToken ct);

    /// <summary>Live RSSI of the current link, or null when unknown.</summary>
    Task<int?> GetRssiAsync(WifiAdapter adapter, CancellationToken ct);

    /// <summary>Names of the Wi-Fi networks saved on this computer.</summary>
    Task<IReadOnlyList<string>> GetSavedNetworksAsync(WifiAdapter adapter, CancellationToken ct);

    /// <summary>Moves the current connection to one BSSID. Returns once the attempt finished (or the timeout passed).</summary>
    Task<bool> ConnectToBssidAsync(WifiAdapter adapter, WifiConnection connection, byte[] bssid, TimeSpan timeout, CancellationToken ct);

    /// <summary>Turns background scanning off (true) or back on. Returns a problem to log, or null.</summary>
    string? SetLowLatency(WifiAdapter adapter, bool on);

    /// <summary>Bytes sent plus received on the adapter since boot, for spotting a busy connection.</summary>
    long? TrafficBytes(WifiAdapter adapter);

    bool OnBattery();

    /// <summary>Platform-specific lines for the top of a diagnostics report.</summary>
    IEnumerable<string> DiagnosticsHeader();
}
