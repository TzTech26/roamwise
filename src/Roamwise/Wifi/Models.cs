namespace Roamwise.Wifi;

public enum WifiBand { Unknown, Ghz24, Ghz5, Ghz6 }

public enum WifiGeneration { Legacy, Wifi4, Wifi5, Wifi6, Wifi7 }

public sealed record WifiAdapter(Guid Id, string Description, bool IsConnected);

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

    public string Bssid => Mac.Format(BssidBytes);

    /// <summary>Last three octets, enough to tell nearby APs apart.</summary>
    public string ShortBssid => Mac.Short(BssidBytes);
}

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
        _ => "Legacy Wi-Fi",
    };

    public static string Quality(int rssi) => rssi switch
    {
        >= -60 => "Strong",
        >= -70 => "Fair",
        _ => "Weak",
    };
}
