using Microsoft.Win32;

namespace Roamwise.Core;

/// <summary>Start with Windows via the per-user Run key (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Roamwise";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
