using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Roamwise.Wifi;

/// <summary>
/// Which bands a Wi-Fi driver supports. The Native Wifi API doesn't say, but "netsh wlan show drivers" lists them
/// on Windows 11 ("Number of supported bands", one "6 GHz [...]" style line per band).
/// </summary>
internal static partial class DriverBands
{
    /// <summary>True or false when netsh lists the adapter's bands; null when it doesn't (older Windows) or can't be run.</summary>
    public static bool? Supports6Ghz(Guid iface)
    {
        string? name = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == iface)?.Name;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", ["wlan", "show", "drivers"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return Parse(output.Result, name);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static bool? Parse(string output, string? interfaceName)
    {
        // One block per adapter, each opened by an unindented "Interface name: <name>" line (the label is translated).
        var blocks = new List<List<string>>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains(':')) blocks.Add([line]);
            else blocks.LastOrDefault()?.Add(line);
        }

        var block = blocks.Count == 1 ? blocks[0]
            : blocks.FirstOrDefault(b => interfaceName is not null && b[0][(b[0].IndexOf(':') + 1)..].Trim() == interfaceName);
        if (block is null) return null;

        var bands = block.Select(l => BandLine().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        return bands.Count == 0 ? null : bands.Contains("6");
    }

    [GeneratedRegex(@"^\s+(\d+(?:[.,]\d+)?)\s*GHz\s*\[")]
    private static partial Regex BandLine();
}
