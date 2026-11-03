using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Roamwise.Core;
using Roamwise.Linux.Platform;
using Roamwise.Linux.Ui;
using Roamwise.Wifi;

namespace Roamwise.Linux;

public partial class App : Application
{
    private SettingsStore _store = null!;
    private ActivityLog _log = null!;
    private IRoamEngine _engine = null!;
    private Tray _tray = null!;
    private FlyoutWindow _flyout = null!;
    private SettingsWindow? _settingsWindow;
    private NameSources _names = null!;
    private Updater _updater = null!;
    private readonly Notifier _notifier = new();
    private bool _demo;

    private AppSettings Settings => _store.Settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        string[] args = Program.Args;
        _demo = args.Contains("--demo") || !OperatingSystem.IsLinux();
        if (args.Contains("--verbose")) DebugLog.Enable();
        bool background = args.Contains("--background");

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Keep the tray app alive and leave a trace instead of vanishing.
            Program.Crash(e.Exception);
            _log?.Add(ActivityKind.Problem, $"Unexpected error: {e.Exception.Message}");
            e.Handled = true;
        };

        _store = new SettingsStore();
        _log = new ActivityLog();
        ApplyTheme();

        _engine = _demo ? new DemoEngine(Settings) : new RoamEngine(Settings, _log, new LinuxWifi());
        _names = new NameSources(Settings, _log, new KeyringSecretStore());
        _updater = new Updater(Settings, _log);
        _updater.QuitRequested += () => Dispatcher.UIThread.Post(Quit);

        _tray = new Tray();
        _flyout = new FlyoutWindow(_engine, Settings, _updater, page => OpenSettings(page), AllowSwitchingAsync);

        _tray.OpenRequested += () => _flyout.Toggle();
        _tray.SettingsRequested += () => OpenSettings();
        _tray.CheckRequested += () => _engine.CheckNow();
        _tray.PauseToggled += () =>
        {
            if (Settings.IsRoaming) Settings.Enabled = false;
            else { Settings.PausedUntil = null; Settings.Enabled = true; }
        };
        _tray.PauseForHourRequested += () => Settings.PausedUntil = DateTime.Now.AddHours(1);
        _tray.ExitRequested += Quit;
        if (Program.Instance is { } instance) instance.ShowRequested += () => Dispatcher.UIThread.Post(_flyout.ShowFlyout);

        _engine.SnapshotChanged += _ => Dispatcher.UIThread.Post(UpdateTray);
        _engine.Roamed += OnRoamed;
        ActualThemeVariantChanged += (_, _) => UpdateTray();
        Settings.PropertyChanged += OnSettingChanged;
        SyncStartup();
        if (!_demo) AppMenu.Install(() => Avalonia.Platform.AssetLoader.Open(new Uri("avares://roamwise/Assets/roamwise.png")));
        if (args.Contains("--updated")) _ = _notifier.ShowAsync("Roamwise updated", $"You're now on version {Updater.CurrentVersion}.");

        if (!_demo)
        {
            // Every 30 minutes: refresh controller names, check for an update (at most daily). Every 30 s: end a timed pause.
            async void Housekeeping()
            {
                await _names.RefreshIfStaleAsync(TimeSpan.FromMinutes(25));
                await _updater.CheckInBackgroundAsync(u => _ = _notifier.ShowAsync("Roamwise update available", $"Version {u.Version} is ready. Open Roamwise to install it."));
            }
            Housekeeping();
            DispatcherTimer.Run(() => { Housekeeping(); return true; }, TimeSpan.FromMinutes(30));
            DispatcherTimer.Run(() =>
            {
                if (Settings.PausedUntil is DateTime until && until <= DateTime.Now)
                {
                    Settings.PausedUntil = null;
                    _log.Add(ActivityKind.Info, "Pause ended. Auto-roam is back on.");
                }
                return true;
            }, TimeSpan.FromSeconds(30));
        }

        _engine.Start();
        UpdateTray();
        if (!background) _flyout.ShowFlyout();
        if (_demo && args.Contains("--select")) _flyout.SelectCandidate();
        if (args.SkipWhile(a => a != "--settings").Skip(1).FirstOrDefault() is string page && !page.StartsWith("--")) OpenSettings(page);
        else if (args.Contains("--settings")) OpenSettings();
        if (_demo && args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault() is string folder) _ = SnapshotAsync(folder);
    }

    private void ApplyTheme() => RequestedThemeVariant = Settings.Theme switch
    {
        ThemePreference.Dark => ThemeVariant.Dark,
        ThemePreference.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    private async Task<string?> AllowSwitchingAsync()
    {
        if (_demo) return "Not available in the demo.";
        string? problem = await SwitchPermission.GrantAsync();
        _log.Add(problem is null ? ActivityKind.Info : ActivityKind.Problem,
            problem is null ? "Allowed Roamwise to switch access points." : $"Couldn't allow switching: {problem}");
        _engine.CheckNow();
        return problem;
    }

    private async Task<string?> RevokeSwitchingAsync()
    {
        if (_demo) return "Not available in the demo.";
        string? problem = await SwitchPermission.RevokeAsync();
        if (problem is null) _log.Add(ActivityKind.Info, "Removed Roamwise's permission to switch access points.");
        _engine.CheckNow();
        return problem;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (e.PropertyName == nameof(AppSettings.StartWithWindows)) SyncStartup();
        if (e.PropertyName == nameof(AppSettings.Theme)) ApplyTheme();
        if (e.PropertyName is nameof(AppSettings.Enabled) or nameof(AppSettings.PausedUntil))
        {
            UpdateTray();
            if (e.PropertyName == nameof(AppSettings.Enabled))
                _log.Add(ActivityKind.Info, Settings.Enabled ? "Auto-roam turned on." : "Auto-roam turned off.");
            else if (Settings.PausedUntil is DateTime until)
                _log.Add(ActivityKind.Info, $"Auto-roam paused until {until:t}.");
            _engine.CheckNow();
        }
    });

    private void UpdateTray() =>
        _tray.Update(_engine.Snapshot, Settings.IsRoaming, Settings.Enabled && Settings.PausedUntil is DateTime until ? $"until {until:t}" : null);

    private void SyncStartup()
    {
        if (_demo) return;
        try { Autostart.Apply(Settings.StartWithWindows); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add(ActivityKind.Problem, "Couldn't update the start-when-I-log-in setting.");
        }
    }

    private void OnRoamed(RoamResult r)
    {
        if (!Settings.NotifyOnRoam) return;
        if (r.Success)
            _ = _notifier.ShowAsync("Switched to a better access point",
                $"{r.Ssid}: now on {Settings.NameOf(r.To)} ({WifiText.Band(r.To.Band)}), {WifiText.Dbm(r.To.Rssi)} dBm (was {WifiText.Dbm(r.From.Rssi)}).");
        else
            _ = _notifier.ShowAsync("Couldn't switch access points",
                $"Staying on {Settings.NameOf(r.From)}. Roamwise will skip {Settings.NameOf(r.To)} ({WifiText.Band(r.To.Band)}) for 15 minutes.");
    }

    private void OpenSettings(string page = "general")
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, _log, _engine, _names, _updater, AllowSwitchingAsync, RevokeSwitchingAsync);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowPage(page);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void Quit()
    {
        _store.Save();
        _tray.Dispose();
        (_engine as IDisposable)?.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>Renders the panel, every settings page and the tray icons to PNGs (used for docs and for checking the UI).</summary>
    private async Task SnapshotAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(2500);
        _flyout.SelectCandidate();
        await Task.Delay(400);
        Snapshot(_flyout, Path.Combine(folder, "linux-flyout.png"));
        foreach (var page in new[] { "general", "roaming", "ranking", "networks", "names", "activity", "about" })
        {
            OpenSettings(page);
            await Task.Delay(500);
            Snapshot(_settingsWindow!, Path.Combine(folder, $"linux-settings-{page}.png"));
        }
        foreach (bool dark in new[] { true, false })
            foreach (var state in Enum.GetValues<Tray.State>())
                Tray.RenderBitmap(64, dark, state).Save(Path.Combine(folder, $"linux-tray-{(dark ? "dark" : "light")}-{state}.png".ToLowerInvariant()));
        Quit();

        static void Snapshot(Window w, string path)
        {
            double scale = w.RenderScaling;
            var size = new PixelSize((int)Math.Ceiling(w.Bounds.Width * scale), (int)Math.Ceiling(w.Bounds.Height * scale));
            using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bmp.Render(w);
            bmp.Save(path);
        }
    }
}
