using System.IO;

namespace Roamwise.Core;

/// <summary>Timestamped trace of what the engine does. Off unless Roamwise starts with --verbose.</summary>
public static class DebugLog
{
    private static readonly object Lock = new();
    private static string? _path;

    public static void Enable() => _path = Path.Combine(SettingsStore.DataFolder, "debug.log");

    public static void Write(string message)
    {
        if (_path is null) return;
        lock (Lock)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
