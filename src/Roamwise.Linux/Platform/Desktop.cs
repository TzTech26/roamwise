using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Roamwise.Core;
using Tmds.DBus.Protocol;

namespace Roamwise.Linux.Platform;

/// <summary>Start when you log in, via the XDG autostart folder every desktop reads.</summary>
public static class Autostart
{
    private static string File =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "roamwise.desktop");

    /// <summary>The AppImage when running from one, otherwise the binary itself.</summary>
    public static string Program => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } a ? a : Environment.ProcessPath!;

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, $"""
            [Desktop Entry]
            Type=Application
            Name=Roamwise
            Comment=Keeps you on the best Wi-Fi access point
            Exec="{Program}" --background
            Icon=roamwise
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """);
    }
}

/// <summary>
/// Puts Roamwise in the app menu (GNOME's app grid, Cinnamon's menu) when it runs from an AppImage, which can't do
/// that by itself. Rewritten on every start so the entry follows the AppImage if it moves.
/// </summary>
public static class AppMenu
{
    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static void Install(Func<Stream> icon)
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage) return;
        try
        {
            string iconPath = Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "roamwise.png");
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            using (var source = icon())
            using (var target = File.Create(iconPath))
                source.CopyTo(target);

            string entry = Path.Combine(DataHome, "applications", "roamwise.desktop");
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, $"""
                [Desktop Entry]
                Type=Application
                Name=Roamwise
                GenericName=Wi-Fi access point switcher
                Comment=Keeps you on the best Wi-Fi access point
                Exec="{appImage}"
                Icon=roamwise
                Terminal=false
                Categories=Network;Utility;
                Keywords=wifi;wireless;roaming;access point;
                StartupWMClass=roamwise
                """);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Desktop notifications through org.freedesktop.Notifications (GNOME, Cinnamon, KDE, XFCE...).</summary>
public sealed class Notifier
{
    private Bus? _bus;
    private uint _last;

    public async Task ShowAsync(string title, string body)
    {
        try
        {
            _bus ??= await Bus.SessionAsync();
            uint replace = _last;
            _last = await _bus.CallAsync("org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify",
                "susssasa{sv}i", (ref MessageWriter w) =>
                {
                    w.WriteString("Roamwise");
                    w.WriteUInt32(replace);
                    w.WriteString("network-wireless");
                    w.WriteString(title);
                    w.WriteString(body);
                    w.WriteArray(Array.Empty<string>());
                    var hints = w.WriteDictionaryStart();
                    w.WriteDictionaryEnd(hints);
                    w.WriteInt32(5000);
                }, static (m, _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or IOException or SocketException or DisconnectedException)
        {
            _bus = null; // no notification daemon; nothing else to do
        }
    }
}

/// <summary>
/// One copy at a time. The first copy listens on a socket in the user's runtime folder; a second copy connects,
/// asks the first to open its panel, and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket? _listener;
    private readonly string _path;

    public bool IsFirst => _listener is not null;
    public event Action? ShowRequested;

    public SingleInstance(string name)
    {
        string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.GetTempPath();
        _path = Path.Combine(dir, $"{name}-{Environment.UserName}.sock");

        if (TrySignal()) return;
        try { System.IO.File.Delete(_path); } catch (IOException) { }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(_path));
            socket.Listen(4);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return;
        }
        _listener = socket;
        _ = AcceptAsync();
    }

    private bool TrySignal()
    {
        if (!System.IO.File.Exists(_path)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(_path));
            s.Send(Encoding.ASCII.GetBytes("show\n"));
            return true;
        }
        catch (SocketException) { return false; } // stale socket from a crashed copy
    }

    private async Task AcceptAsync()
    {
        while (_listener is not null)
        {
            try
            {
                using var client = await _listener.AcceptAsync();
                var buffer = new byte[16];
                int n = await client.ReceiveAsync(buffer);
                if (Encoding.ASCII.GetString(buffer, 0, n).StartsWith("show")) ShowRequested?.Invoke();
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (_listener is not null) try { System.IO.File.Delete(_path); } catch (IOException) { }
    }
}

public static class Shell
{
    /// <summary>Opens a URL, folder or file with the desktop's default app.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The desktop's own Wi-Fi settings, where there is a known one.</summary>
    public static void OpenWifiSettings()
    {
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        (string exe, string[] args) = desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) || desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase)
            ? ("gnome-control-center", new[] { "wifi" })
            : desktop.Contains("Cinnamon", StringComparison.OrdinalIgnoreCase) ? ("cinnamon-settings", new[] { "network" })
            : desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) ? ("kcmshell6", new[] { "kcm_networkmanagement" })
            : ("nm-connection-editor", Array.Empty<string>());
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("nm-connection-editor") { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
