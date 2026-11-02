using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Roamwise.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string ZipUrl, string? SumsUrl);

/// <summary>
/// Updates from the project's GitHub Releases. Downloads only from github.com, checks the SHA-256 published
/// with the release, swaps the exe in place (Windows allows renaming a running exe) and restarts.
/// </summary>
public sealed class Updater
{
    public const string Repo = "TzTech26/roamwise";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    public UpdateInfo? Available { get; private set; }
    public event Action<UpdateInfo>? UpdateAvailable;
    /// <summary>Raised after the new exe has started; the app should exit.</summary>
    public event Action? QuitRequested;

    public Updater(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
    }

    private static string AssetName =>
        $"Roamwise-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")}.zip";

    /// <summary>
    /// Only the published single-file exe can replace itself. A development build (Roamwise.dll beside the exe)
    /// still checks, but sends you to the releases page instead.
    /// </summary>
    public static bool CanSelfUpdate =>
        Environment.ProcessPath is string exe && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Roamwise.dll"));

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanUpAfterUpdate()
    {
        if (Environment.ProcessPath is not string exe) return;
        try { File.Delete(exe + ".old"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = Client();
        var release = await http.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        _settings.LastUpdateCheck = DateTime.Now;

        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);

        string? zip = null, sums = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name == AssetName) zip = url;
            if (name == "SHA256SUMS.txt") sums = url;
        }

        Available = version > CurrentVersion && zip is not null
            ? new UpdateInfo(version, tag, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                release.GetProperty("html_url").GetString() ?? ReleasesPage, zip, sums)
            : null;
        if (Available is not null) UpdateAvailable?.Invoke(Available);
        return Available;
    }

    /// <summary>Daily background check. Installs on its own only when the user turned that on.</summary>
    public async Task CheckInBackgroundAsync(Action<UpdateInfo> notify)
    {
        if (!_settings.CheckForUpdates) return;
        if (_settings.LastUpdateCheck is DateTime last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
        try
        {
            var update = await CheckAsync();
            if (update is null || update.Tag == _settings.SkippedVersion) return;
            if (_settings.InstallUpdatesAutomatically && CanSelfUpdate) await InstallAsync(update);
            else notify(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // Offline or rate-limited; try again tomorrow.
        }
    }

    /// <summary>Downloads, verifies and swaps in the new exe, then starts it. Returns an error sentence on failure.</summary>
    public async Task<string?> InstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (!CanSelfUpdate) return "This copy of Roamwise was built from source, so it can't update itself. Download the release instead.";
        string exe = Environment.ProcessPath!;
        string work = Path.Combine(Path.GetTempPath(), $"Roamwise-update-{update.Version}");
        try
        {
            if (!IsGitHub(update.ZipUrl) || (update.SumsUrl is not null && !IsGitHub(update.SumsUrl)))
                return "The update isn't hosted on GitHub, so it was ignored.";
            if (update.SumsUrl is null) return "This release has no checksum file, so Roamwise won't install it automatically.";

            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, AssetName);
            using (var http = Client())
            {
                await File.WriteAllBytesAsync(zipPath, await http.GetByteArrayAsync(update.ZipUrl, ct), ct);
                string sums = await http.GetStringAsync(update.SumsUrl, ct);
                string? expected = sums.Split('\n')
                    .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == AssetName)?[0];
                string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath, ct)));
                if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return "The download didn't match the published checksum, so it wasn't installed.";
            }

            string extracted = Path.Combine(work, "out");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(zipPath, extracted);
            string newExe = Path.Combine(extracted, "Roamwise.exe");
            if (!File.Exists(newExe)) return "The download didn't contain Roamwise.exe.";

            // Swap: the running exe can be renamed but not overwritten.
            File.Delete(exe + ".old");
            File.Move(exe, exe + ".old");
            try { File.Copy(newExe, exe); }
            catch { File.Move(exe + ".old", exe); throw; }

            _log.Add(ActivityKind.Info, $"Updated to Roamwise {update.Version}.");
            // The new copy waits for this one to exit before taking over the tray.
            Process.Start(new ProcessStartInfo(exe, $"--background --updated --wait {Environment.ProcessId}") { UseShellExecute = false });
            QuitRequested?.Invoke();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Roamwise can't write to {Path.GetDirectoryName(exe)}. Move it to a folder you own (like Documents) or download the update yourself.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
        {
            return $"The update failed: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com"));

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Roamwise/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
