using System.Text;
using Roamwise.Wifi;

namespace Roamwise.Core;

/// <summary>
/// "Roamwise --diagnose report.txt" writes what Roamwise sees without changing anything.
/// Handy for bug reports and for checking a new OS build.
/// </summary>
public static class Diagnostics
{
    public static async Task WriteReportAsync(string path, Func<IWifiBackend> openWifi)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Roamwise {Updater.CurrentVersion} diagnostics, {DateTime.Now:u}");
        sb.AppendLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        var ct = CancellationToken.None;

        try
        {
            using var wlan = openWifi();
            var adapters = await wlan.GetAdaptersAsync(ct);
            foreach (string line in wlan.DiagnosticsHeader()) sb.AppendLine(line);
            sb.AppendLine($"Can switch access points: {wlan.CanSwitch}");
            var rules = new RoamRules();
            foreach (var adapter in adapters)
            {
                sb.AppendLine().AppendLine($"Adapter: {adapter.Name} ({adapter.Description}) {adapter.Id} connected={adapter.IsConnected}");
                var conn = await wlan.GetConnectionAsync(adapter, ct);
                sb.AppendLine(conn is null ? "Connection: none"
                    : $"Connection: profile '{conn.ProfileName}', ssid '{conn.Ssid}', bssid {conn.Bssid}, quality {conn.SignalQuality}, rx {conn.RxRateMbps} Mbps, rssi {await wlan.GetRssiAsync(adapter, ct)}");

                try
                {
                    await wlan.ScanAsync(adapter, TimeSpan.FromSeconds(5), ct);
                    var aps = await wlan.GetAccessPointsAsync(adapter, ct);
                    sb.AppendLine($"Access points: {aps.Count}");
                    foreach (var ap in aps.OrderBy(a => a.Ssid).ThenByDescending(a => a.Rssi))
                    {
                        var score = ApScorer.Score(ap, rules, aps);
                        sb.AppendLine($"  {(ap.Ssid.Length == 0 ? "(hidden)" : ap.Ssid),-28} {ap.Bssid}  {ap.Rssi,4} dBm  {WifiText.Band(ap.Band),-8} ch {ap.Channel,-3} " +
                                      $"{ap.WidthMHz,3} MHz  {WifiText.Generation(ap.Generation),-12} load {(ap.ChannelUtilization?.ToString() ?? "-"),3}%  " +
                                      $"sta {(ap.StationCount?.ToString() ?? "-"),3}  score {score.Total,6:0.0}  [{score.Explain()}]");
                        sb.AppendLine($"      device {ap.DeviceKey}  name '{ap.BroadcastName}'  hardware '{ap.Hardware}'  vendor IEs {string.Join(",", ap.VendorElements)}");
                        foreach (var n in ap.Neighbors)
                            sb.AppendLine($"      neighbor {Mac.Format(n.Bssid)}  {WifiText.Band(n.Band)} ch {n.Channel}  sameSsid={n.SameSsid} coLocated={n.CoLocated} shortSsid={n.ShortSsid:x8}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Scan: {Describe(ex)}");
                }

                try { sb.AppendLine($"Saved networks: {string.Join(", ", await wlan.GetSavedNetworksAsync(adapter, ct))}"); }
                catch (Exception ex) { sb.AppendLine($"Saved networks: {Describe(ex)}"); }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Wi-Fi: {Describe(ex)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    // Expected problems read as a sentence; anything else keeps its stack trace for the bug report.
    private static string Describe(Exception ex) => ex is WifiException ? ex.Message : ex.ToString();
}
