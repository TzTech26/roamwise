using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roamwise.Core;

public enum NetworkScope { AllExceptExcluded, OnlyIncluded }

public enum ThemePreference { System, Light, Dark }

/// <summary>Every user-facing setting. Observable so the settings window can bind straight to it.</summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private bool _startWithWindows;
    private bool _notifyOnRoam = true;
    private ThemePreference _theme = ThemePreference.System;

    private int _scanIntervalSeconds = 30;
    private bool _slowerOnBattery = true;
    private bool _onlyWhenWeak;
    private int _weakSignalDbm = -67;
    private int _minImprovementDb = 8;
    private int _minCandidateDbm = -72;
    private int _confirmScans = 2;
    private int _cooldownSeconds = 120;

    private int _bonus5GhzDb = 6;
    private int _bonus6GhzDb = 8;
    private bool _avoid24Ghz;
    private bool _considerLoad = true;

    private NetworkScope _scope = NetworkScope.AllExceptExcluded;
    private bool _lowLatencyMode;

    // General
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool NotifyOnRoam { get => _notifyOnRoam; set => Set(ref _notifyOnRoam, value); }
    public ThemePreference Theme { get => _theme; set => Set(ref _theme, value); }

    // When to check and when to switch
    public int ScanIntervalSeconds { get => _scanIntervalSeconds; set => Set(ref _scanIntervalSeconds, Math.Clamp(value, 10, 600)); }
    public bool SlowerOnBattery { get => _slowerOnBattery; set => Set(ref _slowerOnBattery, value); }
    public bool OnlyWhenWeak { get => _onlyWhenWeak; set => Set(ref _onlyWhenWeak, value); }
    public int WeakSignalDbm { get => _weakSignalDbm; set => Set(ref _weakSignalDbm, Math.Clamp(value, -85, -50)); }
    public int MinImprovementDb { get => _minImprovementDb; set => Set(ref _minImprovementDb, Math.Clamp(value, 3, 25)); }
    public int MinCandidateDbm { get => _minCandidateDbm; set => Set(ref _minCandidateDbm, Math.Clamp(value, -85, -50)); }
    public int ConfirmScans { get => _confirmScans; set => Set(ref _confirmScans, Math.Clamp(value, 1, 6)); }
    public int CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, Math.Clamp(value, 30, 1800)); }

    // How access points are ranked
    public int Bonus5GhzDb { get => _bonus5GhzDb; set => Set(ref _bonus5GhzDb, Math.Clamp(value, 0, 15)); }
    public int Bonus6GhzDb { get => _bonus6GhzDb; set => Set(ref _bonus6GhzDb, Math.Clamp(value, 0, 15)); }
    public bool Avoid24Ghz { get => _avoid24Ghz; set => Set(ref _avoid24Ghz, value); }
    public bool ConsiderLoad { get => _considerLoad; set => Set(ref _considerLoad, value); }

    // Which networks
    public NetworkScope Scope { get => _scope; set => Set(ref _scope, value); }
    public ObservableCollection<string> ExcludedNetworks { get; set; } = [];
    public ObservableCollection<string> IncludedNetworks { get; set; } = [];

    // Extra tweaks
    public bool LowLatencyMode { get => _lowLatencyMode; set => Set(ref _lowLatencyMode, value); }

    public bool AppliesTo(string ssid)
    {
        bool Listed(IEnumerable<string> list) => list.Any(n => string.Equals(n, ssid, StringComparison.OrdinalIgnoreCase));
        return Scope == NetworkScope.OnlyIncluded ? Listed(IncludedNetworks) : !Listed(ExcludedNetworks);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal void RaiseListsChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scope)));
}

/// <summary>Loads settings from %APPDATA%\Roamwise and saves them shortly after any change.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;

    public AppSettings Settings { get; }

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roamwise");

    public SettingsStore()
    {
        Directory.CreateDirectory(DataFolder);
        _path = Path.Combine(DataFolder, "settings.json");
        Settings = Load(_path);
        _saveTimer = new System.Threading.Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);

        Settings.PropertyChanged += (_, _) => QueueSave();
        Settings.ExcludedNetworks.CollectionChanged += (_, _) => { QueueSave(); Settings.RaiseListsChanged(); };
        Settings.IncludedNetworks.CollectionChanged += (_, _) => { QueueSave(); Settings.RaiseListsChanged(); };
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken file shouldn't stop the app; keep a copy and start from defaults.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    private void QueueSave() => _saveTimer.Change(400, Timeout.Infinite);

    public void Save()
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
    }
}
