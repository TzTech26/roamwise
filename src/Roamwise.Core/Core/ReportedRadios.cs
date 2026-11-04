using System.Buffers.Binary;
using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>
/// Adds the 6 GHz radios a network's access points advertise in their neighbor reports but that Windows
/// hasn't heard itself. Without this, a 6 GHz radio that's a little out of reach simply vanishes from the list.
/// Skipped when the adapter has no 6 GHz: those radios aren't out of reach, they're unusable.
/// </summary>
public static class ReportedRadios
{
    public static List<AccessPoint> WithReported(List<AccessPoint> siblings, IReadOnlyList<AccessPoint> all, string ssid, bool adapterHas6Ghz = true)
    {
        var result = new List<AccessPoint>(siblings);
        if (!adapterHas6Ghz) return result;
        uint shortSsid = InfoElements.ShortSsid(ssid);
        var heard = all.Select(a => a.Bssid).ToHashSet();

        foreach (var reporter in siblings.Where(s => s.HeardDirectly))
        {
            foreach (var n in reporter.Neighbors)
            {
                if (n.Band != WifiBand.Ghz6) continue;
                bool sameNetwork = n.SameSsid || n.ShortSsid == shortSsid || n.ShortSsid == BinaryPrimitives.ReverseEndianness(shortSsid);
                if (!sameNetwork) continue;
                string bssid = Mac.Format(n.Bssid);
                if (heard.Contains(bssid) || result.Any(r => r.Bssid == bssid)) continue;

                // Same device when the AP says so, or when the BSSIDs share their middle octets.
                bool sameDevice = n.CoLocated || n.Bssid.AsSpan(1, 4).SequenceEqual(reporter.BssidBytes.AsSpan(1, 4));
                result.Add(new AccessPoint
                {
                    Ssid = ssid,
                    BssidBytes = n.Bssid,
                    Rssi = -100,
                    LinkQuality = 0,
                    FrequencyMHz = 5950 + 5 * n.Channel,
                    Band = WifiBand.Ghz6,
                    Channel = n.Channel,
                    WidthMHz = 0,
                    Generation = reporter.Generation >= WifiGeneration.Wifi6 ? reporter.Generation : WifiGeneration.Wifi6,
                    BroadcastName = sameDevice ? reporter.BroadcastName : null,
                    Manufacturer = sameDevice ? reporter.Manufacturer : null,
                    Model = sameDevice ? reporter.Model : null,
                    DeviceMac = sameDevice ? reporter.DeviceMac : null,
                    HeardDirectly = false,
                });
            }
        }
        return result;
    }
}
