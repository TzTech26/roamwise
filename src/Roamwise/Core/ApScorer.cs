using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>How an access point's score was built, in dB-equivalent points so it compares directly with signal.</summary>
public sealed record ApScore(AccessPoint Ap, double Total, double BandBonus, double WidthBonus, double LoadPenalty, int CoChannelRadios)
{
    public string Explain()
    {
        if (!Ap.HeardDirectly) return "signal unknown";
        var parts = new List<string> { $"signal {WifiText.Dbm(Ap.Rssi)}" };
        if (BandBonus > 0.05) parts.Add($"band +{BandBonus:0.#}");
        if (BandBonus < -0.05) parts.Add($"band {WifiText.Minus}{-BandBonus:0.#}");
        if (WidthBonus > 0.05) parts.Add($"width +{WidthBonus:0.#}");
        if (LoadPenalty > 0.05) parts.Add($"load {WifiText.Minus}{LoadPenalty:0.#}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Ranks access points of the same network. Signal strength is the base; faster bands and wider channels
/// earn a bonus that fades out as signal weakens (a weak 6 GHz link loses to a solid 5 GHz one), and busy APs
/// or crowded channels lose points.
/// </summary>
public static class ApScorer
{
    // Bonuses apply in full at or above this signal and fade to nothing at FadeEndDbm.
    private const double FullBonusDbm = -65;
    private const double FadeEndDbm = -80;

    public static ApScore Score(AccessPoint ap, AppSettings s, IReadOnlyList<AccessPoint> everything)
    {
        // Known only from a neighbor report: no signal reading, so no fair score.
        if (!ap.HeardDirectly) return new ApScore(ap, double.NaN, 0, 0, 0, 0);

        double fade = Math.Clamp((ap.Rssi - FadeEndDbm) / (FullBonusDbm - FadeEndDbm), 0, 1);

        double band = ap.Band switch
        {
            WifiBand.Ghz5 => s.Bonus5GhzDb * fade,
            WifiBand.Ghz6 => s.Bonus6GhzDb * fade,
            WifiBand.Ghz24 when s.Avoid24Ghz => -6,
            _ => 0,
        };

        double width = ap.WidthMHz switch
        {
            >= 320 => 4,
            >= 160 => 3,
            >= 80 => 2,
            >= 40 => 1,
            _ => 0,
        } * fade;
        if (ap.Generation >= WifiGeneration.Wifi6) width += 1 * fade;

        double load = 0;
        int coChannel = CoChannelRadios(ap, everything);
        if (s.ConsiderLoad)
        {
            if (ap.ChannelUtilization is int util)
                load = util * 0.08 + (ap.StationCount is int n && n > 25 ? Math.Min((n - 25) * 0.1, 3) : 0);
            else
                load = Math.Min(coChannel * (ap.Band == WifiBand.Ghz24 ? 1.5 : 1.0), 8);
        }

        return new ApScore(ap, ap.Rssi + band + width - load, band, width, load, coChannel);
    }

    /// <summary>
    /// Other radios that share (or, on 2.4 GHz, overlap) this AP's channel and are loud enough to contend for airtime.
    /// All BSSIDs of one physical device count once.
    /// </summary>
    private static int CoChannelRadios(AccessPoint ap, IReadOnlyList<AccessPoint> all)
    {
        static string Radio(AccessPoint a) => a.DeviceKey;
        string self = Radio(ap);

        return all
            .Where(o => o.Band == ap.Band && o.Rssi >= -82 && Radio(o) != self)
            .Where(o => ap.Band == WifiBand.Ghz24 ? Math.Abs(o.Channel - ap.Channel) < 5 : o.Channel == ap.Channel)
            .Select(Radio)
            .Distinct()
            .Count();
    }
}
