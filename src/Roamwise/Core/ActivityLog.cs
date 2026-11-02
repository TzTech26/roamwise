using System.IO;
using System.Text.Json;

namespace Roamwise.Core;

public enum ActivityKind { Roamed, RoamFailed, Info, Problem }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Message);

/// <summary>Recent things Roamwise did, newest first. Persisted so the history survives restarts.</summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 200;
    private readonly object _lock = new();
    private readonly List<ActivityEntry> _entries = [];
    private readonly string _path = Path.Combine(SettingsStore.DataFolder, "activity.json");

    public event Action? Changed;

    public ActivityLog()
    {
        try
        {
            if (File.Exists(_path))
                _entries.AddRange(JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(_path)) ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public ActivityEntry? LastRoam
    {
        get { lock (_lock) return _entries.FirstOrDefault(e => e.Kind == ActivityKind.Roamed); }
    }

    public void Add(ActivityKind kind, string message)
    {
        lock (_lock)
        {
            // Collapse repeats of the same problem so a long outage doesn't flood the list.
            if (_entries.Count > 0 && _entries[0].Kind == kind && _entries[0].Message == message && kind != ActivityKind.Roamed)
                _entries[0] = _entries[0] with { Time = DateTime.Now };
            else
                _entries.Insert(0, new ActivityEntry(DateTime.Now, kind, message));

            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Persist();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) { _entries.Clear(); Persist(); }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_entries)); }
        catch (IOException) { }
    }
}
