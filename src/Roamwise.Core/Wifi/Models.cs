namespace Roamwise.Wifi;

public enum WifiBand { Unknown, Ghz24, Ghz5, Ghz6 }

/// <summary>Unknown when the system doesn't report the PHY type (NetworkManager without raw beacon data).</summary>
public enum WifiGeneration { Unknown = -1, Legacy, Wifi4, Wifi5, Wifi6, Wifi7 }

public sealed record WifiAdapter(string Id, string Name, string Description, bool IsConnected);

/// <summary>One access point radio (BSSID) seen in a scan.</summary>
public sealed record AccessPoint
{
    public required string Ssid { get; init; }
    public required byte[] BssidBytes { get; init; }
    public required int Rssi { get; init; }
    public required int LinkQuality { get; init; }
    public required int FrequencyMHz { get; init; }
    public required WifiBand Band { get; init; }
    public required int Channel { get; init; }
    public required int WidthMHz { get; init; }
    public required WifiGeneration Generation { get; init; }
    /// <summary>Channel airtime in use as reported by the AP (BSS Load element), 0-100.</summary>
    public int? ChannelUtilization { get; init; }
    /// <summary>Devices associated to the AP as reported by the AP (BSS Load element).</summary>
    public int? StationCount { get; init; }
    /// <summary>The name the AP announces about itself (vendor name tags or WPS device name), when it does.</summary>
    public string? BroadcastName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    /// <summary>The device's own MAC when the AP announces it (UniFi does); matches what the vendor's app shows.</summary>
    public byte[]? DeviceMac { get; init; }
    /// <summary>Raw vendor-specific elements as "OUI:hex", for diagnostics.</summary>
    public IReadOnlyList<string> VendorElements { get; init; } = [];
    /// <summary>Other radios this AP advertises (mostly its 6 GHz siblings).</summary>
    public IReadOnlyList<ReportedNeighbor> Neighbors { get; init; } = [];
    /// <summary>
    /// False for radios we only know about from another AP's neighbor report. Their signal is unknown,
    /// so they're shown but never chosen automatically.
    /// </summary>
    public bool HeardDirectly { get; init; } = true;
    /// <summary>Missing from the latest scan but heard recently; its signal is from an earlier scan.</summary>
    public bool Stale { get; init; }

    public string Bssid => Mac.Format(BssidBytes);

    /// <summary>Last three octets, enough to tell nearby APs apart.</summary>
    public string ShortBssid => Mac.Short(BssidBytes);

    /// <summary>
    /// Identifies the physical access point. When the AP doesn't announce its device MAC, we rely on its radios
    /// (2.4, 5 and 6 GHz, plus extra SSIDs) sharing the middle four octets of their BSSIDs.
    /// </summary>
    public string DeviceKey => DeviceMac is { } m ? Convert.ToHexString(m) : Convert.ToHexString(BssidBytes, 1, 4);

    /// <summary>The announced name, or a short ID from the octets every radio of the device shares, e.g. "AP 27:63".</summary>
    public string DefaultName => BroadcastName
        ?? (DeviceMac is { } m ? $"AP {m[4]:x2}:{m[5]:x2}" : $"AP {BssidBytes[3]:x2}:{BssidBytes[4]:x2}");

    public string Hardware => string.Join(' ', new[] { Manufacturer, Model }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed record ReportedNeighbor(byte[] Bssid, WifiBand Band, int Channel, bool SameSsid, uint? ShortSsid, bool CoLocated);

public sealed record WifiConnection
{
    public required string ProfileName { get; init; }
    public required string Ssid { get; init; }
    public required byte[] BssidBytes { get; init; }
    public required int SignalQuality { get; init; }
    public required int RxRateMbps { get; init; }
    public required int TxRateMbps { get; init; }

    public string Bssid => Mac.Format(BssidBytes);
}

public static class Mac
{
    public static string Format(byte[] b) => string.Join(':', b.Select(x => x.ToString("x2")));

    public static string Short(byte[] b) => "…" + string.Join(':', b.Skip(3).Select(x => x.ToString("x2")));

    /// <summary>Reads aa:bb:cc:dd:ee:ff, AA-BB-..., aabb.ccdd.eeff or plain hex; null if it isn't a MAC.</summary>
    public static byte[]? Parse(string? s)
    {
        if (s is null) return null;
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? Convert.FromHexString(hex) : null;
    }

    public static bool Equal(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
}

public static class WifiText
{
    public const char Minus = '−';

    public static string Dbm(int v) => v < 0 ? $"{Minus}{-v}" : v.ToString();

    public static string Band(WifiBand b) => b switch
    {
        WifiBand.Ghz24 => "2.4 GHz",
        WifiBand.Ghz5 => "5 GHz",
        WifiBand.Ghz6 => "6 GHz",
        _ => "Unknown band",
    };

    public static string Generation(WifiGeneration g) => g switch
    {
        WifiGeneration.Wifi4 => "Wi-Fi 4",
        WifiGeneration.Wifi5 => "Wi-Fi 5",
        WifiGeneration.Wifi6 => "Wi-Fi 6",
        WifiGeneration.Wifi7 => "Wi-Fi 7",
        WifiGeneration.Unknown => "Unknown",
        _ => "Legacy Wi-Fi",
    };

    public static string Quality(int rssi) => rssi switch
    {
        >= -60 => "Strong",
        >= -70 => "Fair",
        _ => "Weak",
    };
}
