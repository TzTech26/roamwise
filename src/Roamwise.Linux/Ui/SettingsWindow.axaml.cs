using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Roamwise.Core;
using Roamwise.Linux.Platform;

namespace Roamwise.Linux.Ui;

public sealed record ActivityRow(IBrush Brush, string When, string Message);

public sealed record NameRow(string Key, string Name, string Detail);

public partial class SettingsWindow : Window
{
    public const string RepoUrl = "https://github.com/TzTech26/roamwise";

    private readonly AppSettings _settings = null!;
    private readonly ActivityLog _log = null!;
    private readonly IRoamEngine _engine = null!;
    private readonly NameSources _names = null!;
    private readonly Updater _updater = null!;
    private readonly Func<Task<string?>> _allowSwitching = null!;
    private readonly Func<Task<string?>> _revokeSwitching = null!;
    private bool _loadingSource;

    public SettingsWindow() => InitializeComponent(); // designer

    public SettingsWindow(AppSettings settings, ActivityLog log, IRoamEngine engine, NameSources names, Updater updater,
        Func<Task<string?>> allowSwitching, Func<Task<string?>> revokeSwitching)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        _engine = engine;
        _names = names;
        _updater = updater;
        _allowSwitching = allowSwitching;
        _revokeSwitching = revokeSwitching;
        DataContext = settings;

        VersionText.Text = $"Version {Updater.CurrentVersion}";

        _settings.PropertyChanged += OnSettingChanged;
        _log.Changed += OnLogChanged;
        _updater.UpdateAvailable += OnUpdateAvailable;
        _engine.SnapshotChanged += OnSnapshot;
        ActualThemeVariantChanged += (_, _) => UpdateActivity();
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingChanged;
            _log.Changed -= OnLogChanged;
            _updater.UpdateAvailable -= OnUpdateAvailable;
            _engine.SnapshotChanged -= OnSnapshot;
        };

        UpdateSummary();
        UpdateNetworks();
        UpdateActivity();
        UpdateNames();
        LoadSource();
        RenderPause();
        RenderPermission();
        RenderUpdate();
        Nav.SelectedIndex = 0;
    }

    public void ShowPage(string page)
    {
        string tag = page == "updates" ? "about" : page;
        var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? Nav.Items.OfType<ListBoxItem>().First();
        Nav.SelectedItem = item;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        UpdateSummary();
        if (e.PropertyName == nameof(AppSettings.Scope)) UpdateNetworks();
        if (e.PropertyName == nameof(AppSettings.ApNames)) UpdateNames();
        if (e.PropertyName is nameof(AppSettings.Enabled) or nameof(AppSettings.PausedUntil)) RenderPause();
        if (e.PropertyName == nameof(AppSettings.LastUpdateCheck)) RenderUpdate();
    });

    private void OnLogChanged() => Dispatcher.UIThread.Post(UpdateActivity);

    private void OnUpdateAvailable(UpdateInfo _) => Dispatcher.UIThread.Post(() => RenderUpdate());

    private void OnSnapshot(EngineSnapshot _) => Dispatcher.UIThread.Post(() => RenderPermission());

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageGeneral is null) return;
        string tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "general";
        PageGeneral.IsVisible = tag == "general";
        PageRoaming.IsVisible = tag == "roaming";
        PageRanking.IsVisible = tag == "ranking";
        PageNetworks.IsVisible = tag == "networks";
        PageNames.IsVisible = tag == "names";
        PageActivity.IsVisible = tag == "activity";
        PageAbout.IsVisible = tag == "about";
        Scroller.Offset = default;
        if (tag == "networks") UpdateNetworks();
    }

    // The rules in one paragraph, so the effect of the sliders is clear without knowing the jargon.
    private void UpdateSummary()
    {
        var s = _settings;
        string every = DurationConverter.Format(s.ScanIntervalSeconds);
        string trigger = s.OnlyWhenWeak
            ? $"Every {every}, Roamwise checks your signal. Once it drops below {Wifi.WifiText.Dbm(s.WeakSignalDbm)} dBm, it compares the other access points of your network."
            : $"Every {every}, Roamwise compares the access points of your network.";
        string confirm = s.ConfirmScans == 1
            ? "it switches at the next check"
            : $"it switches once that's held for {s.ConfirmScans} checks in a row (about {DurationConverter.Format(s.ScanIntervalSeconds * (s.ConfirmScans - 1))})";
        RulesSummary.Text =
            $"{trigger} " +
            $"If one scores at least {s.MinImprovementDb} points higher and its signal is {Wifi.WifiText.Dbm(s.MinCandidateDbm)} dBm or better, " +
            $"{confirm}. After a switch it waits {DurationConverter.Format(s.CooldownSeconds)} before switching again.";
    }

    // ---- Networks ----

    private ObservableCollection<string> ActiveList =>
        _settings.Scope == NetworkScope.OnlyIncluded ? _settings.IncludedNetworks : _settings.ExcludedNetworks;

    private void UpdateNetworks()
    {
        bool only = _settings.Scope == NetworkScope.OnlyIncluded;
        ListHeading.Text = only ? "Networks to manage" : "Networks to leave alone";
        NetworkList.ItemsSource = ActiveList;
        ListEmpty.Text = only
            ? "No networks listed yet. Roamwise won't switch access points until you add one."
            : "No networks excluded. Roamwise manages every network you join.";
        bool empty = ActiveList.Count == 0;
        ListEmpty.IsVisible = empty;
        ListEmptyDivider.IsVisible = empty;

        string? current = _engine.Snapshot.Connection?.Ssid;
        var saved = _engine.SavedNetworks()
            .Append(current ?? "")
            .Where(n => n.Length > 0 && !ActiveList.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SavedList.ItemsSource = saved;
        SavedPanel.IsVisible = saved.Count > 0;
    }

    private void Add(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || ActiveList.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        ActiveList.Add(name);
        UpdateNetworks();
    }

    private void AddNetwork_Click(object? sender, RoutedEventArgs e)
    {
        Add(NewNetwork.Text);
        NewNetwork.Text = "";
        NewNetwork.Focus();
    }

    private void NewNetwork_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddNetwork_Click(sender, e);
    }

    private void AddSaved_Click(object? sender, RoutedEventArgs e) => Add((sender as Button)?.Tag as string);

    private void RemoveNetwork_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string name) ActiveList.Remove(name);
        UpdateNetworks();
    }

    // ---- Activity ----

    private void UpdateActivity()
    {
        var rows = _log.Entries.Select(e => new ActivityRow(
            Res.Brush(e.Kind switch
            {
                ActivityKind.Roamed => "Rw.Accent",
                ActivityKind.RoamFailed => "Rw.Weak",
                ActivityKind.Problem => "Rw.Fair",
                _ => "Rw.TextFaint",
            }, ActualThemeVariant),
            When(e.Time),
            e.Message)).ToList();

        ActivityList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        ActivityEmpty.IsVisible = empty;
        ActivityCard.IsVisible = !empty;
        ClearActivity.IsEnabled = !empty;

        static string When(DateTime t) =>
            t.Date == DateTime.Today ? $"Today {t:t}" :
            t.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {t:t}" : t.ToString("MMM d, t");
    }

    private void ClearActivity_Click(object? sender, RoutedEventArgs e) => _log.Clear();

    // ---- Names ----

    private void UpdateNames()
    {
        var rows = _settings.ApNames
            .Select(kv => new NameRow(kv.Key, kv.Value, kv.Key.Length == 12
                ? $"Device {string.Join(':', Enumerable.Range(0, 6).Select(i => kv.Key.Substring(i * 2, 2).ToLowerInvariant()))}"
                : $"Radios with BSSIDs ??:{string.Join(':', Enumerable.Range(0, 4).Select(i => kv.Key.Substring(i * 2, 2).ToLowerInvariant()))}:??"))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        CustomNames.ItemsSource = rows;
        CustomNamesEmpty.IsVisible = rows.Count == 0;
    }

    private void ResetName_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string key) _settings.ForgetName(key);
        UpdateNames();
    }

    private NameSourceKind SelectedSource => (SourceBox.SelectedItem as NameSourceInfo)?.Kind ?? NameSourceKind.None;

    private void SourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSource) return;
        if (SelectedSource != _settings.NameSource)
        {
            // Credentials belong to one source; start clean when switching.
            _settings.NameSource = SelectedSource;
            _settings.ControllerAddress = "";
            _settings.ControllerUser = "";
            _settings.ControllerExtra = "";
            _ = _names.ForgetSecretAsync();
            _settings.SetNamedDevices([]);
            _settings.ControllerSyncedAt = null;
            AddressBox.Text = UserBox.Text = ExtraBox.Text = SecretBox.Text = "";
            SourceStatus.Text = "";
        }
        RenderSource();
    }

    private void LoadSource()
    {
        _loadingSource = true;
        SourceBox.ItemsSource = NameSources.All;
        SourceBox.SelectedItem = NameSources.Info(_settings.NameSource);
        AddressBox.Text = _settings.ControllerAddress;
        UserBox.Text = _settings.ControllerUser;
        ExtraBox.Text = _settings.ControllerExtra;
        _loadingSource = false;
        RenderSource();
    }

    private void RenderSource()
    {
        var info = NameSources.Info(SelectedSource);
        bool none = info.Kind == NameSourceKind.None, file = info.Kind == NameSourceKind.File;
        SourceRow.Description = info.Help;

        AddressGroup.IsVisible = info.AddressLabel is not null;
        AddressRow.Header = info.AddressLabel;
        AddressBox.Watermark = info.AddressHint;
        AddressBox.IsReadOnly = file;
        BrowseButton.IsVisible = file;

        UserGroup.IsVisible = info.UserLabel is not null;
        UserRow.Header = info.UserLabel;

        SecretGroup.IsVisible = info.SecretLabel is not null;
        SecretRow.Header = info.SecretLabel;
        SecretRow.Description = _names.SecretDescription;
        SecretBox.Watermark = _settings.ControllerSecretProtected.Length > 0 ? "Saved. Enter a new one to replace it." : $"Paste the {info.SecretLabel?.ToLowerInvariant()}";

        ExtraGroup.IsVisible = info.ExtraLabel is not null;
        ExtraRow.Header = info.ExtraLabel;
        ExtraBox.Watermark = info.ExtraHint;

        SourceFooter.IsVisible = !none;
        SourceConnect.Content = file ? "Import" : "Connect";
        SourceForget.Content = file ? "Remove" : "Disconnect";
        SourceForget.IsVisible = _names.IsConfigured || _settings.NamedDevices.Count > 0;
        if (string.IsNullOrEmpty(SourceStatus.Text))
        {
            int n = _settings.NamedDevices.Count;
            SourceStatus.Foreground = Res.Brush("Rw.TextMuted");
            SourceStatus.Text = _settings.ControllerSyncedAt is DateTime t && n > 0
                ? $"{(file ? "Imported" : "Connected.")} {n} {(n == 1 ? "device" : "devices")}, updated {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("MMM d"))}."
                : file ? "Nothing imported yet." : "Not connected.";
        }
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a list of access points",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Device lists") { Patterns = ["*.csv", "*.txt"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path) AddressBox.Text = path;
    }

    private async void SourceConnect_Click(object? sender, RoutedEventArgs e)
    {
        _settings.ControllerAddress = AddressBox.Text?.Trim() ?? "";
        _settings.ControllerUser = UserBox.Text?.Trim() ?? "";
        _settings.ControllerExtra = ExtraBox.Text?.Trim() ?? "";
        SourceConnect.IsEnabled = false;
        if (SecretBox.Text is { Length: > 0 } secret)
        {
            await _names.SaveSecretAsync(secret);
            SecretBox.Text = "";
        }

        SourceStatus.Foreground = Res.Brush("Rw.TextMuted");
        SourceStatus.Text = SelectedSource == NameSourceKind.File ? "Importing…" : "Connecting…";
        var (ok, message) = await _names.SyncAsync();
        SourceStatus.Text = message;
        SourceStatus.Foreground = Res.Brush(ok ? "Rw.TextMuted" : "Rw.Weak");
        SourceConnect.IsEnabled = true;
        RenderSource();
        _engine.CheckNow();
    }

    private async void SourceForget_Click(object? sender, RoutedEventArgs e)
    {
        await _names.ForgetSecretAsync();
        _settings.SetNamedDevices([]);
        _settings.ControllerSyncedAt = null;
        SourceStatus.Foreground = Res.Brush("Rw.TextMuted");
        SourceStatus.Text = "Removed. Those names are no longer used.";
        RenderSource();
    }

    // ---- Pause and permission ----

    private void RenderPause()
    {
        bool paused = _settings.Enabled && _settings.PausedUntil is DateTime u && u > DateTime.Now;
        PauseRow.Header = paused ? $"Paused until {_settings.PausedUntil:t}" : "Pause for an hour";
        PauseButton.Content = paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = _settings.Enabled;
    }

    private void Pause_Click(object? sender, RoutedEventArgs e) =>
        _settings.PausedUntil = _settings.PausedUntil is DateTime u && u > DateTime.Now ? null : DateTime.Now.AddHours(1);

    private void RenderPermission(string? status = null)
    {
        bool can = _engine.Snapshot.CanSwitch;
        bool installed = OperatingSystem.IsLinux() && SwitchPermission.Installed;
        PermissionRow.Header = can ? "Roamwise can switch access points" : "Allow Roamwise to switch access points";
        PermissionRow.Description = status ?? (can
            ? installed
                ? $"Done once with your password; Roamwise talks to wpa_supplicant, the program that runs your Wi-Fi. Removing it deletes {SwitchPermission.PolicyPath}."
                : "This system already lets your account use wpa_supplicant, the program that runs your Wi-Fi."
            : "Switching between access points goes through wpa_supplicant, which only lets administrators in by default. " +
              "Roamwise asks once for your password to add a rule for your account. Until then it shows what it would do and uses estimated signal levels.");
        PermissionButton.Content = can ? "Remove" : "Allow";
        PermissionButton.IsVisible = !can || installed;
    }

    private async void Permission_Click(object? sender, RoutedEventArgs e)
    {
        PermissionButton.IsEnabled = false;
        string? problem = _engine.Snapshot.CanSwitch ? await _revokeSwitching() : await _allowSwitching();
        PermissionButton.IsEnabled = true;
        _engine.CheckNow();
        RenderPermission(problem);
    }

    // ---- Updates ----

    private void RenderUpdate(string? status = null)
    {
        var u = _updater.Available;
        UpdateTitle.Text = u is null ? $"Roamwise {Updater.CurrentVersion}" : $"Roamwise {u.Version} is available";
        UpdateStatus.Foreground = Res.Brush("Rw.TextMuted");
        UpdateStatus.Text = status ?? (u is not null
            ? $"You have {Updater.CurrentVersion}."
            : _settings.LastUpdateCheck is DateTime t ? $"Up to date. Last checked {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("MMM d"))}." : "Not checked yet.");
        UpdateNotes.IsVisible = u is not null;
        UpdateButton.Content = u is null ? "Check for updates" : Updater.CanSelfUpdate ? "Install and restart" : "Download";
        UpdateButton.Classes.Set("accent", u is not null);
    }

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (_updater.Available is { } u)
            {
                if (!Updater.CanSelfUpdate) { Shell.Open(u.PageUrl); return; }
                UpdateStatus.Text = "Downloading…";
                if (await _updater.InstallAsync(u) is string error) ShowError(error);
                return;
            }
            UpdateStatus.Text = "Checking…";
            try
            {
                await _updater.CheckAsync();
                RenderUpdate();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                ShowError(ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? "No releases have been published yet."
                    : "Couldn't reach GitHub. Check your connection and try again.");
            }
        }
        finally { UpdateButton.IsEnabled = true; }

        void ShowError(string message)
        {
            RenderUpdate(message);
            UpdateStatus.Foreground = Res.Brush("Rw.Weak");
        }
    }

    private void UpdateNotes_Click(object? sender, RoutedEventArgs e) => Shell.Open(_updater.Available?.PageUrl ?? Updater.ReleasesPage);

    // ---- Diagnostics ----

    private async void Diagnose_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save diagnostics report",
            SuggestedFileName = $"roamwise-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is not string path) return;
        DiagnoseButton.IsEnabled = false;
        DiagnoseButton.Content = "Scanning…";
        try
        {
            await Diagnostics.WriteReportAsync(path, () => new LinuxWifi());
            Shell.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Wifi.WifiException)
        {
            DiagnoseButton.Content = "Couldn't save";
            ToolTip.SetTip(DiagnoseButton, ex.Message);
            return;
        }
        finally { DiagnoseButton.IsEnabled = true; }
        DiagnoseButton.Content = "Save report…";
    }

    private void ResetRules_Click(object? sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        _settings.ScanIntervalSeconds = d.ScanIntervalSeconds;
        _settings.SlowerOnBattery = d.SlowerOnBattery;
        _settings.MinImprovementDb = d.MinImprovementDb;
        _settings.MinCandidateDbm = d.MinCandidateDbm;
        _settings.ConfirmScans = d.ConfirmScans;
        _settings.CooldownSeconds = d.CooldownSeconds;
        _settings.OnlyWhenWeak = d.OnlyWhenWeak;
        _settings.WeakSignalDbm = d.WeakSignalDbm;
    }

    private void OpenRepo_Click(object? sender, RoutedEventArgs e) => Shell.Open(RepoUrl);
    private void OpenData_Click(object? sender, RoutedEventArgs e) => Shell.Open(SettingsStore.DataFolder);
}
