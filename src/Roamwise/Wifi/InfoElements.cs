namespace Roamwise.Wifi;

/// <summary>
/// Reads the 802.11 information elements an AP puts in its beacons: BSS Load (how busy the AP is),
/// the HT/VHT/HE/EHT operation elements (channel width), and the places vendors put the AP's name and model.
/// </summary>
internal static class InfoElements
{
    private const byte BssLoad = 11;
    private const byte HtOperation = 61;
    private const byte CiscoCcx = 133;
    private const byte VhtOperation = 192;
    private const byte VendorSpecific = 221;
    private const byte Extension = 255;
    private const byte ExtHeOperation = 36;
    private const byte ExtEhtOperation = 106;

    // Vendor IEs known to carry the AP's configured name as a plain string.
    private static readonly Dictionary<string, string> NamingVendors = new()
    {
        ["000B86"] = "Aruba",
        ["001977"] = "Aerohive",
        ["000C42"] = "MikroTik",
        ["00180A"] = "Meraki",
        ["5C5B35"] = "Mist",
    };

    // Vendor IEs that identify the AP maker (chipset vendors like Qualcomm or Broadcom are left out on purpose).
    private static readonly Dictionary<string, string> MakerVendors = new()
    {
        ["00156D"] = "Ubiquiti",
        ["004096"] = "Cisco",
        ["001392"] = "Ruckus",
    };

    private const string UbiquitiOui = "00156D";

    public readonly record struct Parsed(int WidthMHz, int? ChannelUtilization, int? StationCount,
        string? Name, string? Manufacturer, string? Model, byte[]? DeviceMac, IReadOnlyList<string> VendorElements);

    public static Parsed Parse(ReadOnlySpan<byte> ies)
    {
        int width = 20;
        int? utilization = null, stations = null;
        int? vhtWidth = null, heWidth = null, ehtWidth = null;
        bool htForty = false;
        string? ccxName = null, vendorName = null, wpsName = null, manufacturer = null, model = null;
        byte[]? deviceMac = null;
        var ouis = new List<string>();

        int i = 0;
        while (i + 2 <= ies.Length)
        {
            byte id = ies[i];
            int len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            var data = ies.Slice(i + 2, len);

            switch (id)
            {
                case BssLoad when len >= 3:
                    stations = data[0] | (data[1] << 8);
                    utilization = (int)Math.Round(data[2] * 100.0 / 255.0);
                    break;

                case HtOperation when len >= 2:
                    // Secondary channel offset (bits 0-1) set and STA channel width (bit 2) = any width.
                    htForty = (data[1] & 0x03) != 0 && (data[1] & 0x04) != 0;
                    break;

                case VhtOperation when len >= 3:
                    vhtWidth = data[0] switch
                    {
                        1 when data[2] != 0 && Math.Abs(data[2] - data[1]) == 8 => 160,
                        1 => 80,
                        2 or 3 => 160,
                        _ => null,
                    };
                    break;

                case Extension when len >= 1 && data[0] == ExtHeOperation:
                    heWidth = ParseHe6GhzWidth(data[1..]);
                    break;

                case Extension when len >= 1 && data[0] == ExtEhtOperation:
                    ehtWidth = ParseEhtWidth(data[1..]);
                    break;

                // Cisco Aironet/CCX: the AP name sits at offset 10, up to 16 bytes, null-padded.
                case CiscoCcx when len >= 26:
                    ccxName = Ascii(data.Slice(10, 16));
                    break;

                case VendorSpecific when len >= 4:
                    string oui = Convert.ToHexString(data[..3]);
                    ouis.Add($"{oui}:{Convert.ToHexString(data[3..])}");
                    if (oui == "0050F2" && data[3] == 4)
                        ParseWps(data[4..], ref wpsName, ref manufacturer, ref model);
                    else if (NamingVendors.ContainsKey(oui))
                        vendorName ??= LongestText(data[4..]);
                    else if (oui == UbiquitiOui)
                        deviceMac ??= UbiquitiDeviceMac(data[3..]);
                    break;
            }

            i += 2 + len;
        }

        if (htForty) width = 40;
        if (vhtWidth is int v) width = Math.Max(width, v);
        if (heWidth is int h) width = Math.Max(width, h);
        if (ehtWidth is int e) width = Math.Max(width, e);

        manufacturer ??= ouis.Select(o => NamingVendors.GetValueOrDefault(o[..6]) ?? MakerVendors.GetValueOrDefault(o[..6]))
            .FirstOrDefault(n => n is not null);
        // WPS "device name" is often just the product category; only use it when it says more than that.
        if (wpsName is not null && (IsGeneric(wpsName) || string.Equals(wpsName, manufacturer, StringComparison.OrdinalIgnoreCase)))
            wpsName = null;
        string? name = ccxName ?? vendorName ?? wpsName;
        return new Parsed(width, utilization, stations, name, manufacturer, model, deviceMac, ouis);
    }

    // UniFi beacons carry the device's base MAC (the one shown in the UniFi app) as type 0x81, length 6.
    private static byte[]? UbiquitiDeviceMac(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i + 8 <= d.Length; i++)
            if (d[i] == 0x81 && d[i + 1] == 6)
                return d.Slice(i + 2, 6).ToArray();
        return null;
    }

    // WPS attributes are big-endian type/length/value triples.
    private static void ParseWps(ReadOnlySpan<byte> d, ref string? deviceName, ref string? manufacturer, ref string? model)
    {
        int i = 0;
        string? modelName = null, modelNumber = null;
        while (i + 4 <= d.Length)
        {
            int type = (d[i] << 8) | d[i + 1];
            int len = (d[i + 2] << 8) | d[i + 3];
            if (i + 4 + len > d.Length) break;
            var value = d.Slice(i + 4, len);
            switch (type)
            {
                case 0x1011: deviceName = Ascii(value); break;
                case 0x1021: manufacturer = Ascii(value); break;
                case 0x1023: modelName = Ascii(value); break;
                case 0x1024: modelNumber = Ascii(value); break;
            }
            i += 4 + len;
        }

        model = (modelName, modelNumber) switch
        {
            (null, null) => null,
            (string m, null) => m,
            (null, string n) => n,
            (string m, string n) when m.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Length < 2 => m,
            (string m, string n) => $"{m} {n}",
        };
        if (model is not null && manufacturer is not null && model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            model = model[manufacturer.Length..].Trim();
        if (string.IsNullOrEmpty(model)) model = null;
    }

    private static bool IsGeneric(string s) =>
        s.Length < 2 || s.Trim().ToLowerInvariant() is "ap" or "router" or "wireless router" or "wireless ap" or "wps router"
            or "wireless access point" or "access point" or "wlan" or "wlan ap" or "gateway";

    /// <summary>Printable ASCII with control bytes and padding stripped; null when nothing readable is left.</summary>
    private static string? Ascii(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        int n = 0;
        foreach (byte b in bytes)
        {
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) chars[n++] = (char)b;
        }
        string s = new string(chars, 0, n).Trim();
        return s.Length > 0 ? s : null;
    }

    /// <summary>The longest readable run in a vendor element, used where vendors embed the AP name in their own format.</summary>
    private static string? LongestText(ReadOnlySpan<byte> bytes)
    {
        string? best = null;
        int start = -1;
        for (int i = 0; i <= bytes.Length; i++)
        {
            bool printable = i < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F;
            if (printable && start < 0) start = i;
            if (!printable && start >= 0)
            {
                var run = Ascii(bytes[start..i]);
                if (run is not null && run.Length >= 3 && run.Any(char.IsLetter) && run.Length > (best?.Length ?? 0)) best = run;
                start = -1;
            }
        }
        return best;
    }

    // HE Operation: 3 bytes params, 1 BSS color, 2 basic MCS, then optional VHT info (3), co-hosted (1), 6 GHz info (5).
    private static int? ParseHe6GhzWidth(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6) return null;
        bool vhtInfo = (d[1] & 0x40) != 0;
        bool coHosted = (d[1] & 0x80) != 0;
        bool sixGhzInfo = (d[2] & 0x02) != 0;
        if (!sixGhzInfo) return null;

        int offset = 6 + (vhtInfo ? 3 : 0) + (coHosted ? 1 : 0);
        if (d.Length < offset + 2) return null;
        return (d[offset + 1] & 0x03) switch { 0 => 20, 1 => 40, 2 => 80, _ => 160 };
    }

    // EHT Operation: 1 byte params (bit 0 = info present), 4 bytes basic MCS, then control (bits 0-2 = width).
    private static int? ParseEhtWidth(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6 || (d[0] & 0x01) == 0) return null;
        return (d[5] & 0x07) switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, 4 => 320, _ => null };
    }

    public static (WifiBand band, int channel) FromFrequency(int mhz)
    {
        if (mhz >= 2400 && mhz < 2500)
            return (WifiBand.Ghz24, mhz == 2484 ? 14 : (mhz - 2407) / 5);
        if (mhz == 5935)
            return (WifiBand.Ghz6, 2);
        if (mhz >= 5150 && mhz < 5925)
            return (WifiBand.Ghz5, (mhz - 5000) / 5);
        if (mhz >= 5925 && mhz <= 7125)
            return (WifiBand.Ghz6, (mhz - 5950) / 5);
        return (WifiBand.Unknown, 0);
    }
}
