using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Roamwise.Wifi;

namespace Roamwise.Linux.Ui;

/// <summary>One row in a settings card: title and description on the left, a control on the right (template in App.axaml).</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Header));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

public static class Res
{
    /// <summary>A theme brush from App.axaml, for the current light/dark variant.</summary>
    public static IBrush Brush(string key, ThemeVariant? variant = null)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, variant ?? app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
    }
}

public sealed class DbmConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => $"{WifiText.Dbm(System.Convert.ToInt32(value))} dBm";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => BindingOperations.DoNothing;
}

public sealed class PlusDbConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => $"+{System.Convert.ToInt32(value)} dB";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => BindingOperations.DoNothing;
}

public sealed class MbpsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => $"{System.Convert.ToInt32(value)} Mbps";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => BindingOperations.DoNothing;
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => Format(System.Convert.ToInt32(value));
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => BindingOperations.DoNothing;

    public static string Format(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };
}

public sealed class ChecksConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        System.Convert.ToInt32(value) is var n && n == 1 ? "1 check" : $"{n} checks";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => BindingOperations.DoNothing;
}

/// <summary>Binds a RadioButton to one value of an enum property.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is true && p is not null ? Enum.Parse(t, p.ToString()!) : BindingOperations.DoNothing;
}
