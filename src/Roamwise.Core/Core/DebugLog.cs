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

    private static string? _lastUnexpected;

    /// <summary>
    /// An error Roamwise didn't expect, with its stack trace. Kept in errors.log even without --verbose so a
    /// "Something went wrong" can be traced; repeats of the same error are written once.
    /// </summary>
    public static void Unexpected(Exception ex)
    {
        Write($"unexpected: {ex}");
        string text = ex.ToString();
        lock (Lock)
        {
            if (text == _lastUnexpected) return;
            _lastUnexpected = text;
            try
            {
                string path = Path.Combine(SettingsStore.DataFolder, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:u} Roamwise {Updater.CurrentVersion}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
