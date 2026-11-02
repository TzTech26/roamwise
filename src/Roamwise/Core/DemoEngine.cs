using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>What the UI needs from the engine. The real one scans; the demo one serves a busy office network.</summary>
public interface IRoamEngine
{
    EngineSnapshot Snapshot { get; }
    event Action<EngineSnapshot>? SnapshotChanged;
    event Action<RoamResult>? Roamed;
    void Start();
    void CheckNow();
    void SwitchTo(string bssid);
    IReadOnlyList<string> SavedNetworks();
}

/// <summary>Run with --demo to see the UI without a crowded network nearby (used for screenshots).</summary>
public sealed class DemoEngine(AppSettings settings) : IRoamEngine
{
    public EngineSnapshot Snapshot { get; private set; } = new();
    public event Action<EngineSnapshot>? SnapshotChanged;
    public event Action<RoamResult>? Roamed { add { } remove { } }

    public void Start() => Build();
    public void CheckNow() => Build();
    public void SwitchTo(string bssid) => Build();
    public IReadOnlyList<string> SavedNetworks() => ["Harbor Office", "Harbor Guest", "Home 5G", "Pixel hotspot"];

    private void Build()
    {
        var aps = new List<AccessPoint>
        {
            Ap("Harbor Office", "3c:28:6d:a4:f1:20", -71, WifiBand.Ghz5, 44, 80, WifiGeneration.Wifi6, 62, 31, name: "Lobby"),
            Ap("Harbor Office", "3c:28:6d:b2:07:9c", -54, WifiBand.Ghz5, 149, 80, WifiGeneration.Wifi6, 18, 9, device: "3c:28:6d:b2:07:9b"),
            Ap("Harbor Office", "3c:28:6d:b2:07:9d", -58, WifiBand.Ghz6, 37, 160, WifiGeneration.Wifi6, 7, 4, device: "3c:28:6d:b2:07:9b"),
            Ap("Harbor Office", "3c:28:6d:a4:f1:21", -66, WifiBand.Ghz24, 6, 20, WifiGeneration.Wifi6, 81, 40, name: "Lobby"),
            Ap("Harbor Office", "3c:28:6d:c8:5e:40", -77, WifiBand.Ghz5, 36, 80, WifiGeneration.Wifi6, 35, 14, name: "Conference B"),
            Ap("Harbor Office", "3c:28:6d:c8:5e:41", -84, WifiBand.Ghz24, 11, 20, WifiGeneration.Wifi6, 70, 22, name: "Conference B"),
            Ap("Harbor Guest", "3e:28:6d:a4:f1:20", -70, WifiBand.Ghz5, 44, 80, WifiGeneration.Wifi6, 62, 12, name: "Lobby"),
        };
        var siblings = aps.Where(a => a.Ssid == "Harbor Office").ToList();
        var scored = siblings.Select(a => ApScorer.Score(a, settings, aps)).OrderByDescending(s => s.Total).ToList();
        var current = scored.First(s => s.Ap.Bssid == "3c:28:6d:a4:f1:20");
        var best = scored.Where(s => s != current && s.Ap.Rssi >= settings.MinCandidateDbm).MaxBy(s => s.Total);

        Snapshot = new EngineSnapshot
        {
            State = EngineState.Connected,
            Connection = new WifiConnection
            {
                ProfileName = "Harbor Office", Ssid = "Harbor Office", BssidBytes = current.Ap.BssidBytes,
                SignalQuality = 58, RxRateMbps = 286, TxRateMbps = 240,
            },
            Siblings = scored,
            Current = current,
            Candidate = best is not null && best.Total - current.Total >= settings.MinImprovementDb ? best : null,
            IsManaged = true,
            NearbyNetworks = 2,
            Decision = best is null ? "You're on the best access point."
                : $"{settings.NameOf(best.Ap)} ({WifiText.Band(best.Ap.Band)}) is +{best.Total - current.Total:0} better. Confirming over 1 more check.",
            LastCheck = DateTime.Now.AddSeconds(-12),
            NextCheck = DateTime.Now.AddSeconds(18),
        };
        SnapshotChanged?.Invoke(Snapshot);
    }

    private static AccessPoint Ap(string ssid, string mac, int rssi, WifiBand band, int ch, int width, WifiGeneration gen, int util, int sta,
        string? name = null, string? device = null) => new()
    {
        Ssid = ssid,
        BssidBytes = Bytes(mac),
        Rssi = rssi, LinkQuality = Math.Clamp(2 * (rssi + 100), 0, 100), FrequencyMHz = 0,
        Band = band, Channel = ch, WidthMHz = width, Generation = gen, ChannelUtilization = util, StationCount = sta,
        BroadcastName = name,
        DeviceMac = device is null ? null : Bytes(device),
        Manufacturer = device is null ? "Aruba" : "Ubiquiti",
        Model = device is null ? "AP-535" : null,
    };

    private static byte[] Bytes(string mac) => mac.Split(':').Select(h => Convert.ToByte(h, 16)).ToArray();
}
