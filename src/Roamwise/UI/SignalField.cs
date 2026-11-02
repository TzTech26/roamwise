using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.UI;

/// <summary>
/// The access points of the current network drawn on one shared signal scale, with the user's
/// "minimum signal" and "roam below" settings drawn as lines across them, so the roaming decision is visible.
/// </summary>
public sealed class SignalField : FrameworkElement
{
    private const double MinDbm = -90, MaxDbm = -30;
    private const double LabelWidth = 64, ValueWidth = 40, BandWidth = 52;
    private const double TopPad = 20, RowHeight = 28, AxisHeight = 18;
    private const int MaxRows = 6;

    private readonly Dictionary<string, double> _shown = new();
    private readonly Dictionary<string, double> _from = new();
    private DateTime _animStart;
    private int _hoverRow = -1;

    public SignalField()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public static readonly DependencyProperty SnapshotProperty = Register<EngineSnapshot?>(nameof(Snapshot), null, OnSnapshotChanged);
    public static readonly DependencyProperty MinCandidateDbmProperty = Register(nameof(MinCandidateDbm), -72);
    public static readonly DependencyProperty WeakDbmProperty = Register<int?>(nameof(WeakDbm), null);
    public static readonly DependencyProperty SelectedBssidProperty = Register<string?>(nameof(SelectedBssid), null);
    public static readonly DependencyProperty AccentBrushProperty = Register<Brush>(nameof(AccentBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty AccentSoftBrushProperty = Register<Brush>(nameof(AccentSoftBrush), Brushes.LightBlue);
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Gray);
    public static readonly DependencyProperty NeutralBrushProperty = Register<Brush>(nameof(NeutralBrush), Brushes.Gray);
    public static readonly DependencyProperty TextBrushProperty = Register<Brush>(nameof(TextBrush), Brushes.White);
    public static readonly DependencyProperty MutedBrushProperty = Register<Brush>(nameof(MutedBrush), Brushes.Gray);
    public static readonly DependencyProperty FaintBrushProperty = Register<Brush>(nameof(FaintBrush), Brushes.Gray);
    public static readonly DependencyProperty HoverBrushProperty = Register<Brush>(nameof(HoverBrush), Brushes.Transparent);
    public static readonly DependencyProperty TriggerBrushProperty = Register<Brush>(nameof(TriggerBrush), Brushes.Orange);

    public EngineSnapshot? Snapshot { get => (EngineSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public int MinCandidateDbm { get => (int)GetValue(MinCandidateDbmProperty); set => SetValue(MinCandidateDbmProperty, value); }
    public int? WeakDbm { get => (int?)GetValue(WeakDbmProperty); set => SetValue(WeakDbmProperty, value); }
    public string? SelectedBssid { get => (string?)GetValue(SelectedBssidProperty); set => SetValue(SelectedBssidProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush AccentSoftBrush { get => (Brush)GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush NeutralBrush { get => (Brush)GetValue(NeutralBrushProperty); set => SetValue(NeutralBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush FaintBrush { get => (Brush)GetValue(FaintBrushProperty); set => SetValue(FaintBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush TriggerBrush { get => (Brush)GetValue(TriggerBrushProperty); set => SetValue(TriggerBrushProperty, value); }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(SignalField),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, changed));

    private IReadOnlyList<ApScore> Rows => Snapshot?.Siblings.Take(MaxRows).ToList() ?? [];

    private int Hidden => Math.Max(0, (Snapshot?.Siblings.Count ?? 0) - MaxRows);

    private static void OnSnapshotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (SignalField)d;
        field.BeginAnimation();
        field.UpdateAutomationName();
    }

    // Bars glide from their old length to the new one after each scan, so a change in signal is easy to spot.
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

    protected override Size MeasureOverride(Size available)
    {
        int rows = Math.Max(Rows.Count, 1);
        double h = TopPad + rows * RowHeight + (Hidden > 0 ? 18 : 0) + AxisHeight;
        return new Size(double.IsInfinity(available.Width) ? 340 : available.Width, h);
    }

    private double BarLeft => LabelWidth;
    private double BarRight => ActualWidth - ValueWidth - BandWidth - 8;

    private double X(double dbm) => BarLeft + (Math.Clamp(dbm, MinDbm, MaxDbm) - MinDbm) / (MaxDbm - MinDbm) * (BarRight - BarLeft);

    protected override void OnRender(DrawingContext dc)
    {
        var rows = Rows;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere

        double fieldTop = TopPad;
        double fieldBottom = TopPad + Math.Max(rows.Count, 1) * RowHeight;
        double progress = Progress();
        var snap = Snapshot;

        // Rule lines, drawn first so the bars sit on top.
        DrawRule(dc, MinCandidateDbm, $"min {WifiText.Dbm(MinCandidateDbm)}", FaintBrush, MutedBrush, fieldTop, fieldBottom, dpi, alignRight: false);
        if (WeakDbm is int weak)
            DrawRule(dc, weak, $"roam below {WifiText.Dbm(weak)}", TriggerBrush, TriggerBrush, fieldTop, fieldBottom, dpi, alignRight: true);

        for (int i = 0; i < rows.Count; i++)
        {
            var s = rows[i];
            var ap = s.Ap;
            double top = TopPad + i * RowHeight;
            double mid = top + RowHeight / 2;
            bool isCurrent = s == snap?.Current;
            bool isCandidate = s == snap?.Candidate;
            bool isSelected = ap.Bssid == SelectedBssid;

            if (isSelected || i == _hoverRow)
                dc.DrawRoundedRectangle(HoverBrush, null, new Rect(-6, top + 1, ActualWidth + 12, RowHeight - 2), 4, 4);
            if (isSelected && IsKeyboardFocused)
                dc.DrawRoundedRectangle(null, new Pen(TextBrush, 1.5), new Rect(-6, top + 1, ActualWidth + 12, RowHeight - 2), 4, 4);

            var labelBrush = isCurrent ? TextBrush : MutedBrush;
            DrawText(dc, ap.ShortBssid, 12, labelBrush, new Point(0, mid), dpi, bold: isCurrent);

            // Animated value for this bar.
            double target = ap.Rssi;
            double start = _from.TryGetValue(ap.Bssid, out var f) ? f : MinDbm;
            double value = start + (target - start) * progress;
            _shown[ap.Bssid] = value;

            double barH = isCurrent ? 6 : 4;
            var track = new Rect(BarLeft, mid - barH / 2, BarRight - BarLeft, barH);
            dc.DrawRoundedRectangle(TrackBrush, null, track, barH / 2, barH / 2);

            var fill = isCurrent ? AccentBrush : isCandidate ? AccentSoftBrush : NeutralBrush;
            double fillW = Math.Max(barH, X(value) - BarLeft);
            dc.DrawRoundedRectangle(fill, null, new Rect(BarLeft, mid - barH / 2, fillW, barH), barH / 2, barH / 2);
            if (isCurrent)
                dc.DrawEllipse(AccentBrush, new Pen(TextBrush, 1.5), new Point(BarLeft + fillW, mid), 5, 5);

            DrawText(dc, WifiText.Dbm(ap.Rssi), 12, isCurrent ? TextBrush : MutedBrush,
                new Point(BarRight + 8 + ValueWidth, mid), dpi, alignRight: true, bold: isCurrent);
            DrawText(dc, ap.Band == WifiBand.Unknown ? "" : WifiText.Band(ap.Band), 11, FaintBrush,
                new Point(ActualWidth, mid), dpi, alignRight: true);
        }

        if (rows.Count == 0)
            DrawText(dc, "No access points to show yet.", 12, MutedBrush, new Point(0, TopPad + RowHeight / 2), dpi);

        double y = fieldBottom;
        if (Hidden > 0)
        {
            DrawText(dc, $"{Hidden} weaker {(Hidden == 1 ? "access point" : "access points")} not shown", 11, FaintBrush,
                new Point(0, y + 9), dpi);
            y += 18;
        }

        // Axis.
        foreach (int tick in new[] { -90, -70, -50, -30 })
        {
            double x = X(tick);
            var ft = Text($"{WifiText.Dbm(tick)}", 10, FaintBrush, dpi);
            double tx = tick == -90 ? x : tick == -30 ? x - ft.Width : x - ft.Width / 2;
            dc.DrawText(ft, new Point(tx, y + 4));
        }
        var unit = Text("dBm", 10, FaintBrush, dpi);
        dc.DrawText(unit, new Point(BarRight + 8 + ValueWidth - unit.Width, y + 4));
    }

    private void DrawRule(DrawingContext dc, int dbm, string label, Brush line, Brush text, double top, double bottom, double dpi, bool alignRight)
    {
        double x = Math.Round(X(dbm)) + 0.5;
        var pen = new Pen(line, 1) { DashStyle = new DashStyle([2, 3], 0) };
        dc.DrawLine(pen, new Point(x, top - 4), new Point(x, bottom));
        var ft = Text(label, 10, text, dpi);
        double tx = alignRight ? x - ft.Width - 3 : x + 3;
        tx = Math.Clamp(tx, BarLeft - 4, BarRight + ValueWidth - ft.Width);
        dc.DrawText(ft, new Point(tx, top - 17));
    }

    private void DrawText(DrawingContext dc, string s, double size, Brush brush, Point anchor, double dpi, bool alignRight = false, bool bold = false)
    {
        var ft = Text(s, size, brush, dpi, bold);
        dc.DrawText(ft, new Point(alignRight ? anchor.X - ft.Width : anchor.X, anchor.Y - ft.Height / 2));
    }

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? SemiBold : Regular, size, brush, dpi);

    private int RowAt(Point p)
    {
        int i = (int)Math.Floor((p.Y - TopPad) / RowHeight);
        return p.Y >= TopPad && i >= 0 && i < Rows.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = RowAt(e.GetPosition(this));
        if (row != _hoverRow) { _hoverRow = row; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverRow = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int row = RowAt(e.GetPosition(this));
        if (row < 0) return;
        string bssid = Rows[row].Ap.Bssid;
        SelectedBssid = SelectedBssid == bssid ? null : bssid;
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var rows = Rows;
        if (rows.Count == 0) return;
        int idx = rows.ToList().FindIndex(r => r.Ap.Bssid == SelectedBssid);
        switch (e.Key)
        {
            case Key.Down: idx = Math.Min(idx + 1, rows.Count - 1); break;
            case Key.Up: idx = Math.Max(idx - 1, 0); break;
            case Key.Escape when SelectedBssid is not null: SelectedBssid = null; e.Handled = true; return;
            default: return;
        }
        SelectedBssid = rows[idx].Ap.Bssid;
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    private void UpdateAutomationName()
    {
        var rows = Rows;
        string text = rows.Count == 0
            ? "No access points"
            : string.Join("; ", rows.Select(r => $"{r.Ap.ShortBssid} {WifiText.Dbm(r.Ap.Rssi)} dBm {WifiText.Band(r.Ap.Band)}{(r == Snapshot?.Current ? ", connected" : "")}"));
        AutomationProperties.SetName(this, "Access points: " + text);
    }
}
