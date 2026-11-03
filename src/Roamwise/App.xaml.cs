using System.ComponentModel;
using System.Windows;
using Roamwise.Core;
using Roamwise.UI;
using Roamwise.Wifi;

namespace Roamwise;

public partial class App : Application
{
    private const string MutexName = @"Local\Roamwise.SingleInstance";
    private const string ShowEventName = @"Local\Roamwise.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private SettingsStore _store = null!;
    private ActivityLog _log = null!;
    private ThemeService _theme = null!;
    private IRoamEngine _engine = null!;
    private TrayIcon _tray = null!;
    private FlyoutWindow _flyout = null!;
    private SettingsWindow? _settingsWindow;
    private NameSources _names = null!;
    private Updater _updater = null!;

    private AppSettings Settings => _store.Settings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            // Keep the tray app alive and leave a trace instead of vanishing.
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsStore.DataFolder, "crash.log"), $"{DateTime.Now:u}\n{ex.Exception}\n\n"); }
            catch (System.IO.IOException) { }
            _log?.Add(ActivityKind.Problem, $"Unexpected error: {ex.Exception.Message}");
            ex.Handled = true;
        };

        if (Array.IndexOf(e.Args, "--diagnose") is int di && di >= 0)
        {
            await Diagnostics.WriteReportAsync(e.Args.ElementAtOrDefault(di + 1) ?? "roamwise-diagnostics.txt", () => new WlanClient());
            Shutdown();
            return;
        }

        bool demo = e.Args.Contains("--demo");
        if (e.Args.Contains("--verbose")) DebugLog.Enable();
        bool background = e.Args.Contains("--background");

        // After an update the new copy waits for the old one to exit, then removes the old exe.
        if (e.Args.SkipWhile(a => a != "--wait").Skip(1).FirstOrDefault() is string pidText && int.TryParse(pidText, out int pid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(15000); }
            catch (ArgumentException) { } // already gone
        }
        Updater.CleanUpAfterUpdate();

        // One copy at a time; launching again just opens the running copy's flyout.
        _mutex = new Mutex(true, demo ? MutexName + ".Demo" : MutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _store = new SettingsStore();
        _log = new ActivityLog();
        _theme = new ThemeService(Settings);
        _theme.Apply();

        try
        {
            _engine = demo ? new DemoEngine(Settings) : new RoamEngine(Settings, _log, new WlanClient());
        }
        catch (WlanException ex)
        {
            MessageBox.Show($"Roamwise can't start: {ex.Message}", "Roamwise", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _names = new NameSources(Settings, _log, new DpapiSecretStore());
        _updater = new Updater(Settings, _log);
        _updater.QuitRequested += () => Dispatcher.BeginInvoke(Quit);

        _tray = new TrayIcon();
        _flyout = new FlyoutWindow(_engine, Settings, _theme, _updater, page => OpenSettings(page));

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

        _engine.SnapshotChanged += s => Dispatcher.BeginInvoke(UpdateTray);
        _engine.Roamed += OnRoamed;
        _theme.Changed += UpdateTray;
        Settings.PropertyChanged += OnSettingChanged;
        SyncStartup();
        if (e.Args.Contains("--updated")) _tray.Notify("Roamwise updated", $"You're now on version {Updater.CurrentVersion}.");

        if (!demo)
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(() => _flyout.ShowFlyout());
            }) { IsBackground = true }.Start();
        }

        if (!demo)
        {
            // Every 30 minutes: refresh controller names, check for an update (at most daily), and end a timed pause.
            async void Housekeeping()
            {
                await _names.RefreshIfStaleAsync(TimeSpan.FromMinutes(25));
                await _updater.CheckInBackgroundAsync(u =>
                    Dispatcher.BeginInvoke(() => _tray.Notify("Roamwise update available", $"Version {u.Version} is ready. Open Roamwise to install it.")));
            }
            Housekeeping();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            timer.Tick += (_, _) => Housekeeping();
            timer.Start();

            var pauseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            pauseTimer.Tick += (_, _) =>
            {
                if (Settings.PausedUntil is DateTime until && until <= DateTime.Now)
                {
                    Settings.PausedUntil = null;
                    _log.Add(ActivityKind.Info, "Pause ended. Auto-roam is back on.");
                }
            };
            pauseTimer.Start();
        }

        _engine.Start();
        if (!background) _flyout.ShowFlyout();
        if (demo && e.Args.Contains("--select")) _flyout.SelectCandidate();
        if (e.Args.SkipWhile(a => a != "--settings").Skip(1).FirstOrDefault() is string page) OpenSettings(page);
        else if (e.Args.Contains("--settings")) OpenSettings();
        if (demo && e.Args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault() is string folder) await SnapshotAsync(folder);
    }

    /// <summary>Renders the panel and every settings page to PNGs, for docs (works with the display off).</summary>
    private async Task SnapshotAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        await Task.Delay(2500);
        _flyout.SelectCandidate();
        await Task.Delay(300);
        Snapshot(_flyout, System.IO.Path.Combine(folder, "flyout.png"));
        foreach (var page in new[] { "general", "roaming", "ranking", "networks", "names", "activity", "about" })
        {
            OpenSettings(page);
            await Task.Delay(400);
            Snapshot(_settingsWindow!, System.IO.Path.Combine(folder, $"settings-{page}.png"));
        }
        Quit();

        static void Snapshot(Window w, string path)
        {
            var root = (FrameworkElement)w.Content;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            var size = new Size(root.ActualWidth, root.ActualHeight);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle((System.Windows.Media.Brush)w.FindResource("Rw.FlyoutFallback"), null, new Rect(size));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(size));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(visual);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var file = System.IO.File.Create(path);
            png.Save(file);
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.PropertyName == nameof(AppSettings.StartWithWindows)) SyncStartup();
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
        _tray.Update(_engine.Snapshot, Settings.IsRoaming,
            Settings.Enabled && Settings.PausedUntil is DateTime until ? $"until {until:t}" : null, _theme.IsDark);

    private void SyncStartup()
    {
        try { StartupManager.Apply(Settings.StartWithWindows); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Add(ActivityKind.Problem, "Couldn't update the start-with-Windows setting.");
        }
    }

    private void OnRoamed(RoamResult r)
    {
        if (!Settings.NotifyOnRoam) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (r.Success)
                _tray.Notify($"Switched to a better access point",
                    $"{r.Ssid}: now on {Settings.NameOf(r.To)} ({WifiText.Band(r.To.Band)}), {WifiText.Dbm(r.To.Rssi)} dBm (was {WifiText.Dbm(r.From.Rssi)}).");
            else
                _tray.Notify("Couldn't switch access points", $"Staying on {Settings.NameOf(r.From)}. Roamwise will skip {Settings.NameOf(r.To)} ({WifiText.Band(r.To.Band)}) for 15 minutes.");
        });
    }

    private void OpenSettings(string page = "general")
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, _log, _engine, _theme, _names, _updater);
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
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
