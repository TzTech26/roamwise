using System.Drawing;
using System.Drawing.Drawing2D;
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
    private (bool dark, bool active, int size)? _drawn;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? CheckRequested;
    public event Action? PauseToggled;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9f) };
        _pause = new ToolStripMenuItem("Pause auto-roam", null, (_, _) => PauseToggled?.Invoke());
        _menu.Items.Add(new ToolStripMenuItem("Open Roamwise", null, (_, _) => OpenRequested?.Invoke()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripMenuItem("Check now", null, (_, _) => CheckRequested?.Invoke()));
        _menu.Items.Add(_pause);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => SettingsRequested?.Invoke()));
        _menu.Items.Add(new ToolStripMenuItem("Quit Roamwise", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon { Text = "Roamwise", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
        Update(new EngineSnapshot(), enabled: true, darkMenu: true);
    }

    public void Update(EngineSnapshot s, bool enabled, bool darkMenu)
    {
        bool taskbarDark = ThemeService.TaskbarDark();
        bool active = enabled && s.State == EngineState.Connected && s.IsManaged;
        int size = SystemInformation.SmallIconSize.Width;
        if (_drawn != (taskbarDark, active, size))
        {
            var old = _icon.Icon;
            _icon.Icon = Render(size, taskbarDark, active);
            if (old is not null) DestroyIcon(old.Handle);
            _drawn = (taskbarDark, active, size);
        }

        _pause.Text = enabled ? "Pause auto-roam" : "Resume auto-roam";
        _menu.Renderer = new MenuRenderer(darkMenu);
        _menu.BackColor = darkMenu ? Color.FromArgb(0x2B, 0x2E, 0x34) : Color.FromArgb(0xF9, 0xF9, 0xFA);
        _menu.ForeColor = darkMenu ? Color.FromArgb(0xE9, 0xEB, 0xEE) : Color.FromArgb(0x1B, 0x1E, 0x23);

        string tip = s.State switch
        {
            EngineState.Connected when s.Current is not null =>
                $"Roamwise: {s.Connection!.Ssid}, {WifiText.Dbm(s.Current.Ap.Rssi)} dBm{(enabled ? "" : " (paused)")}",
            EngineState.Disconnected => "Roamwise: not connected",
            EngineState.NeedsLocation => "Roamwise: needs location access",
            EngineState.WifiOff => "Roamwise: Wi-Fi is off",
            _ => "Roamwise",
        };
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    /// <summary>
    /// Wi-Fi arcs with a status dot in the corner. Drawn at the exact tray size so it stays crisp at every scale.
    /// Dimmed, with a hollow dot, when Roamwise isn't acting on the current network.
    /// </summary>
    private static Icon Render(int size, bool taskbarDark, bool active)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var ink = taskbarDark ? Color.White : Color.FromArgb(0x1B, 0x1E, 0x23);
            if (!active) ink = Color.FromArgb(150, ink);

            float s = size / 16f;
            float stroke = Math.Max(1.4f, 1.5f * s);
            using var pen = new Pen(ink, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var center = new PointF(8f * s, 13.2f * s);
            foreach (float r in new[] { 3.6f, 7.2f })
            {
                float rr = r * s;
                g.DrawArc(pen, center.X - rr, center.Y - rr, rr * 2, rr * 2, 225, 90);
            }
            using var dot = new SolidBrush(ink);
            float d = 1.4f * s;
            g.FillEllipse(dot, center.X - d, center.Y - d, d * 2, d * 2);

            // Status dot: solid when actively managing, hollow when paused or idle.
            float sd = 3.2f * s;
            var dotRect = new RectangleF(16f * s - sd * 2 - 0.5f * s, 0.5f * s, sd * 2, sd * 2);
            if (active) g.FillEllipse(new SolidBrush(ink), dotRect);
            else g.DrawEllipse(new Pen(ink, Math.Max(1f, s)), dotRect);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

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
