namespace Roamwise.Wifi;

/// <summary>
/// Reads the 802.11 information elements an AP puts in its beacons. We only need a few:
/// BSS Load (how busy the AP is) and the HT/VHT/HE/EHT operation elements (channel width).
/// </summary>
internal static class InfoElements
{
    private const byte BssLoad = 11;
    private const byte HtOperation = 61;
    private const byte VhtOperation = 192;
    private const byte Extension = 255;
    private const byte ExtHeOperation = 36;
    private const byte ExtEhtOperation = 106;

    public readonly record struct Parsed(int WidthMHz, int? ChannelUtilization, int? StationCount);

    public static Parsed Parse(ReadOnlySpan<byte> ies)
    {
        int width = 20;
        int? utilization = null, stations = null;
        int? vhtWidth = null, heWidth = null, ehtWidth = null;
        bool htForty = false;

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
            }

            i += 2 + len;
        }

        if (htForty) width = 40;
        if (vhtWidth is int v) width = Math.Max(width, v);
        if (heWidth is int h) width = Math.Max(width, h);
        if (ehtWidth is int e) width = Math.Max(width, e);
        return new Parsed(width, utilization, stations);
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
