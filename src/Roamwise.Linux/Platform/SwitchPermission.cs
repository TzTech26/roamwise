using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Roamwise.Linux.Platform;

/// <summary>
/// The one-time setup that lets Roamwise talk to wpa_supplicant. Its D-Bus API is limited to root (and the
/// netdev group on some distributions), so Roamwise asks once, through the desktop's normal password prompt
/// (pkexec), to install a D-Bus policy that allows the current user. Removing the file undoes it.
/// </summary>
public static partial class SwitchPermission
{
    private static string User => Environment.UserName;

    public static string PolicyPath => $"/etc/dbus-1/system.d/roamwise-{User}.conf";

    public static bool Installed => File.Exists(PolicyPath);

    [GeneratedRegex("^[a-z_][a-z0-9_.-]*[$]?$")]
    private static partial Regex SafeUserName();

    private static string Policy => $"""
        <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-BUS Bus Configuration 1.0//EN"
         "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
        <!-- Installed by Roamwise so {User} can read Wi-Fi scan details and switch access points.
             Delete this file to undo it. -->
        <busconfig>
          <policy user="{User}">
            <allow send_destination="fi.w1.wpa_supplicant1"/>
            <allow receive_sender="fi.w1.wpa_supplicant1" receive_type="signal"/>
          </policy>
        </busconfig>
        """;

    /// <summary>Installs the policy. Returns null on success or a sentence explaining what went wrong.</summary>
    public static async Task<string?> GrantAsync()
    {
        if (!SafeUserName().IsMatch(User)) return $"The user name \"{User}\" can't be used in a D-Bus policy.";
        string temp = Path.Combine(Path.GetTempPath(), $"roamwise-policy-{Guid.NewGuid():N}.conf");
        await File.WriteAllTextAsync(temp, Policy);
        try
        {
            // install copies with the right owner and mode; then the bus reloads its configuration.
            return await RunAsRootAsync($"install -o root -g root -m 0644 '{temp}' '{PolicyPath}' && {ReloadBus}");
        }
        finally { File.Delete(temp); }
    }

    public static Task<string?> RevokeAsync() => RunAsRootAsync($"rm -f '{PolicyPath}' && {ReloadBus}");

    private const string ReloadBus =
        "(dbus-send --system --type=method_call --dest=org.freedesktop.DBus / org.freedesktop.DBus.ReloadConfig || systemctl reload dbus)";

    private static async Task<string?> RunAsRootAsync(string script)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pkexec", ["/bin/sh", "-c", script])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode switch
            {
                0 => null,
                // pkexec: 126 = dismissed or not authorized, 127 = authentication failed.
                126 or 127 => "The password prompt was cancelled, or the password wasn't accepted.",
                _ => err.Trim() is { Length: > 0 } e ? e : $"The setup failed (exit code {p.ExitCode}).",
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "pkexec isn't installed, so Roamwise can't ask for permission. Install the policykit-1 package.";
        }
    }
}
