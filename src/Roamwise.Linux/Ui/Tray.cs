using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Roamwise.Core;
using Roamwise.Wifi;

namespace Roamwise.Linux.Ui;

/// <summary>
/// The tray icon (StatusNotifierItem: GNOME with the AppIndicator extension that Ubuntu ships, Cinnamon, KDE, XFCE).
/// Left click opens the panel; the menu has the rest.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _pause;
    private readonly NativeMenuItem _pauseHour;
    private (bool dark, State state)? _drawn;

    public enum State { Active, Paused, Idle }

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? CheckRequested;
    public event Action? PauseToggled;
    public event Action? PauseForHourRequested;
    public event Action? ExitRequested;

    public Tray()
    {
        _pause = new NativeMenuItem("Pause auto-roam");
        _pause.Click += (_, _) => PauseToggled?.Invoke();
        _pauseHour = new NativeMenuItem("Pause for 1 hour");
        _pauseHour.Click += (_, _) => PauseForHourRequested?.Invoke();

        var open = new NativeMenuItem("Open Roamwise");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var check = new NativeMenuItem("Check now");
        check.Click += (_, _) => CheckRequested?.Invoke();
        var settings = new NativeMenuItem("Settings");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var quit = new NativeMenuItem("Quit Roamwise");
        quit.Click += (_, _) => ExitRequested?.Invoke();

        _icon = new TrayIcon
        {
            ToolTipText = "Roamwise",
            Menu = new NativeMenu { Items = { open, check, _pause, _pauseHour, new NativeMenuItemSeparator(), settings, quit } },
            Icon = Render(64, darkPanel: true, State.Idle),
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    /// <param name="roaming">Auto-roam is on and not paused.</param>
    /// <param name="pausedLabel">"until 5:12 PM" while a timed pause is running.</param>
    public void Update(EngineSnapshot s, bool roaming, string? pausedLabel)
    {
        bool dark = PanelIsDark();
        var state = !roaming ? State.Paused
            : s.State == EngineState.Connected && s.IsManaged && s.CanSwitch ? State.Active
            : State.Idle;
        if (_drawn != (dark, state))
        {
            _icon.Icon = Render(64, dark, state);
            _drawn = (dark, state);
        }

        _pause.Header = roaming ? "Pause auto-roam" : "Resume auto-roam";
        _pauseHour.IsVisible = roaming;

        string paused = roaming ? "" : pausedLabel is null ? " (paused)" : $" (paused {pausedLabel})";
        _icon.ToolTipText = s.State switch
        {
            EngineState.Connected when s.Current is not null => $"Roamwise: {s.Connection!.Ssid}, {WifiText.Dbm(s.Current.Ap.Rssi)} dBm{paused}",
            EngineState.Disconnected => "Roamwise: not connected",
            EngineState.WifiOff => "Roamwise: Wi-Fi is off",
            _ => "Roamwise",
        };
    }

    /// <summary>
    /// The tray's background. GNOME's top bar is dark in every theme; elsewhere the panel follows the desktop theme.
    /// </summary>
    private static bool PanelIsDark()
    {
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) || desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase)) return true;
        return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != Avalonia.Platform.PlatformThemeVariant.Light;
    }

    /// <summary>
    /// A Wi-Fi fan: all green while Roamwise is managing the connection; otherwise in the panel's ink color with a
    /// badge cut into the corner (a pause sign when paused, grey when idle).
    /// </summary>
    public static WindowIcon Render(int size, bool darkPanel, State state) => new(RenderBitmap(size, darkPanel, state));

    public static Bitmap RenderBitmap(int size, bool darkPanel, State state)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var ink = darkPanel ? Colors.White : Color.FromRgb(0x1B, 0x1E, 0x23);
        var glyph = state == State.Active
            ? new SolidColorBrush(darkPanel ? Color.FromRgb(0x4C, 0xC9, 0x9B) : Color.FromRgb(0x1E, 0x8E, 0x66))
            : new SolidColorBrush(ink);

        double s = size;
        double d = Math.Round(s * 0.46), gap = Math.Max(1, s / 16);
        var badge = new Rect(s - d, s - d, d, d);

        using (var dc = bmp.CreateDrawingContext())
        {
            // Leave a transparent ring around the badge so it reads on any panel color.
            if (state == State.Active) DrawWifi(dc, s, glyph);
            else
            {
                var clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, s, s)), new EllipseGeometry(badge.Inflate(gap)));
                using (dc.PushGeometryClip(clip)) DrawWifi(dc, s, glyph);
            }

            if (state != State.Active)
            {
                var fill = state == State.Paused
                    ? darkPanel ? Color.FromRgb(0xC8, 0xCC, 0xD2) : Color.FromRgb(0x5C, 0x63, 0x6D)
                    : darkPanel ? Color.FromRgb(0x8A, 0x91, 0x9B) : Color.FromRgb(0x9A, 0xA1, 0xAA);
                dc.DrawEllipse(new SolidColorBrush(fill), null, badge.Center, d / 2, d / 2);
                if (state == State.Paused)
                {
                    var bar = new SolidColorBrush(darkPanel ? Color.FromRgb(0x1B, 0x1E, 0x23) : Colors.White);
                    double w = Math.Max(1, d * 0.16), h = d * 0.46, cx = badge.Center.X, cy = badge.Center.Y;
                    dc.FillRectangle(bar, new Rect(cx - w * 1.6, cy - h / 2, w, h));
                    dc.FillRectangle(bar, new Rect(cx + w * 0.6, cy - h / 2, w, h));
                }
            }
        }
        return bmp;
    }

    private static void DrawWifi(DrawingContext dc, double s, IBrush brush)
    {
        var pen = new Pen(brush, s * 0.095, lineCap: PenLineCap.Round);
        var center = new Point(s * 0.5, s * 0.80);
        foreach (double r in new[] { 0.18, 0.35, 0.52 })
        {
            double radius = r * s;
            // ±60° either side of straight up.
            var from = new Point(center.X - radius * Math.Sin(Math.PI / 3), center.Y - radius * Math.Cos(Math.PI / 3));
            var to = new Point(center.X + radius * Math.Sin(Math.PI / 3), from.Y);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(from, false);
                ctx.ArcTo(to, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, g);
        }
        dc.DrawEllipse(brush, null, center, s * 0.075, s * 0.075);
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
