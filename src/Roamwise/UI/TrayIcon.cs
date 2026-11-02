using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.UI;

/// <summary>The notification area icon: left click opens the flyout, right click opens a short menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _pauseHour;
    private (bool dark, TrayState state, int size)? _drawn;

    private enum TrayState { Active, Paused, Idle }

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? CheckRequested;
    public event Action? PauseToggled;
    public event Action? PauseForHourRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9f) };
        _pause = new ToolStripMenuItem("Pause auto-roam", null, (_, _) => PauseToggled?.Invoke());
        _pauseHour = new ToolStripMenuItem("Pause for 1 hour", null, (_, _) => PauseForHourRequested?.Invoke());
        _menu.Items.Add(new ToolStripMenuItem("Open Roamwise", null, (_, _) => OpenRequested?.Invoke()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripMenuItem("Check now", null, (_, _) => CheckRequested?.Invoke()));
        _menu.Items.Add(_pause);
        _menu.Items.Add(_pauseHour);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => SettingsRequested?.Invoke()));
        _menu.Items.Add(new ToolStripMenuItem("Quit Roamwise", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon { Text = "Roamwise", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
        Update(new EngineSnapshot(), roaming: true, pausedLabel: null, darkMenu: true);
    }

    /// <param name="roaming">Auto-roam is on and not paused.</param>
    /// <param name="pausedLabel">"until 5:12 PM" while a timed pause is running.</param>
    public void Update(EngineSnapshot s, bool roaming, string? pausedLabel, bool darkMenu)
    {
        bool taskbarDark = ThemeService.TaskbarDark();
        var state = !roaming ? TrayState.Paused
            : s.State == EngineState.Connected && s.IsManaged ? TrayState.Active
            : TrayState.Idle;
        int size = TrayIconSize();
        if (_drawn != (taskbarDark, state, size))
        {
            var old = _icon.Icon;
            _icon.Icon = Render(size, taskbarDark, state);
            if (old is not null) DestroyIcon(old.Handle);
            _drawn = (taskbarDark, state, size);
        }

        _pause.Text = roaming ? "Pause auto-roam" : "Resume auto-roam";
        _pauseHour.Visible = roaming;
        _menu.Renderer = new MenuRenderer(darkMenu);
        _menu.BackColor = darkMenu ? Color.FromArgb(0x2B, 0x2E, 0x34) : Color.FromArgb(0xF9, 0xF9, 0xFA);
        _menu.ForeColor = darkMenu ? Color.FromArgb(0xE9, 0xEB, 0xEE) : Color.FromArgb(0x1B, 0x1E, 0x23);

        string paused = roaming ? "" : pausedLabel is null ? " (paused)" : $" (paused {pausedLabel})";
        string tip = s.State switch
        {
            EngineState.Connected when s.Current is not null =>
                $"Roamwise: {s.Connection!.Ssid}, {WifiText.Dbm(s.Current.Ap.Rssi)} dBm{paused}",
            EngineState.Disconnected => "Roamwise: not connected",
            EngineState.NeedsLocation => "Roamwise: needs location access",
            EngineState.WifiOff => "Roamwise: Wi-Fi is off",
            _ => "Roamwise",
        };
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    // The tray uses the system DPI, not the per-monitor one; ask Windows for the exact small-icon size.
    private static int TrayIconSize()
    {
        try { return GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem()); }
        catch (EntryPointNotFoundException) { return SystemInformation.SmallIconSize.Width; }
    }

    /// <summary>
    /// The Windows Wi-Fi glyph (so it reads as Wi-Fi at a glance) with a status badge cut into the corner:
    /// green when Roamwise is managing the connection, a pause sign when paused, grey when idle.
    /// Full-contrast ink in every state; the badge carries the state, not a faded glyph.
    /// </summary>
    private static Icon Render(int size, bool taskbarDark, TrayState state)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var ink = taskbarDark ? Color.White : Color.FromArgb(0x1B, 0x1E, 0x23);

            DrawWifi(g, size, ink);

            // Badge in the bottom-right, separated from the glyph by a transparent ring.
            float d = MathF.Round(size * 0.46f);
            float gap = Math.Max(1f, size / 16f);
            var badge = new RectangleF(size - d, size - d, d, d);
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillEllipse(clear, RectangleF.Inflate(badge, gap, gap));
                g.CompositingMode = CompositingMode.SourceOver;
            }

            var fill = state switch
            {
                TrayState.Active => Color.FromArgb(0x3F, 0xB6, 0x8B),
                TrayState.Paused => taskbarDark ? Color.FromArgb(0xC8, 0xCC, 0xD2) : Color.FromArgb(0x5C, 0x63, 0x6D),
                _ => taskbarDark ? Color.FromArgb(0x8A, 0x91, 0x9B) : Color.FromArgb(0x9A, 0xA1, 0xAA),
            };
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, badge);

            if (state == TrayState.Paused)
            {
                var bar = taskbarDark ? Color.FromArgb(0x1B, 0x1E, 0x23) : Color.White;
                using var barBrush = new SolidBrush(bar);
                float w = Math.Max(1f, d * 0.16f), h = d * 0.46f, cx = badge.X + d / 2, cy = badge.Y + d / 2;
                g.FillRectangle(barBrush, cx - w * 1.6f, cy - h / 2, w, h);
                g.FillRectangle(barBrush, cx + w * 0.6f, cy - h / 2, w, h);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawWifi(Graphics g, int size, Color ink)
    {
        // Prefer the system icon font; fall back to drawn arcs where it's missing (Windows 10).
        using var font = new Font("Segoe Fluent Icons", size, GraphicsUnit.Pixel);
        if (font.Name == "Segoe Fluent Icons")
        {
            // Fill the glyph's outline scaled to the icon's width, so it's never clipped whatever the DPI.
            using var path = new GraphicsPath();
            path.AddString("", font.FontFamily, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            float pad = size / 32f, scale = (size - 2 * pad) / bounds.Width;
            using var fit = new Matrix();
            fit.Translate(pad, pad);
            fit.Scale(scale, scale);
            fit.Translate(-bounds.X, -bounds.Y);
            path.Transform(fit);
            using var brush = new SolidBrush(ink);
            g.FillPath(brush, path);
            return;
        }

        float s = size / 16f;
        using var pen = new Pen(ink, Math.Max(1.5f, 1.6f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var center = new PointF(7f * s, 13.5f * s);
        foreach (float r in new[] { 4f, 8f, 12f })
            g.DrawArc(pen, center.X - r * s, center.Y - r * s, r * 2 * s, r * 2 * s, 225, 90);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private sealed class MenuRenderer(bool dark) : ToolStripProfessionalRenderer(new MenuColors(dark))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = dark ? Color.FromArgb(0xE9, 0xEB, 0xEE) : Color.FromArgb(0x1B, 0x1E, 0x23);
            base.OnRenderItemText(e);
        }
    }

    private sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        private Color Bg => dark ? Color.FromArgb(0x2B, 0x2E, 0x34) : Color.FromArgb(0xF9, 0xF9, 0xFA);
        private Color Hover => dark ? Color.FromArgb(0x3A, 0x3E, 0x46) : Color.FromArgb(0xEA, 0xEB, 0xEE);
        private Color Line => dark ? Color.FromArgb(0x45, 0x49, 0x52) : Color.FromArgb(0xDD, 0xDF, 0xE3);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }
}
