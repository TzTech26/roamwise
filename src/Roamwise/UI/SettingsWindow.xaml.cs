using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.UI;

public partial class SettingsWindow : Window
{
    public const string RepoUrl = "https://github.com/TzTech26/roamwise";

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;
    private readonly IRoamEngine _engine;
    private readonly ThemeService _theme;
    private readonly NameSources _names;
    private readonly Updater _updater;

    public sealed record ActivityRow(string Glyph, Brush GlyphBrush, string When, string Message);

    public SettingsWindow(AppSettings settings, ActivityLog log, IRoamEngine engine, ThemeService theme, NameSources names, Updater updater)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        _engine = engine;
        _theme = theme;
        _names = names;
        _updater = updater;
        DataContext = settings;

        VersionText.Text = $"Version {Updater.CurrentVersion}";

        SourceInitialized += (_, _) => ApplyEffects();
        _theme.Changed += OnThemeChanged;
        _settings.PropertyChanged += OnSettingChanged;
        _log.Changed += OnLogChanged;
        NewNetwork.TextChanged += (_, _) => NewNetworkHint.Visibility = NewNetwork.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) =>
        {
            _theme.Changed -= OnThemeChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            _log.Changed -= OnLogChanged;
            _updater.UpdateAvailable -= OnUpdateAvailable;
        };

        UpdateSummary();
        UpdateNetworks();
        UpdateActivity();
        UpdateNames();
        LoadSource();
        RenderPause();
        RenderUpdate();
        _updater.UpdateAvailable += OnUpdateAvailable;
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "networks" => NavNetworks,
            "activity" => NavActivity,
            "roaming" => NavRoaming,
            "names" => NavNames,
            "about" or "updates" => NavAbout,
            "ranking" => NavRanking,
            _ => NavGeneral,
        };
        nav.IsChecked = true;
    }

    private void ApplyEffects()
    {
        bool backdrop = WindowEffects.Apply(this, _theme.IsDark, WindowEffects.BackdropMica);
        if (backdrop) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "Rw.FlyoutFallback");
    }

    private void OnThemeChanged()
    {
        ApplyEffects();
        UpdateActivity();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        UpdateSummary();
        if (e.PropertyName == nameof(AppSettings.Scope)) UpdateNetworks();
        if (e.PropertyName == nameof(AppSettings.ApNames)) UpdateNames();
        if (e.PropertyName is nameof(AppSettings.Enabled) or nameof(AppSettings.PausedUntil)) RenderPause();
        if (e.PropertyName == nameof(AppSettings.LastUpdateCheck)) RenderUpdate();
    });

    private void OnLogChanged() => Dispatcher.BeginInvoke(UpdateActivity);

    private void OnUpdateAvailable(UpdateInfo _) => Dispatcher.BeginInvoke(() => RenderUpdate());

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral is null) return; // fires during InitializeComponent
        PageGeneral.Visibility = Show(NavGeneral);
        PageRoaming.Visibility = Show(NavRoaming);
        PageRanking.Visibility = Show(NavRanking);
        PageNetworks.Visibility = Show(NavNetworks);
        PageNames.Visibility = Show(NavNames);
        PageActivity.Visibility = Show(NavActivity);
        PageAbout.Visibility = Show(NavAbout);
        Scroller.ScrollToTop();
        if (NavNetworks.IsChecked == true) UpdateNetworks();

        static Visibility Show(RadioButton r) => r.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // The rules in one paragraph, so the effect of the sliders is clear without knowing the jargon.
    private void UpdateSummary()
    {
        var s = _settings;
        string every = DurationConverter.Format(s.ScanIntervalSeconds);
        string trigger = s.OnlyWhenWeak
            ? $"Every {every}, Roamwise checks your signal. Once it drops below {WifiText.Dbm(s.WeakSignalDbm)} dBm, it compares the other access points of your network."
            : $"Every {every}, Roamwise compares the access points of your network.";
        string confirm = s.ConfirmScans == 1
            ? "it switches at the next check"
            : $"it switches once that's held for {s.ConfirmScans} checks in a row (about {DurationConverter.Format(s.ScanIntervalSeconds * (s.ConfirmScans - 1))})";
        RulesSummary.Text =
            $"{trigger} " +
            $"If one scores at least {s.MinImprovementDb} points higher and its signal is {WifiText.Dbm(s.MinCandidateDbm)} dBm or better, " +
            $"{confirm}. After a switch it waits {DurationConverter.Format(s.CooldownSeconds)} before switching again.";
    }

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
        ListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyDivider.Visibility = ListEmpty.Visibility;

        string? current = _engine.Snapshot.Connection?.Ssid;
        var saved = _engine.SavedNetworks()
            .Append(current ?? "")
            .Where(n => n.Length > 0 && !ActiveList.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SavedList.ItemsSource = saved;
        SavedPanel.Visibility = saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || ActiveList.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        ActiveList.Add(name);
        UpdateNetworks();
    }

    private void AddNetwork_Click(object sender, RoutedEventArgs e)
    {
        Add(NewNetwork.Text);
        NewNetwork.Clear();
        NewNetwork.Focus();
    }

    private void NewNetwork_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddNetwork_Click(sender, e);
    }

    private void AddSaved_Click(object sender, RoutedEventArgs e) => Add((string)((Button)sender).Tag);

    private void RemoveNetwork_Click(object sender, RoutedEventArgs e)
    {
        ActiveList.Remove((string)((Button)sender).Tag);
        UpdateNetworks();
    }

    private void UpdateActivity()
    {
        var rows = _log.Entries.Select(e => new ActivityRow(
            e.Kind switch
            {
                ActivityKind.Roamed => "",     // switch
                ActivityKind.RoamFailed => "", // error
                ActivityKind.Problem => "",    // warning
                _ => "",                       // info
            },
            (Brush)FindResource(e.Kind switch
            {
                ActivityKind.Roamed => "Rw.AccentText",
                ActivityKind.RoamFailed => "Rw.Weak",
                ActivityKind.Problem => "Rw.Fair",
                _ => "Rw.TextMuted",
            }),
            When(e.Time),
            e.Message)).ToList();

        ActivityList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        ActivityEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ActivityCard.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearActivity.IsEnabled = !empty;

        static string When(DateTime t) =>
            t.Date == DateTime.Today ? $"Today {t:t}" :
            t.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {t:t}" : t.ToString("MMM d, t");
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e) => _log.Clear();

    public sealed record NameRow(string Key, string Name, string Detail);

    private void UpdateNames()
    {
        var rows = _settings.ApNames
            .Select(kv => new NameRow(kv.Key, kv.Value, kv.Key.Length == 12
                ? $"Device {string.Join(':', Enumerable.Range(0, 6).Select(i => kv.Key.Substring(i * 2, 2).ToLowerInvariant()))}"
                : $"Radios with BSSIDs ??:{string.Join(':', Enumerable.Range(0, 4).Select(i => kv.Key.Substring(i * 2, 2).ToLowerInvariant()))}:??"))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        CustomNames.ItemsSource = rows;
        CustomNamesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Name sources ----

    private NameSourceKind SelectedSource => (SourceBox.SelectedItem as NameSourceInfo)?.Kind ?? NameSourceKind.None;

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            AddressBox.Clear(); UserBox.Clear(); ExtraBox.Clear(); SecretBox.Clear();
            SourceStatus.Text = "";
        }
        RenderSource();
    }

    private bool _loadingSource;

    private void LoadSource()
    {
        _loadingSource = true;
        SourceBox.ItemsSource = NameSources.All;
        SourceBox.SelectedItem = NameSources.Info(_settings.NameSource);
        AddressBox.Text = _settings.ControllerAddress;
        UserBox.Text = _settings.ControllerUser;
        ExtraBox.Text = _settings.ControllerExtra;
        _loadingSource = false;

        AddressBox.TextChanged += (_, _) => Hint(AddressHint, AddressBox.Text);
        ExtraBox.TextChanged += (_, _) => Hint(ExtraHint, ExtraBox.Text);
        RenderSource();
    }

    private void RenderSource()
    {
        var info = NameSources.Info(SelectedSource);
        bool none = info.Kind == NameSourceKind.None, file = info.Kind == NameSourceKind.File;
        SourceRow.Description = info.Help;

        AddressGroup.Visibility = Show(info.AddressLabel);
        AddressRow.Header = info.AddressLabel;
        AddressHint.Text = info.AddressHint;
        AddressBox.IsReadOnly = file;
        BrowseButton.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        Hint(AddressHint, AddressBox.Text);

        UserGroup.Visibility = Show(info.UserLabel);
        UserRow.Header = info.UserLabel;

        SecretGroup.Visibility = Show(info.SecretLabel);
        SecretRow.Header = info.SecretLabel;
        bool saved = _settings.ControllerSecretProtected.Length > 0;
        SecretHint.Text = saved ? "Saved. Enter a new one to replace it." : $"Paste the {info.SecretLabel?.ToLowerInvariant()}";
        Hint(SecretHint, SecretBox.Password);

        ExtraGroup.Visibility = Show(info.ExtraLabel);
        ExtraRow.Header = info.ExtraLabel;
        ExtraHint.Text = info.ExtraHint;
        Hint(ExtraHint, ExtraBox.Text);

        SourceFooter.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        SourceConnect.Content = file ? "Import" : "Connect";
        SourceForget.Content = file ? "Remove" : "Disconnect";
        SourceForget.Visibility = _names.IsConfigured || _settings.NamedDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SourceStatus.Text.Length == 0)
        {
            int n = _settings.NamedDevices.Count;
            SourceStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.TextMuted");
            SourceStatus.Text = _settings.ControllerSyncedAt is DateTime t && n > 0
                ? $"{(file ? "Imported" : "Connected.")} {n} {(n == 1 ? "device" : "devices")}, updated {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("MMM d"))}."
                : file ? "Nothing imported yet." : "Not connected.";
        }

        static Visibility Show(string? label) => label is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Hint(TextBlock hint, string text) => hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) => Hint(SecretHint, SecretBox.Password);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a list of access points",
            Filter = "Device lists (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddressBox.Text = dialog.FileName;
    }

    private async void SourceConnect_Click(object sender, RoutedEventArgs e)
    {
        _settings.ControllerAddress = AddressBox.Text.Trim();
        _settings.ControllerUser = UserBox.Text.Trim();
        _settings.ControllerExtra = ExtraBox.Text.Trim();
        if (SecretBox.Password.Length > 0)
        {
            await _names.SaveSecretAsync(SecretBox.Password);
            SecretBox.Clear();
        }

        SourceConnect.IsEnabled = false;
        SourceStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.TextMuted");
        SourceStatus.Text = SelectedSource == NameSourceKind.File ? "Importing…" : "Connecting…";
        var (ok, message) = await _names.SyncAsync();
        SourceStatus.Text = message;
        SourceStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Rw.TextMuted" : "Rw.Weak");
        SourceConnect.IsEnabled = true;
        RenderSource();
        _engine.CheckNow();
    }

    private async void SourceForget_Click(object sender, RoutedEventArgs e)
    {
        await _names.ForgetSecretAsync();
        _settings.SetNamedDevices([]);
        _settings.ControllerSyncedAt = null;
        SourceStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.TextMuted");
        SourceStatus.Text = "Removed. Those names are no longer used.";
        RenderSource();
    }

    // ---- Pause ----

    private void RenderPause()
    {
        bool paused = _settings.Enabled && _settings.PausedUntil is DateTime u && u > DateTime.Now;
        PauseRow.Header = paused ? $"Paused until {_settings.PausedUntil:t}" : "Pause for an hour";
        PauseButton.Content = paused ? "Resume" : "Pause";
        PauseButton.IsEnabled = _settings.Enabled;
    }

    private void Pause_Click(object sender, RoutedEventArgs e) =>
        _settings.PausedUntil = _settings.PausedUntil is DateTime u && u > DateTime.Now ? null : DateTime.Now.AddHours(1);

    // ---- Updates ----

    private void RenderUpdate(string? status = null)
    {
        var u = _updater.Available;
        UpdateTitle.Text = u is null ? $"Roamwise {Updater.CurrentVersion}" : $"Roamwise {u.Version} is available";
        UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.TextMuted");
        UpdateStatus.Text = status ?? (u is not null
            ? $"You have {Updater.CurrentVersion}."
            : _settings.LastUpdateCheck is DateTime t ? $"Up to date. Last checked {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("MMM d"))}." : "Not checked yet.");
        UpdateNotes.Visibility = u is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.Content = u is null ? "Check for updates" : Updater.CanSelfUpdate ? "Install and restart" : "Download";
        UpdateButton.Style = (Style)FindResource(u is null ? "Rw.Button" : "Rw.AccentButton");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            if (_updater.Available is { } u)
            {
                if (!Updater.CanSelfUpdate) { Launch(u.PageUrl); return; }
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
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                ShowError(ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? "No releases have been published yet."
                    : "Couldn't reach GitHub. Check your connection and try again.");
            }
        }
        finally { UpdateButton.IsEnabled = true; }

        void ShowError(string message)
        {
            RenderUpdate(message);
            UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.Weak");
        }
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e) => Launch(_updater.Available?.PageUrl ?? Updater.ReleasesPage);

    // ---- Diagnostics ----

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save diagnostics report",
            FileName = $"roamwise-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Text files (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        DiagnoseButton.IsEnabled = false;
        DiagnoseButton.Content = "Scanning…";
        try
        {
            await Diagnostics.WriteReportAsync(dialog.FileName, () => new WlanClient());
            Launch(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or WlanException)
        {
            MessageBox.Show(this, $"Couldn't write the report: {ex.Message}", "Roamwise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            DiagnoseButton.Content = "Save report…";
            DiagnoseButton.IsEnabled = true;
        }
    }
    private void ResetName_Click(object sender, RoutedEventArgs e)
    {
        string key = (string)((Button)sender).Tag;
        _settings.ForgetName(key);
        UpdateNames();
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
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

    private static void Launch(string target) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => Launch(RepoUrl);
    private void OpenData_Click(object sender, RoutedEventArgs e) => Launch(SettingsStore.DataFolder);
    private void OpenLocation_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:privacy-location");
}
