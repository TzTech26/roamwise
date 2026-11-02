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
            await Diagnostics.WriteReportAsync(e.Args.ElementAtOrDefault(di + 1) ?? "roamwise-diagnostics.txt");
            Shutdown();
            return;
        }

        bool demo = e.Args.Contains("--demo");
        bool background = e.Args.Contains("--background");

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
            _engine = demo ? new DemoEngine(Settings) : new RoamEngine(Settings, _log);
        }
        catch (WlanException ex)
        {
            MessageBox.Show($"Roamwise can't start: {ex.Message}", "Roamwise", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _tray = new TrayIcon();
        _flyout = new FlyoutWindow(_engine, Settings, _theme, () => OpenSettings());

        _tray.OpenRequested += () => _flyout.Toggle();
        _tray.SettingsRequested += () => OpenSettings();
        _tray.CheckRequested += () => _engine.CheckNow();
        _tray.PauseToggled += () => Settings.Enabled = !Settings.Enabled;
        _tray.ExitRequested += Quit;

        _engine.SnapshotChanged += s => Dispatcher.BeginInvoke(() => _tray.Update(s, Settings.Enabled, _theme.IsDark));
        _engine.Roamed += OnRoamed;
        _theme.Changed += () => _tray.Update(_engine.Snapshot, Settings.Enabled, _theme.IsDark);
        Settings.PropertyChanged += OnSettingChanged;
        SyncStartup();

        if (!demo)
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(() => _flyout.ShowFlyout());
            }) { IsBackground = true }.Start();
        }

        _engine.Start();
        if (!background) _flyout.ShowFlyout();
        if (demo && e.Args.Contains("--select")) _flyout.SelectCandidate();
        if (e.Args.SkipWhile(a => a != "--settings").Skip(1).FirstOrDefault() is string page) OpenSettings(page);
        else if (e.Args.Contains("--settings")) OpenSettings();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.PropertyName == nameof(AppSettings.StartWithWindows)) SyncStartup();
        if (e.PropertyName == nameof(AppSettings.Enabled))
        {
            _tray.Update(_engine.Snapshot, Settings.Enabled, _theme.IsDark);
            _log.Add(ActivityKind.Info, Settings.Enabled ? "Auto-roam turned on." : "Auto-roam paused.");
        }
    });

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
            _settingsWindow = new SettingsWindow(Settings, _log, _engine, _theme);
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
