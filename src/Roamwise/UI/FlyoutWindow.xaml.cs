using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.UI;

/// <summary>The panel that opens from the notification area icon.</summary>
public partial class FlyoutWindow : Window
{
    private const double EdgeGap = 12;

    private readonly IRoamEngine _engine;
    private readonly AppSettings _settings;
    private readonly ThemeService _theme;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _clock;
    private EngineSnapshot _snap = new();
    private Action? _emptyAction;

    /// <summary>When the flyout last closed by losing focus; used to ignore the tray click that caused it.</summary>
    public DateTime LastHidden { get; private set; }

    public FlyoutWindow(IRoamEngine engine, AppSettings settings, ThemeService theme, Action openSettings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _theme = theme;
        _openSettings = openSettings;
        DataContext = settings;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();

        SourceInitialized += (_, _) => ApplyEffects();
        _theme.Changed += () => { ApplyEffects(); Render(_snap); };
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) => Position();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Field.SelectedBssid is null) HideFlyout(); };

        _settings.PropertyChanged += OnSettingChanged;
        _engine.SnapshotChanged += s => Dispatcher.BeginInvoke(() => Render(s));
        DependencyPropertyDescriptor.FromProperty(SignalField.SelectedBssidProperty, typeof(SignalField))
            .AddValueChanged(Field, (_, _) => RenderDetail());

        Field.NameOf = _settings.NameOf;
        SyncRules();
        Render(_engine.Snapshot);
    }

    public void Toggle()
    {
        if (IsVisible) HideFlyout();
        else if (DateTime.Now - LastHidden > TimeSpan.FromMilliseconds(300)) ShowFlyout();
    }

    public void ShowFlyout()
    {
        Render(_engine.Snapshot);
        MaxHeight = SystemParameters.WorkArea.Height - 2 * EdgeGap;
        Opacity = 0;
        Show();
        Position();
        Activate();
        _clock.Start();
        UpdateClock();

        if (Motion.Enabled)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(TaskbarAtTop() ? -16 : 16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else Opacity = 1;
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

    private void ApplyEffects()
    {
        bool backdrop = WindowEffects.Apply(this, _theme.IsDark, WindowEffects.BackdropAcrylic);
        SetResourceReference(BackgroundProperty, backdrop ? "Rw.FlyoutTint" : "Rw.FlyoutFallback");
    }

    // Sit just above the taskbar's notification area, wherever the taskbar is docked.
    private void Position()
    {
        if (!IsVisible) return;
        var wa = SystemParameters.WorkArea;
        double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;

        Left = wa.Left > 0 ? wa.Left + EdgeGap : wa.Right - ActualWidth - EdgeGap;
        Top = TaskbarAtTop() ? wa.Top + EdgeGap : wa.Bottom - ActualHeight - EdgeGap;
        if (wa.Width >= screenW && wa.Height >= screenH) // auto-hide taskbar
            Top = wa.Bottom - ActualHeight - 56;
    }

    private static bool TaskbarAtTop() => SystemParameters.WorkArea.Top > 0;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        SyncRules();
        if (e.PropertyName == nameof(AppSettings.ApNames)) Field.Refresh();
        Render(_snap);
    });

    private void SyncRules()
    {
        Field.MinCandidateDbm = _settings.MinCandidateDbm;
        Field.WeakDbm = _settings.OnlyWhenWeak ? _settings.WeakSignalDbm : null;
    }

    private void Render(EngineSnapshot s)
    {
        _snap = s;
        var conn = s.Connection;
        bool connected = s.State == EngineState.Connected && conn is not null && s.Current is not null;

        FieldPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RssiPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CheckButton.IsEnabled = !s.IsRoaming;

        if (connected)
        {
            var ap = s.Current!.Ap;
            SsidText.Text = conn!.Ssid;
            LinkText.Visibility = Visibility.Visible;
            LinkText.Text = ap.Band == WifiBand.Unknown
                ? $"{_settings.NameOf(ap)}, {conn.RxRateMbps} Mbps"
                : $"{_settings.NameOf(ap)}, {WifiText.Band(ap.Band)} channel {ap.Channel}, {conn.RxRateMbps} Mbps";
            RssiText.Text = WifiText.Dbm(ap.Rssi);
            QualityText.Inlines.Clear();
            QualityText.Inlines.Add(new Run("dBm, "));
            QualityText.Inlines.Add(new Run(WifiText.Quality(ap.Rssi).ToLowerInvariant()) { Foreground = QualityBrush(ap.Rssi) });

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
                EngineState.NeedsLocation => ("Location access is off",
                    "Windows only shares nearby access points with apps that have location access. Turn on \"Let desktop apps access your location\" and Roamwise picks up from there.",
                    "Open location settings", () => Launch("ms-settings:privacy-location")),
                EngineState.WifiOff => ("Wi-Fi is off", "Turn Wi-Fi on to let Roamwise pick the best access point.",
                    "Open Wi-Fi settings", () => Launch("ms-settings:network-wifi")),
                EngineState.NoAdapter => ("No Wi-Fi adapter", "Roamwise didn't find a Wi-Fi adapter on this PC.", null, null),
                EngineState.Disconnected => ("Not connected",
                    $"Join a network and Roamwise starts checking its access points.{(s.NearbyNetworks > 0 ? $" {s.NearbyNetworks} networks are in range." : "")}",
                    "Open Wi-Fi settings", () => Launch("ms-settings:network-wifi")),
                EngineState.Problem => ("Something went wrong", s.Problem ?? s.Decision, "Try again", () => _engine.CheckNow()),
                _ => ("Checking Wi-Fi…", "Reading nearby access points.", null, null),
            };
            SsidText.Text = info.title;
            EmptyText.Text = info.text;
            LinkText.Text = "";
            LinkText.Visibility = Visibility.Collapsed;
            _emptyAction = info.act;
            EmptyAction.Content = info.action;
            EmptyAction.Visibility = info.action is null ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateClock();
    }

    private ApScore? Picked => _snap.Siblings.FirstOrDefault(x => x.Ap.Bssid == Field.SelectedBssid);

    private void RenderDetail()
    {
        var picked = Picked;
        if (picked is null || _snap.Current is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            RenamePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var ap = picked.Ap;
        bool isCurrent = picked == _snap.Current;
        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = _settings.NameOf(ap);
        DetailId.Text = ap.Band == WifiBand.Unknown
            ? ap.Bssid
            : $"{WifiText.Band(ap.Band)} radio {ap.Bssid}{(isCurrent ? ", connected" : "")}";

        DetailFacts.Children.Clear();
        DetailFacts.RowDefinitions.Clear();

        if (ap.HeardDirectly)
        {
            var signal = new TextBlock();
            signal.Inlines.Add(new Run($"{WifiText.Dbm(ap.Rssi)} dBm, "));
            signal.Inlines.Add(new Run(WifiText.Quality(ap.Rssi).ToLowerInvariant()) { Foreground = QualityBrush(ap.Rssi) });
            AddFact("Signal", signal);
        }
        else
        {
            AddFact("Signal", "Unknown. Your PC can't hear this radio right now; its access point lists it on another band. " +
                              "6 GHz reaches less far, so move closer to use it.");
        }

        if (ap.Band != WifiBand.Unknown)
        {
            AddFact("Channel", ap.WidthMHz > 0
                ? $"{ap.Channel} on {WifiText.Band(ap.Band)}, {ap.WidthMHz} MHz wide"
                : $"{ap.Channel} on {WifiText.Band(ap.Band)}");
            AddFact("Standard", WifiText.Generation(ap.Generation));
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

        SwitchButton.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;
        SwitchButton.IsEnabled = !_snap.IsRoaming;
        if (!ap.HeardDirectly)
        {
            AddFact("Rating", "Not rated until your PC can measure its signal.");
            return;
        }

        // Rating: the score in dBm-equivalent terms, how it compares, and what went into it.
        var rating = new TextBlock();
        rating.Inlines.Add(new Run($"{WifiText.Dbm((int)Math.Round(picked.Total))} dBm equivalent"));
        if (!isCurrent)
        {
            double diff = picked.Total - _snap.Current.Total;
            rating.Inlines.Add(new Run(diff >= 0.5 ? $", {diff:0} better than yours" : diff <= -0.5 ? $", {-diff:0} worse than yours" : ", same as yours")
            {
                Foreground = (Brush)FindResource(diff >= 0.5 ? "Rw.AccentText" : "Rw.TextMuted"),
            });
        }
        rating.Inlines.Add(new LineBreak());
        rating.Inlines.Add(new Run(picked.Explain()) { Foreground = (Brush)FindResource("Rw.TextFaint"), FontSize = 11 });
        AddFact("Rating", rating);
    }

    private void AddFact(string label, string value) => AddFact(label, new TextBlock { Text = value });

    private void AddFact(string label, TextBlock value)
    {
        int row = DetailFacts.RowDefinitions.Count;
        DetailFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = label, Style = (Style)FindResource("Rw.Caption"), Margin = new Thickness(0, 3, 0, 3) };
        Grid.SetRow(name, row);

        value.Style = (Style)FindResource("Rw.Caption");
        value.SetResourceReference(TextBlock.ForegroundProperty, "Rw.Text");
        value.Margin = new Thickness(0, 3, 0, 3);
        value.LineHeight = 17;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        DetailFacts.Children.Add(name);
        DetailFacts.Children.Add(value);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } picked) return;
        bool open = RenamePanel.Visibility != Visibility.Visible;
        RenamePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open) return;
        RenameBox.Text = _settings.NameOf(picked.Ap);
        RenameBox.SelectAll();
        RenameBox.Focus();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Picked is { } picked)
        {
            _settings.RenameAp(picked.Ap, RenameBox.Text);
            RenamePanel.Visibility = Visibility.Collapsed;
            Field.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RenamePanel.Visibility = Visibility.Collapsed;
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

    private Brush QualityBrush(int rssi) => (Brush)FindResource(rssi >= -60 ? "Rw.Strong" : rssi >= -70 ? "Rw.Fair" : "Rw.Weak");

    private static void Launch(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _openSettings();
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckedText.Text = "Checking…";
        _engine.CheckNow();
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (Field.SelectedBssid is string bssid)
        {
            _engine.SwitchTo(bssid);
            SwitchButton.IsEnabled = false;
            DecisionText.Text = "Switching…";
        }
    }

    private void EmptyAction_Click(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();
}
