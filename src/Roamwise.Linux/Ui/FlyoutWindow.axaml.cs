using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Roamwise.Core;
using Roamwise.Linux.Platform;
using Roamwise.Wifi;

namespace Roamwise.Linux.Ui;

/// <summary>The panel that opens from the tray icon. Same content as the Windows flyout.</summary>
public partial class FlyoutWindow : Window
{
    private const int EdgeGap = 12;

    private readonly IRoamEngine _engine = null!;
    private readonly AppSettings _settings = null!;
    private readonly Updater _updater = null!;
    private readonly Action<string> _openSettings = null!;
    private readonly Func<Task<string?>> _allowSwitching = null!;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private EngineSnapshot _snap = new();
    private Action? _emptyAction;
    private bool _keepOpen; // while the password prompt has focus

    /// <summary>When the panel last closed by losing focus; used to ignore the tray click that caused it.</summary>
    public DateTime LastHidden { get; private set; }

    public FlyoutWindow() => InitializeComponent(); // designer

    public FlyoutWindow(IRoamEngine engine, AppSettings settings, Updater updater, Action<string> openSettings, Func<Task<string?>> allowSwitching)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _updater = updater;
        _openSettings = openSettings;
        _allowSwitching = allowSwitching;
        DataContext = settings;

        _clock.Tick += (_, _) => UpdateClock();
        _updater.UpdateAvailable += _ => Dispatcher.UIThread.Post(RenderUpdate);
        Deactivated += (_, _) => { if (!_keepOpen) HideFlyout(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && Field.SelectedBssid is null) HideFlyout(); };
        ActualThemeVariantChanged += (_, _) => Render(_snap);

        _settings.PropertyChanged += OnSettingChanged;
        _engine.SnapshotChanged += s => Dispatcher.UIThread.Post(() => Render(s));
        Field.SelectionChanged += RenderDetail;
        Field.NameOf = _settings.NameOf;
        SyncRules();
        Render(_engine.Snapshot);
        RenderUpdate();
    }

    public void Toggle()
    {
        if (IsVisible) HideFlyout();
        else if (DateTime.Now - LastHidden > TimeSpan.FromMilliseconds(300)) ShowFlyout();
    }

    public void ShowFlyout()
    {
        Render(_engine.Snapshot);
        if (Screens.Primary is { } screen) MaxHeight = screen.WorkingArea.Height / screen.Scaling - 2 * EdgeGap;
        Show();
        PlaceNearTray();
        Activate();
        _clock.Start();
        UpdateClock();
    }

    /// <summary>Opens the details for the access point Roamwise would move to (used by --demo screenshots).</summary>
    public void SelectCandidate() => Field.SelectedBssid = _snap.Candidate?.Ap.Bssid;

    public void HideFlyout()
    {
        if (!IsVisible) return;
        LastHidden = DateTime.Now;
        _clock.Stop();
        Field.SelectedBssid = null;
        Hide();
    }

    /// <summary>
    /// Next to the panel the tray sits in: top right when the work area starts below the top of the screen
    /// (GNOME's top bar), bottom right otherwise (Cinnamon, KDE, XFCE).
    /// </summary>
    private void PlaceNearTray()
    {
        if (Screens.Primary is not { } screen) return;
        var wa = screen.WorkingArea;
        double s = screen.Scaling;
        int w = (int)Math.Ceiling(Bounds.Width * s), h = (int)Math.Ceiling(Bounds.Height * s);
        int gap = (int)(EdgeGap * s);
        bool top = wa.Y > screen.Bounds.Y;
        Position = new PixelPoint(wa.Right - w - gap, top ? wa.Y + gap : wa.Bottom - h - gap);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsVisible) PlaceNearTray();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        SyncRules();
        if (e.PropertyName == nameof(AppSettings.Sort)) Field.Sort = _settings.Sort;
        Field.Refresh();
        Render(_snap);
    });

    private void SyncRules()
    {
        Field.Sort = _settings.Sort;
        Field.MinCandidateDbm = _settings.MinCandidateDbm;
        Field.WeakDbm = _settings.OnlyWhenWeak ? _settings.WeakSignalDbm : null;
    }

    private void Render(EngineSnapshot s)
    {
        _snap = s;
        var conn = s.Connection;
        bool connected = s.State == EngineState.Connected && conn is not null && s.Current is not null;

        FieldPanel.IsVisible = connected;
        RssiPanel.IsVisible = connected;
        EmptyPanel.IsVisible = !connected;
        CheckButton.IsEnabled = !s.IsRoaming;
        SetupBanner.IsVisible = !s.CanSwitch && s.State is EngineState.Connected or EngineState.Disconnected;

        if (connected)
        {
            var ap = s.Current!.Ap;
            SsidText.Text = conn!.Ssid;
            LinkText.IsVisible = true;
            LinkText.Text = ap.Band == WifiBand.Unknown
                ? $"{_settings.NameOf(ap)}, {conn.RxRateMbps} Mbps"
                : $"{_settings.NameOf(ap)}, {WifiText.Band(ap.Band)} channel {ap.Channel}, {conn.RxRateMbps} Mbps";
            RssiText.Text = WifiText.Dbm(ap.Rssi);
            QualityText.Inlines = [new Run("dBm, "), new Run(WifiText.Quality(ap.Rssi).ToLowerInvariant()) { Foreground = QualityBrush(ap.Rssi) }];

            Field.Snapshot = s;
            RenderSummary(s);
            DecisionText.Text = s.Decision;
            RenderDetail();
        }
        else
        {
            Field.Snapshot = null;
            (string title, string text, string? action, Action? act) info = s.State switch
            {
                EngineState.WifiOff => ("Wi-Fi is off", "Turn Wi-Fi on to let Roamwise pick the best access point.",
                    "Open Wi-Fi settings", Shell.OpenWifiSettings),
                EngineState.NoAdapter => ("No Wi-Fi adapter", "Roamwise didn't find a Wi-Fi adapter that NetworkManager manages.", null, null),
                EngineState.Disconnected => ("Not connected",
                    $"Join a network and Roamwise starts checking its access points.{(s.NearbyNetworks > 0 ? $" {s.NearbyNetworks} networks are in range." : "")}",
                    "Open Wi-Fi settings", Shell.OpenWifiSettings),
                EngineState.Problem => ("Something went wrong", s.Problem ?? s.Decision, "Try again", () => _engine.CheckNow()),
                _ => ("Checking Wi-Fi…", "Reading nearby access points.", null, null),
            };
            SsidText.Text = info.title;
            EmptyText.Text = info.text;
            LinkText.Text = "";
            LinkText.IsVisible = false;
            _emptyAction = info.act;
            EmptyAction.Content = info.action;
            EmptyAction.IsVisible = info.action is not null;
        }
        UpdateClock();
    }

    private ApScore? Picked => _snap.Siblings.FirstOrDefault(x => x.Ap.Bssid == Field.SelectedBssid);

    private void RenderDetail()
    {
        var picked = Picked;
        if (picked is null || _snap.Current is null)
        {
            DetailPanel.IsVisible = false;
            RenamePanel.IsVisible = false;
            return;
        }

        var ap = picked.Ap;
        bool isCurrent = picked == _snap.Current;
        DetailPanel.IsVisible = true;
        DetailName.Text = _settings.NameOf(ap);
        DetailId.Text = ap.Band == WifiBand.Unknown
            ? ap.Bssid
            : $"{WifiText.Band(ap.Band)} radio {ap.Bssid}{(isCurrent ? ", connected" : "")}";

        DetailFacts.Children.Clear();
        DetailFacts.RowDefinitions.Clear();

        if (ap.HeardDirectly)
        {
            var signal = new TextBlock
            {
                Inlines = [new Run($"{WifiText.Dbm(ap.Rssi)} dBm, "), new Run(WifiText.Quality(ap.Rssi).ToLowerInvariant()) { Foreground = QualityBrush(ap.Rssi) }],
            };
            if (ap.Stale) signal.Inlines.Add(new Run(" in an earlier scan") { Foreground = Res.Brush("Rw.TextMuted") });
            if (ap.Generation == WifiGeneration.Unknown) signal.Inlines.Add(new Run(" (estimated)") { Foreground = Res.Brush("Rw.TextMuted") });
            AddFact("Signal", signal);
        }
        else
        {
            AddFact("Signal", "Unknown. This computer can't hear this radio right now; its access point lists it on another band. " +
                              "6 GHz reaches less far, so move closer to use it.");
        }

        if (ap.Band != WifiBand.Unknown)
        {
            AddFact("Channel", ap.WidthMHz > 0
                ? $"{ap.Channel} on {WifiText.Band(ap.Band)}, {ap.WidthMHz} MHz wide"
                : $"{ap.Channel} on {WifiText.Band(ap.Band)}");
            if (ap.Generation != WifiGeneration.Unknown) AddFact("Standard", WifiText.Generation(ap.Generation));
        }

        string? load = (ap.ChannelUtilization, ap.StationCount) switch
        {
            (int u, int n) => $"{u}% airtime used, {n} {(n == 1 ? "device" : "devices")}",
            (int u, null) => $"{u}% airtime used",
            (null, int n) => $"{n} {(n == 1 ? "device" : "devices")} connected",
            _ when picked.CoChannelRadios > 0 => $"{picked.CoChannelRadios} other {(picked.CoChannelRadios == 1 ? "radio" : "radios")} on this channel",
            _ => null,
        };
        if (load is not null) AddFact("Load", load);
        if (_settings.HardwareOf(ap) is { Length: > 0 } hardware) AddFact("Hardware", hardware);

        SwitchButton.IsVisible = !isCurrent && _snap.CanSwitch;
        SwitchButton.IsEnabled = !_snap.IsRoaming;
        if (!ap.HeardDirectly)
        {
            AddFact("Rating", "Not rated until this computer can measure its signal.");
            return;
        }

        var rating = new TextBlock { Inlines = [new Run($"{WifiText.Dbm((int)Math.Round(picked.Total))} dBm equivalent")] };
        if (!isCurrent)
        {
            double diff = picked.Total - _snap.Current.Total;
            rating.Inlines.Add(new Run(diff >= 0.5 ? $", {diff:0} better than yours" : diff <= -0.5 ? $", {-diff:0} worse than yours" : ", same as yours")
            {
                Foreground = Res.Brush(diff >= 0.5 ? "Rw.AccentText" : "Rw.TextMuted"),
            });
        }
        rating.Inlines.Add(new LineBreak());
        rating.Inlines.Add(new Run(picked.Explain()) { Foreground = Res.Brush("Rw.TextFaint"), FontSize = 11 });
        AddFact("Rating", rating);
    }

    private void AddFact(string label, string value) => AddFact(label, new TextBlock { Text = value });

    private void AddFact(string label, TextBlock value)
    {
        int row = DetailFacts.RowDefinitions.Count;
        DetailFacts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var name = new TextBlock { Text = label, Margin = new Thickness(0, 3) };
        name.Classes.Add("caption");
        Grid.SetRow(name, row);

        value.Classes.Add("caption");
        value.Foreground = Res.Brush("Rw.Text");
        value.Margin = new Thickness(0, 3);
        value.LineHeight = 17;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        DetailFacts.Children.Add(name);
        DetailFacts.Children.Add(value);
    }

    private void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (Picked is not { } picked) return;
        RenamePanel.IsVisible = !RenamePanel.IsVisible;
        if (!RenamePanel.IsVisible) return;
        RenameBox.Text = _settings.NameOf(picked.Ap);
        RenameBox.SelectAll();
        RenameBox.Focus();
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Picked is { } picked)
        {
            _settings.RenameAp(picked.Ap, RenameBox.Text);
            RenamePanel.IsVisible = false;
            Field.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RenamePanel.IsVisible = false;
            Field.Focus();
            e.Handled = true;
        }
    }

    private void RenderSummary(EngineSnapshot s)
    {
        int radios = s.Siblings.Count;
        int devices = s.Siblings.Select(x => x.Ap.DeviceKey).Distinct().Count();
        RadioSummary.Text = devices == radios
            ? $"{radios} {(radios == 1 ? "access point" : "access points")}"
            : $"{radios} radios on {devices} {(devices == 1 ? "access point" : "access points")}";
    }

    private void UpdateClock()
    {
        if (_snap.IsRoaming) { CheckedText.Text = "Switching…"; return; }
        if (_snap.LastCheck is not DateTime last) { CheckedText.Text = "Checking…"; return; }
        int ago = (int)Math.Max(0, (DateTime.Now - last).TotalSeconds);
        string when = ago < 5 ? "just now" : ago < 60 ? $"{ago} s ago" : $"{ago / 60} min ago";
        CheckedText.Text = $"Checked {when}";
    }

    private static IBrush QualityBrush(int rssi) => Res.Brush(rssi >= -60 ? "Rw.Strong" : rssi >= -70 ? "Rw.Fair" : "Rw.Weak");

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        HideFlyout();
        _openSettings("general");
    }

    private void CheckNow_Click(object? sender, RoutedEventArgs e)
    {
        CheckedText.Text = "Checking…";
        _engine.CheckNow();
    }

    private void Switch_Click(object? sender, RoutedEventArgs e)
    {
        if (Field.SelectedBssid is string bssid)
        {
            _engine.SwitchTo(bssid);
            SwitchButton.IsEnabled = false;
            DecisionText.Text = "Switching…";
        }
    }

    private void EmptyAction_Click(object? sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    private async void Setup_Click(object? sender, RoutedEventArgs e)
    {
        SetupButton.IsEnabled = false;
        _keepOpen = true;
        string? problem;
        try { problem = await _allowSwitching(); }
        finally { _keepOpen = false; }
        SetupButton.IsEnabled = true;
        SetupText.Text = problem ?? "Done. Roamwise can switch access points now.";
        SetupText.Foreground = Res.Brush(problem is null ? "Rw.TextMuted" : "Rw.Weak");
        _engine.CheckNow();
    }

    private void RenderUpdate()
    {
        var u = _updater.Available;
        UpdateBanner.IsVisible = u is not null && u.Tag != _settings.SkippedVersion;
        if (u is null) return;
        UpdateTitle.Text = $"Roamwise {u.Version} is available";
        UpdateButton.Content = Updater.CanSelfUpdate ? "Install" : "Download";
    }

    private async void UpdateInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_updater.Available is not { } u) return;
        if (!Updater.CanSelfUpdate) { Shell.Open(u.PageUrl); return; }
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Installing…";
        UpdateError.IsVisible = false;
        if (await _updater.InstallAsync(u) is string error)
        {
            UpdateError.Text = error;
            UpdateError.IsVisible = true;
            UpdateButton.Content = "Try again";
            UpdateButton.IsEnabled = true;
        }
    }

    private void UpdateNotes_Click(object? sender, RoutedEventArgs e)
    {
        if (_updater.Available is { } u) Shell.Open(u.PageUrl);
    }

    private void UpdateSkip_Click(object? sender, RoutedEventArgs e)
    {
        if (_updater.Available is { } u) _settings.SkippedVersion = u.Tag;
        RenderUpdate();
    }
}
