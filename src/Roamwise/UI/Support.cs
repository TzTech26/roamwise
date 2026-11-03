using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Roamwise.Core;
using Roamwise.Wifi;
using WinRtColor = Windows.UI.Color;
using Windows.UI.ViewManagement;

namespace Roamwise.UI;

/// <summary>One row in a settings card: title and description on the left, a control on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public static readonly DependencyProperty InlineProperty =
        DependencyProperty.Register(nameof(Inline), typeof(bool), typeof(SettingRow));

    /// <summary>Show the description under the header instead of on hover, for text you need while filling the row in.</summary>
    public bool Inline { get => (bool)GetValue(InlineProperty); set => SetValue(InlineProperty, value); }
}

/// <summary>Follows the Windows light/dark setting and accent color, or the user's override.</summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;
    private readonly UISettings _ui = new();

    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.Theme)) Apply(); };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
        _ui.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Apply);
    }

    public static bool SystemAppsDark() => ReadPersonalize("AppsUseLightTheme") == 0;

    public static bool TaskbarDark() => ReadPersonalize("SystemUsesLightTheme") == 0;

    private static int ReadPersonalize(string name) =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", name, 1) is int v ? v : 1;

    public void Apply()
    {
        IsDark = _settings.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemAppsDark(),
        };

        var app = Application.Current;
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Themes/Dark.xaml") == true
                                            || d.Source?.OriginalString.EndsWith("Themes/Light.xaml") == true);
        if (old is not null) dicts.Remove(old);
        dicts.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(IsDark ? "Dark" : "Light")}.xaml") });

        // Windows 11 uses the lighter accent shades on dark surfaces and the darker ones on light surfaces.
        Color fill = ToColor(_ui.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1));
        Color text = ToColor(_ui.GetColorValue(IsDark ? UIColorType.AccentLight3 : UIColorType.AccentDark2));

        // The chart uses the accent to mark "you are here". A grey accent can't carry that, so use Windows' default blue.
        if (Saturation(ToColor(_ui.GetColorValue(UIColorType.Accent))) < 0.2)
        {
            fill = IsDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
            text = IsDark ? Color.FromRgb(0x99, 0xEB, 0xFF) : Color.FromRgb(0x00, 0x3E, 0x92);
        }
        app.Resources["Rw.Accent"] = Frozen(fill);
        app.Resources["Rw.AccentText"] = Frozen(text);
        app.Resources["Rw.AccentSoft"] = Frozen(Color.FromArgb(0x70, fill.R, fill.G, fill.B));
        app.Resources["Rw.OnAccent"] = Frozen(IsDark ? Color.FromRgb(0, 0, 0) : Colors.White);

        Changed?.Invoke();
    }

    private static Color ToColor(WinRtColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static double Saturation(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        double l = (max + min) / 2;
        return max == min ? 0 : (max - min) / (1 - Math.Abs(2 * l - 1));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>DWM tweaks: dark title bar, rounded corners and the Mica/acrylic backdrop on Windows 11.</summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    public const int BackdropMica = 2;
    public const int BackdropAcrylic = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Returns true when the system backdrop is active and the window background should stay transparent.</summary>
    public static bool Apply(Window window, bool dark, int backdrop)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        if (!BackdropSupported) return false;

        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is null) return false;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }
}

public static class Motion
{
    /// <summary>Respects "Animation effects" in Windows accessibility settings.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
}

[ValueConversion(typeof(int), typeof(string))]
public sealed class DbmConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => $"{WifiText.Dbm(System.Convert.ToInt32(value))} dBm";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

[ValueConversion(typeof(int), typeof(string))]
public sealed class PlusDbConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => $"+{System.Convert.ToInt32(value)} dB";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

[ValueConversion(typeof(int), typeof(string))]
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Format(System.Convert.ToInt32(value));
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;

    public static string Format(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };
}

[ValueConversion(typeof(int), typeof(string))]
public sealed class ChecksConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        System.Convert.ToInt32(value) is var n && n == 1 ? "1 check" : $"{n} checks";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Binds a RadioButton to one value of an enum property.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}
