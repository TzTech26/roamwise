using System.IO;
using System.Text;
using Roamwise.Native;
using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>
/// "Roamwise.exe --diagnose report.txt" writes what Roamwise sees without changing anything.
/// Handy for bug reports and for checking a new Windows build.
/// </summary>
public static class Diagnostics
{
    public static async Task WriteReportAsync(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Roamwise diagnostics, {DateTime.Now:u}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}");
        unsafe
        {
            sb.AppendLine($"Struct sizes: BssEntry={sizeof(WlanBssEntry)} (expect 360), ConnectionAttributes={sizeof(WlanConnectionAttributes)} (604), " +
                          $"InterfaceInfo={sizeof(WlanInterfaceInfo)} (532), ConnectionParameters={System.Runtime.InteropServices.Marshal.SizeOf<WlanConnectionParameters>()} (40)");
        }

        try
        {
            using var wlan = new WlanClient();
            var settings = new AppSettings();
            foreach (var adapter in wlan.GetAdapters())
            {
                sb.AppendLine().AppendLine($"Adapter: {adapter.Description} {adapter.Id} connected={adapter.IsConnected}");
                var conn = wlan.GetConnection(adapter.Id);
                sb.AppendLine(conn is null ? "Connection: none"
                    : $"Connection: profile '{conn.ProfileName}', ssid '{conn.Ssid}', bssid {conn.Bssid}, quality {conn.SignalQuality}, rx {conn.RxRateMbps} Mbps, rssi {wlan.GetRssi(adapter.Id)}");

                try
                {
                    await wlan.ScanAsync(adapter.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
                    var aps = wlan.GetAccessPoints(adapter.Id);
                    sb.AppendLine($"Access points: {aps.Count}");
                    foreach (var ap in aps.OrderBy(a => a.Ssid).ThenByDescending(a => a.Rssi))
                    {
                        var score = ApScorer.Score(ap, settings, aps);
                        sb.AppendLine($"  {(ap.Ssid.Length == 0 ? "(hidden)" : ap.Ssid),-28} {ap.Bssid}  {ap.Rssi,4} dBm  {WifiText.Band(ap.Band),-8} ch {ap.Channel,-3} " +
                                      $"{ap.WidthMHz,3} MHz  {WifiText.Generation(ap.Generation),-12} load {(ap.ChannelUtilization?.ToString() ?? "-"),3}%  " +
                                      $"sta {(ap.StationCount?.ToString() ?? "-"),3}  score {score.Total,6:0.0}  [{score.Explain()}]");
                        sb.AppendLine($"      device {ap.DeviceKey}  name '{ap.BroadcastName}'  hardware '{ap.Hardware}'  vendor IEs {string.Join(",", ap.VendorElements)}");
                        foreach (var n in ap.Neighbors)
                            sb.AppendLine($"      neighbor {Mac.Format(n.Bssid)}  {WifiText.Band(n.Band)} ch {n.Channel}  sameSsid={n.SameSsid} coLocated={n.CoLocated} shortSsid={n.ShortSsid:x8}");
                    }
                }
                catch (WlanException ex)
                {
                    sb.AppendLine($"Scan: {ex.Message}");
                }

                try { sb.AppendLine($"Saved profiles: {string.Join(", ", wlan.GetProfileNames(adapter.Id))}"); }
                catch (WlanException ex) { sb.AppendLine($"Saved profiles: {ex.Message}"); }
            }
        }
        catch (WlanException ex)
        {
            sb.AppendLine($"Wi-Fi API: {ex.Message}");
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }
}
