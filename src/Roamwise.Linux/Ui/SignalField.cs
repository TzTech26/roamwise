using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.Linux.Ui;

/// <summary>
/// The access points of the current network drawn on one shared signal scale, grouped by band or by device,
/// with the user's "minimum signal" and "roam below" settings marked on every bar so the roaming decision is visible.
/// Same drawing as the Windows app's SignalField.
/// </summary>
public sealed class SignalField : Control
{
    private const double MinDbm = -90, MaxDbm = -30;
    private const double RuleLabelHeight = 18, RowHeight = 36, HeaderHeight = 24, HeaderGap = 6, AxisHeight = 18;
    private const double ValueWidth = 34;
    private const int MaxRows = 8;

    private readonly Dictionary<string, double> _shown = new();
    private readonly Dictionary<string, double> _from = new();
    private DateTime _animStart;
    private bool _animating;
    private string? _hoverBssid;
    private List<Item> _layout = [];
    private int _hiddenCount;
    private EngineSnapshot? _snapshot;
    private ApSort _sort = ApSort.Band;
    private string? _selected;

    private abstract record Item(double Top, double Height);
    private sealed record HeaderItem(double Top, string Title, string? Detail) : Item(Top, HeaderHeight);
    private sealed record RowItem(double Top, ApScore Score, string Primary, string Secondary) : Item(Top, RowHeight);

    public SignalField()
    {
        Focusable = true;
        ClipToBounds = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public EngineSnapshot? Snapshot
    {
        get => _snapshot;
        set { _snapshot = value; BeginAnimation(); InvalidateMeasure(); InvalidateVisual(); }
    }

    public ApSort Sort
    {
        get => _sort;
        set { _sort = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    public int MinCandidateDbm { get; set; } = -72;
    public int? WeakDbm { get; set; }

    public string? SelectedBssid
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;

    /// <summary>How to name an access point (custom name, announced name, or a short ID).</summary>
    public Func<AccessPoint, string> NameOf { get; set; } = ap => ap.DefaultName;

    /// <summary>Call after names or rules change.</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
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

    private double W => Bounds.Width;

    private double X(double dbm) => (Math.Clamp(dbm, MinDbm, MaxDbm) - MinDbm) / (MaxDbm - MinDbm) * W;

    // ---- Animation: bars glide to their new length after each scan ----

    private void BeginAnimation()
    {
        _from.Clear();
        foreach (var (k, v) in _shown) _from[k] = v;
        _animStart = DateTime.Now;
        if (_animating) return;
        _animating = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        InvalidateVisual();
        if (Progress() < 1) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        else _animating = false;
    }

    private double Progress()
    {
        double t = Math.Clamp((DateTime.Now - _animStart).TotalMilliseconds / 450.0, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    // ---- Drawing ----

    public override void Render(DrawingContext dc)
    {
        var accent = Res.Brush("Rw.Accent", ActualThemeVariant);
        var accentText = Res.Brush("Rw.AccentText", ActualThemeVariant);
        var accentSoft = Res.Brush("Rw.AccentSoft", ActualThemeVariant);
        var track = Res.Brush("Rw.Track", ActualThemeVariant);
        var neutral = Res.Brush("Rw.BarNeutral", ActualThemeVariant);
        var text = Res.Brush("Rw.Text", ActualThemeVariant);
        var muted = Res.Brush("Rw.TextMuted", ActualThemeVariant);
        var faint = Res.Brush("Rw.TextFaint", ActualThemeVariant);
        var hover = Res.Brush("Rw.Surface", ActualThemeVariant);
        var trigger = Res.Brush("Rw.Fair", ActualThemeVariant);

        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable everywhere
        var snap = Snapshot;
        double progress = Progress();

        DrawRuleLabel(dc, MinCandidateDbm, $"min {WifiText.Dbm(MinCandidateDbm)}", muted, alignRight: false);
        if (WeakDbm is int weakLabel)
            DrawRuleLabel(dc, weakLabel, $"roam below {WifiText.Dbm(weakLabel)}", trigger, alignRight: true);

        foreach (var item in _layout)
        {
            if (item is HeaderItem h)
            {
                double mid = h.Top + HeaderHeight / 2 + 1;
                var title = Text(h.Title, 12, text, bold: true);
                dc.DrawText(title, new Point(0, mid - title.Height / 2));
                if (h.Detail is not null)
                {
                    var detail = Text(h.Detail, 11, faint);
                    detail.MaxTextWidth = Math.Max(1, W - title.Width - 10);
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
            var rowRect = new Rect(-8, row.Top + 1, W + 16, RowHeight - 2);

            if (isSelected || ap.Bssid == _hoverBssid)
                dc.DrawRectangle(hover, null, rowRect, 4, 4);
            if (isSelected && IsFocused)
                dc.DrawRectangle(null, new Pen(text, 1.5), rowRect, 4, 4);

            using (dc.PushOpacity(ap.Stale ? 0.45 : 1))
            {
                double textMid = row.Top + 12;
                var value = ap.HeardDirectly
                    ? Text(WifiText.Dbm(ap.Rssi), 12, isCurrent ? text : muted, bold: isCurrent)
                    : Text("—", 12, faint);
                dc.DrawText(value, new Point(W - value.Width, textMid - value.Height / 2));
                double right = W - ValueWidth - 6;

                string? tag = isCurrent ? "connected" : isCandidate ? "better" : null;
                if (tag is not null)
                {
                    var t = Text(tag, 11, accentText, bold: true);
                    dc.DrawText(t, new Point(right - t.Width, textMid - t.Height / 2));
                    right -= t.Width + 10;
                }

                var primary = Text(row.Primary, 12, isCurrent ? text : muted, bold: isCurrent);
                primary.MaxTextWidth = Math.Max(1, right * 0.62);
                primary.Trimming = TextTrimming.CharacterEllipsis;
                primary.MaxLineCount = 1;
                dc.DrawText(primary, new Point(0, textMid - primary.Height / 2));
                if (row.Secondary.Length > 0)
                {
                    double x2 = primary.WidthIncludingTrailingWhitespace + 8;
                    var secondary = Text(row.Secondary, 11, faint);
                    secondary.MaxTextWidth = Math.Max(1, right - x2);
                    secondary.Trimming = TextTrimming.CharacterEllipsis;
                    secondary.MaxLineCount = 1;
                    dc.DrawText(secondary, new Point(x2, textMid - secondary.Height / 2 + 0.5));
                }

                double barMid = row.Top + 27;
                double start = _from.TryGetValue(ap.Bssid, out var f) ? f : MinDbm;
                double shownValue = start + (ap.Rssi - start) * progress;
                _shown[ap.Bssid] = shownValue;

                double barH = isCurrent ? 6 : 4;
                if (!ap.HeardDirectly)
                {
                    dc.DrawLine(new Pen(faint, 1, new DashStyle([3, 3], 0)), new Point(0, barMid), new Point(W, barMid));
                    continue;
                }

                dc.DrawRectangle(track, null, new Rect(0, barMid - barH / 2, W, barH), barH / 2, barH / 2);
                var fill = isCurrent ? accent : isCandidate ? accentSoft : neutral;
                double fillW = Math.Max(barH, X(shownValue));
                dc.DrawRectangle(fill, null, new Rect(0, barMid - barH / 2, fillW, barH), barH / 2, barH / 2);

                DrawRuleMark(dc, MinCandidateDbm, barMid, faint);
                if (WeakDbm is int weak) DrawRuleMark(dc, weak, barMid, trigger);

                if (isCurrent)
                    dc.DrawEllipse(accent, new Pen(text, 1.5), new Point(fillW, barMid), 5, 5);
            }
        }

        if (_layout.Count == 0)
        {
            var empty = Text("No access points to show yet.", 12, muted);
            dc.DrawText(empty, new Point(0, RuleLabelHeight + RowHeight / 2 - empty.Height / 2));
        }

        double y = ContentBottom;
        if (_hiddenCount > 0)
        {
            var more = Text($"{_hiddenCount} weaker {(_hiddenCount == 1 ? "radio" : "radios")} not shown", 11, faint);
            dc.DrawText(more, new Point(0, y + 10 - more.Height / 2));
            y += 20;
        }

        foreach (int tick in new[] { -90, -70, -50, -30 })
        {
            double x = X(tick);
            var ft = Text(tick == -30 ? $"{WifiText.Dbm(tick)} dBm" : WifiText.Dbm(tick), 10, faint);
            double tx = tick == -90 ? x : tick == -30 ? x - ft.Width : x - ft.Width / 2;
            dc.DrawText(ft, new Point(tx, y + 4));
        }
    }

    private void DrawRuleMark(DrawingContext dc, int dbm, double barMid, IBrush brush)
    {
        double x = Math.Round(X(dbm)) + 0.5;
        dc.DrawLine(new Pen(brush, 1.5), new Point(x, barMid - 6), new Point(x, barMid + 6));
    }

    private void DrawRuleLabel(DrawingContext dc, int dbm, string label, IBrush brush, bool alignRight)
    {
        double x = Math.Round(X(dbm)) + 0.5;
        var ft = Text(label, 10, brush);
        double tx = Math.Clamp(alignRight ? x - ft.Width - 3 : x + 3, 0, Math.Max(0, W - ft.Width));
        dc.DrawLine(new Pen(brush, 1), new Point(x, 3), new Point(x, RuleLabelHeight - 2));
        dc.DrawText(ft, new Point(tx, 2));
    }

    private static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    // ---- Input ----

    private RowItem? RowAt(Point p) =>
        _layout.OfType<RowItem>().FirstOrDefault(r => p.Y >= r.Top && p.Y < r.Top + RowHeight);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        string? hover = RowAt(e.GetPosition(this))?.Score.Ap.Bssid;
        if (hover != _hoverBssid) { _hoverBssid = hover; InvalidateVisual(); }
        Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hoverBssid = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || RowAt(e.GetPosition(this)) is not { } row) return;
        string bssid = row.Score.Ap.Bssid;
        SelectedBssid = SelectedBssid == bssid ? null : bssid;
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var rows = _layout.OfType<RowItem>().ToList();
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

    protected override void OnGotFocus(GotFocusEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }
}
