using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
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
    private readonly UnifiController _unifi;

    public sealed record ActivityRow(string Glyph, Brush GlyphBrush, string When, string Message);

    public SettingsWindow(AppSettings settings, ActivityLog log, IRoamEngine engine, ThemeService theme, UnifiController unifi)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        _engine = engine;
        _theme = theme;
        _unifi = unifi;
        DataContext = settings;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";

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
        };

        UpdateSummary();
        UpdateNetworks();
        UpdateActivity();
        UpdateNames();
        UnifiHostBox.TextChanged += (_, _) => UnifiHostHint.Visibility = UnifiHostBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UnifiHostHint.Visibility = UnifiHostBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "networks" => NavNetworks,
            "activity" => NavActivity,
            "roaming" => NavRoaming,
            "names" => NavNames,
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
        if (e.PropertyName is nameof(AppSettings.ApNames) or nameof(AppSettings.UnifiApiKeyProtected)) UpdateNames();
    });

    private void OnLogChanged() => Dispatcher.BeginInvoke(UpdateActivity);

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

        bool saved = _settings.UnifiApiKeyProtected.Length > 0;
        UnifiKeyHint.Text = saved ? "Saved. Paste a new key to replace it." : "Paste your API key";
        UnifiKeyHint.Visibility = UnifiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UnifiForget.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        if (UnifiStatus.Text.Length == 0)
            UnifiStatus.Text = _settings.ControllerSyncedAt is DateTime t && saved
                ? $"Connected. {_settings.ControllerDevices.Values.Distinct().Count()} devices, last updated {t:t}."
                : "Not connected.";
    }

    private void UnifiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        UnifiKeyHint.Visibility = UnifiKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void UnifiConnect_Click(object sender, RoutedEventArgs e)
    {
        _settings.UnifiHost = UnifiHostBox.Text;
        if (UnifiKeyBox.Password.Length > 0)
        {
            _settings.UnifiApiKeyProtected = UnifiController.Protect(UnifiKeyBox.Password);
            UnifiKeyBox.Clear();
        }

        UnifiConnect.IsEnabled = false;
        UnifiStatus.Text = "Connecting…";
        var (ok, message) = await _unifi.SyncAsync();
        UnifiStatus.Text = message;
        UnifiStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Rw.TextMuted" : "Rw.Weak");
        UnifiConnect.IsEnabled = true;
        UpdateNames();
        _engine.CheckNow();
    }

    private void UnifiForget_Click(object sender, RoutedEventArgs e)
    {
        _settings.UnifiApiKeyProtected = "";
        _settings.SetControllerDevices([]);
        _settings.ControllerSyncedAt = null;
        UnifiStatus.Text = "Disconnected. UniFi names are no longer used.";
        UnifiStatus.SetResourceReference(TextBlock.ForegroundProperty, "Rw.TextMuted");
        UpdateNames();
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
