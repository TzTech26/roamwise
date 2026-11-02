using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.UI;

/// <summary>
/// The access points of the current network drawn on one shared signal scale, grouped by band or by device,
/// with the user's "minimum signal" and "roam below" settings marked on every bar so the roaming decision is visible.
/// </summary>
public sealed class SignalField : FrameworkElement
{
    private const double MinDbm = -90, MaxDbm = -30;
    private const double RuleLabelHeight = 18, RowHeight = 36, HeaderHeight = 24, HeaderGap = 6, AxisHeight = 18;
    private const double ValueWidth = 34;
    private const int MaxRows = 8;

    private readonly Dictionary<string, double> _shown = new();
    private readonly Dictionary<string, double> _from = new();
    private DateTime _animStart;
    private string? _hoverBssid;
    private List<Item> _layout = [];
    private int _hiddenCount;

    private abstract record Item(double Top, double Height);
    private sealed record HeaderItem(double Top, string Title, string? Detail) : Item(Top, HeaderHeight);
    private sealed record RowItem(double Top, ApScore Score, string Primary, string Secondary) : Item(Top, RowHeight);

    public SignalField()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public static readonly DependencyProperty SnapshotProperty = Register<EngineSnapshot?>(nameof(Snapshot), null, OnSnapshotChanged);
    public static readonly DependencyProperty SortProperty = Register(nameof(Sort), ApSort.Band);
    public static readonly DependencyProperty MinCandidateDbmProperty = Register(nameof(MinCandidateDbm), -72);
    public static readonly DependencyProperty WeakDbmProperty = Register<int?>(nameof(WeakDbm), null);
    public static readonly DependencyProperty SelectedBssidProperty = Register<string?>(nameof(SelectedBssid), null);
    public static readonly DependencyProperty AccentBrushProperty = Register<Brush>(nameof(AccentBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty AccentTextBrushProperty = Register<Brush>(nameof(AccentTextBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty AccentSoftBrushProperty = Register<Brush>(nameof(AccentSoftBrush), Brushes.LightBlue);
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Gray);
    public static readonly DependencyProperty NeutralBrushProperty = Register<Brush>(nameof(NeutralBrush), Brushes.Gray);
    public static readonly DependencyProperty TextBrushProperty = Register<Brush>(nameof(TextBrush), Brushes.White);
    public static readonly DependencyProperty MutedBrushProperty = Register<Brush>(nameof(MutedBrush), Brushes.Gray);
    public static readonly DependencyProperty FaintBrushProperty = Register<Brush>(nameof(FaintBrush), Brushes.Gray);
    public static readonly DependencyProperty HoverBrushProperty = Register<Brush>(nameof(HoverBrush), Brushes.Transparent);
    public static readonly DependencyProperty LineBrushProperty = Register<Brush>(nameof(LineBrush), Brushes.Gray);
    public static readonly DependencyProperty TriggerBrushProperty = Register<Brush>(nameof(TriggerBrush), Brushes.Orange);

    public EngineSnapshot? Snapshot { get => (EngineSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public ApSort Sort { get => (ApSort)GetValue(SortProperty); set => SetValue(SortProperty, value); }
    public int MinCandidateDbm { get => (int)GetValue(MinCandidateDbmProperty); set => SetValue(MinCandidateDbmProperty, value); }
    public int? WeakDbm { get => (int?)GetValue(WeakDbmProperty); set => SetValue(WeakDbmProperty, value); }
    public string? SelectedBssid { get => (string?)GetValue(SelectedBssidProperty); set => SetValue(SelectedBssidProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush AccentTextBrush { get => (Brush)GetValue(AccentTextBrushProperty); set => SetValue(AccentTextBrushProperty, value); }
    public Brush AccentSoftBrush { get => (Brush)GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush NeutralBrush { get => (Brush)GetValue(NeutralBrushProperty); set => SetValue(NeutralBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush FaintBrush { get => (Brush)GetValue(FaintBrushProperty); set => SetValue(FaintBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush TriggerBrush { get => (Brush)GetValue(TriggerBrushProperty); set => SetValue(TriggerBrushProperty, value); }

    /// <summary>How to name an access point (custom name, announced name, or a short ID).</summary>
    public Func<AccessPoint, string> NameOf { get; set; } = ap => ap.DefaultName;

    /// <summary>Call after names change.</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
        UpdateAutomationName();
    }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(SignalField),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, changed));

    private static void OnSnapshotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (SignalField)d;
        field.BeginAnimation();
        field.UpdateAutomationName();
    }

    // ---- Layout ----

    private static int BandOrder(WifiBand b) => b switch { WifiBand.Ghz6 => 0, WifiBand.Ghz5 => 1, WifiBand.Ghz24 => 2, _ => 3 };

    private static string Channel(AccessPoint ap) =>
        ap.Band == WifiBand.Unknown ? ""
        : !ap.HeardDirectly ? $"ch {ap.Channel}, out of earshot"
        : ap.Stale ? $"ch {ap.Channel}, missed last scan"
        : $"ch {ap.Channel}, {ap.WidthMHz} MHz";

    private List<Item> BuildLayout()
    {
        var all = Snapshot?.Siblings ?? [];
        // Keep the strongest-scoring radios when there are too many to show, but never drop the current one.
        var shown = all.Take(MaxRows).ToList();
        if (Snapshot?.Current is { } cur && !shown.Contains(cur) && shown.Count > 0) shown[^1] = cur;
        _hiddenCount = all.Count - shown.Count;

        var items = new List<Item>();
        double y = RuleLabelHeight;

        void AddGroup(string title, string? detail, IEnumerable<(ApScore s, string primary, string secondary)> rows)
        {
            if (items.Count > 0) y += HeaderGap;
            items.Add(new HeaderItem(y, title, detail));
            y += HeaderHeight;
            foreach (var (s, p, sec) in rows) { items.Add(new RowItem(y, s, p, sec)); y += RowHeight; }
        }

        switch (Sort)
        {
            case ApSort.Band:
                foreach (var g in shown.GroupBy(s => s.Ap.Band).OrderBy(g => BandOrder(g.Key)))
                {
                    var rows = g.OrderBy(s => s.Ap.Channel).ThenByDescending(s => s.Ap.Rssi)
                        .Select(s => (s, NameOf(s.Ap), Channel(s.Ap)));
                    AddGroup(g.Key == WifiBand.Unknown ? "Other" : WifiText.Band(g.Key), Count(g.Count(), "radio"), rows);
                }
                break;

            case ApSort.Device:
                foreach (var g in shown.GroupBy(s => s.Ap.DeviceKey).OrderByDescending(g => g.Max(s => s.Total)))
                {
                    var first = g.First().Ap;
                    var rows = g.OrderBy(s => BandOrder(s.Ap.Band)).ThenByDescending(s => s.Ap.Rssi)
                        .Select(s => (s, s.Ap.Band == WifiBand.Unknown ? "Radio" : WifiText.Band(s.Ap.Band), Channel(s.Ap)));
                    AddGroup(NameOf(first), first.Hardware.Length > 0 ? first.Hardware : null, rows);
                }
                break;

            default:
                foreach (var s in shown)
                {
                    items.Add(new RowItem(y, s, NameOf(s.Ap),
                        s.Ap.Band == WifiBand.Unknown ? ""
                        : $"{WifiText.Band(s.Ap.Band)}, ch {s.Ap.Channel}{(s.Ap.HeardDirectly ? "" : ", out of earshot")}"));
                    y += RowHeight;
                }
                break;
        }
        return items;
    }

    private static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private double ContentBottom => _layout.Count == 0 ? RuleLabelHeight + RowHeight : _layout[^1].Top + _layout[^1].Height;

    protected override Size MeasureOverride(Size available)
    {
        _layout = BuildLayout();
        double h = ContentBottom + (_hiddenCount > 0 ? 20 : 0) + AxisHeight;
        return new Size(double.IsInfinity(available.Width) ? 340 : available.Width, h);
    }

    private double X(double dbm) => (Math.Clamp(dbm, MinDbm, MaxDbm) - MinDbm) / (MaxDbm - MinDbm) * ActualWidth;

    // ---- Animation: bars glide to their new length after each scan ----

    private void BeginAnimation()
    {
        _from.Clear();
        foreach (var (k, v) in _shown) _from[k] = v;
        _animStart = DateTime.Now;
        if (Motion.Enabled)
        {
            CompositionTarget.Rendering -= OnFrame;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (Progress() >= 1) CompositionTarget.Rendering -= OnFrame;
    }

    private double Progress()
    {
        if (!Motion.Enabled) return 1;
        double t = Math.Clamp((DateTime.Now - _animStart).TotalMilliseconds / 450.0, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere
        var snap = Snapshot;
        double progress = Progress();

        // Rule labels along the top; the matching marks are drawn on every bar.
        DrawRuleLabel(dc, MinCandidateDbm, $"min {WifiText.Dbm(MinCandidateDbm)}", MutedBrush, dpi, alignRight: false);
        if (WeakDbm is int weakLabel)
            DrawRuleLabel(dc, weakLabel, $"roam below {WifiText.Dbm(weakLabel)}", TriggerBrush, dpi, alignRight: true);

        foreach (var item in _layout)
        {
            if (item is HeaderItem h)
            {
                double mid = h.Top + HeaderHeight / 2 + 1;
                var title = Text(h.Title, 12, TextBrush, dpi, bold: true);
                dc.DrawText(title, new Point(0, mid - title.Height / 2));
                if (h.Detail is not null)
                {
                    var detail = Text(h.Detail, 11, FaintBrush, dpi);
                    detail.MaxTextWidth = Math.Max(1, ActualWidth - title.Width - 10);
                    detail.Trimming = TextTrimming.CharacterEllipsis;
                    detail.MaxLineCount = 1;
                    dc.DrawText(detail, new Point(title.Width + 8, mid - detail.Height / 2));
                }
                continue;
            }

            var row = (RowItem)item;
            var ap = row.Score.Ap;
            bool isCurrent = row.Score == snap?.Current;
            bool isCandidate = row.Score == snap?.Candidate;
            bool isSelected = ap.Bssid == SelectedBssid;
            var rowRect = new Rect(-8, row.Top + 1, ActualWidth + 16, RowHeight - 2);

            if (isSelected || ap.Bssid == _hoverBssid)
                dc.DrawRoundedRectangle(HoverBrush, null, rowRect, 4, 4);
            if (isSelected && IsKeyboardFocused)
                dc.DrawRoundedRectangle(null, new Pen(TextBrush, 1.5), rowRect, 4, 4);

            if (ap.Stale) dc.PushOpacity(0.45);

            // Line 1: name, channel, tag, value.
            double textMid = row.Top + 12;
            var value = ap.HeardDirectly
                ? Text(WifiText.Dbm(ap.Rssi), 12, isCurrent ? TextBrush : MutedBrush, dpi, bold: isCurrent)
                : Text("—", 12, FaintBrush, dpi);
            dc.DrawText(value, new Point(ActualWidth - value.Width, textMid - value.Height / 2));
            double right = ActualWidth - ValueWidth - 6;

            string? tag = isCurrent ? "connected" : isCandidate ? "better" : null;
            if (tag is not null)
            {
                var t = Text(tag, 11, AccentTextBrush, dpi, bold: true);
                dc.DrawText(t, new Point(right - t.Width, textMid - t.Height / 2));
                right -= t.Width + 10;
            }

            var primary = Text(row.Primary, 12, isCurrent ? TextBrush : MutedBrush, dpi, bold: isCurrent);
            primary.MaxTextWidth = Math.Max(1, right * 0.62);
            primary.Trimming = TextTrimming.CharacterEllipsis;
            primary.MaxLineCount = 1;
            dc.DrawText(primary, new Point(0, textMid - primary.Height / 2));
            if (row.Secondary.Length > 0)
            {
                double x2 = primary.WidthIncludingTrailingWhitespace + 8;
                var secondary = Text(row.Secondary, 11, FaintBrush, dpi);
                secondary.MaxTextWidth = Math.Max(1, right - x2);
                secondary.Trimming = TextTrimming.CharacterEllipsis;
                secondary.MaxLineCount = 1;
                dc.DrawText(secondary, new Point(x2, textMid - secondary.Height / 2 + 0.5));
            }

            // Line 2: the bar.
            double barMid = row.Top + 27;
            double target = ap.Rssi;
            double start = _from.TryGetValue(ap.Bssid, out var f) ? f : MinDbm;
            double shownValue = start + (target - start) * progress;
            _shown[ap.Bssid] = shownValue;

            double barH = isCurrent ? 6 : 4;
            if (!ap.HeardDirectly)
            {
                // No reading: an empty dashed track instead of a bar.
                var dashed = new Pen(FaintBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
                dc.DrawLine(dashed, new Point(0, barMid), new Point(ActualWidth, barMid));
                continue;
            }

            dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, barMid - barH / 2, ActualWidth, barH), barH / 2, barH / 2);
            var fill = isCurrent ? AccentBrush : isCandidate ? AccentSoftBrush : NeutralBrush;
            double fillW = Math.Max(barH, X(shownValue));
            dc.DrawRoundedRectangle(fill, null, new Rect(0, barMid - barH / 2, fillW, barH), barH / 2, barH / 2);

            DrawRuleMark(dc, MinCandidateDbm, barMid, LineBrush);
            if (WeakDbm is int weak) DrawRuleMark(dc, weak, barMid, TriggerBrush);

            if (isCurrent)
                dc.DrawEllipse(AccentBrush, new Pen(TextBrush, 1.5), new Point(fillW, barMid), 5, 5);
            if (ap.Stale) dc.Pop();
        }

        if (_layout.Count == 0)
            DrawText(dc, "No access points to show yet.", 12, MutedBrush, new Point(0, RuleLabelHeight + RowHeight / 2), dpi);

        double y = ContentBottom;
        if (_hiddenCount > 0)
        {
            DrawText(dc, $"{_hiddenCount} weaker {(_hiddenCount == 1 ? "radio" : "radios")} not shown", 11, FaintBrush, new Point(0, y + 10), dpi);
            y += 20;
        }

        foreach (int tick in new[] { -90, -70, -50, -30 })
        {
            double x = X(tick);
            var ft = Text(tick == -30 ? $"{WifiText.Dbm(tick)} dBm" : WifiText.Dbm(tick), 10, FaintBrush, dpi);
            double tx = tick == -90 ? x : tick == -30 ? x - ft.Width : x - ft.Width / 2;
            dc.DrawText(ft, new Point(tx, y + 4));
        }
    }

    private void DrawRuleMark(DrawingContext dc, int dbm, double barMid, Brush brush)
    {
        double x = Math.Round(X(dbm)) + 0.5;
        dc.DrawLine(new Pen(brush, 1.5), new Point(x, barMid - 6), new Point(x, barMid + 6));
    }

    private void DrawRuleLabel(DrawingContext dc, int dbm, string label, Brush brush, double dpi, bool alignRight)
    {
        double x = Math.Round(X(dbm)) + 0.5;
        var ft = Text(label, 10, brush, dpi);
        double tx = Math.Clamp(alignRight ? x - ft.Width - 3 : x + 3, 0, ActualWidth - ft.Width);
        dc.DrawLine(new Pen(brush, 1), new Point(x, 3), new Point(x, RuleLabelHeight - 2));
        dc.DrawText(ft, new Point(tx, 2));
    }

    private void DrawText(DrawingContext dc, string s, double size, Brush brush, Point anchor, double dpi)
    {
        var ft = Text(s, size, brush, dpi);
        dc.DrawText(ft, new Point(anchor.X, anchor.Y - ft.Height / 2));
    }

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? SemiBold : Regular, size, brush, dpi);

    // ---- Input ----

    private RowItem? RowAt(Point p) =>
        _layout.OfType<RowItem>().FirstOrDefault(r => p.Y >= r.Top && p.Y < r.Top + RowHeight);

    private List<RowItem> Rows => _layout.OfType<RowItem>().ToList();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        string? hover = RowAt(e.GetPosition(this))?.Score.Ap.Bssid;
        if (hover != _hoverBssid) { _hoverBssid = hover; InvalidateVisual(); }
        Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverBssid = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (RowAt(e.GetPosition(this)) is not { } row) return;
        string bssid = row.Score.Ap.Bssid;
        SelectedBssid = SelectedBssid == bssid ? null : bssid;
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var rows = Rows;
        if (rows.Count == 0) return;
        int idx = rows.FindIndex(r => r.Score.Ap.Bssid == SelectedBssid);
        switch (e.Key)
        {
            case Key.Down: idx = Math.Min(idx + 1, rows.Count - 1); break;
            case Key.Up: idx = Math.Max(idx - 1, 0); break;
            case Key.Escape when SelectedBssid is not null: SelectedBssid = null; e.Handled = true; return;
            default: return;
        }
        SelectedBssid = rows[idx].Score.Ap.Bssid;
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    private void UpdateAutomationName()
    {
        var rows = Snapshot?.Siblings ?? [];
        string text = rows.Count == 0
            ? "No access points"
            : string.Join("; ", rows.Select(r =>
                $"{NameOf(r.Ap)}, {WifiText.Band(r.Ap.Band)}, {WifiText.Dbm(r.Ap.Rssi)} dBm{(r == Snapshot?.Current ? ", connected" : "")}"));
        AutomationProperties.SetName(this, "Access points: " + text);
    }
}
