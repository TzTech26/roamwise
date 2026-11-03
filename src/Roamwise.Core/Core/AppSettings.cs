using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roamwise.Wifi;

namespace Roamwise.Core;


public enum NetworkScope { AllExceptExcluded, OnlyIncluded }

public enum ThemePreference { System, Light, Dark }

/// <summary>How the flyout orders access points.</summary>
public enum ApSort { Band, Device, Best }

/// <summary>Where Roamwise gets access point names from, besides the ones you type.</summary>
public enum NameSourceKind { None, Unifi, Omada, Meraki, Mist, Ruckus, File }

/// <summary>An access point as a controller (or an imported list) knows it.</summary>
public sealed record ControllerDevice(string Mac, string Name, string? Model, string? Maker = null);

/// <summary>Every user-facing setting. Observable so the settings window can bind straight to it.</summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private DateTime? _pausedUntil;
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
    private bool _holdOffWhenBusy = true;
    private int _busyMbps = 2;

    private int _bonus5GhzDb = 6;
    private int _bonus6GhzDb = 8;
    private bool _avoid24Ghz;
    private bool _considerLoad = true;

    private NetworkScope _scope = NetworkScope.AllExceptExcluded;
    private bool _lowLatencyMode;
    private ApSort _sort = ApSort.Band;

    private NameSourceKind _nameSource;
    private string _controllerAddress = "";
    private string _controllerUser = "";
    private string _controllerSecret = "";
    private string _controllerExtra = "";

    private bool _checkForUpdates = true;
    private bool _installUpdatesAutomatically;

    // General
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Auto-roam is paused until this time (from "Pause for 1 hour").</summary>
    public DateTime? PausedUntil { get => _pausedUntil; set => Set(ref _pausedUntil, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool NotifyOnRoam { get => _notifyOnRoam; set => Set(ref _notifyOnRoam, value); }
    public ThemePreference Theme { get => _theme; set => Set(ref _theme, value); }

    /// <summary>Auto-roam is on and no timed pause is running.</summary>
    [JsonIgnore]
    public bool IsRoaming => Enabled && !(PausedUntil > DateTime.Now);

    // When to check and when to switch
    public int ScanIntervalSeconds { get => _scanIntervalSeconds; set => Set(ref _scanIntervalSeconds, Math.Clamp(value, 10, 600)); }
    public bool SlowerOnBattery { get => _slowerOnBattery; set => Set(ref _slowerOnBattery, value); }
    public bool OnlyWhenWeak { get => _onlyWhenWeak; set => Set(ref _onlyWhenWeak, value); }
    public int WeakSignalDbm { get => _weakSignalDbm; set => Set(ref _weakSignalDbm, Math.Clamp(value, -85, -50)); }
    public int MinImprovementDb { get => _minImprovementDb; set => Set(ref _minImprovementDb, Math.Clamp(value, 3, 25)); }
    public int MinCandidateDbm { get => _minCandidateDbm; set => Set(ref _minCandidateDbm, Math.Clamp(value, -85, -50)); }
    public int ConfirmScans { get => _confirmScans; set => Set(ref _confirmScans, Math.Clamp(value, 1, 6)); }
    public int CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, Math.Clamp(value, 30, 1800)); }
    /// <summary>Don't switch while traffic is above <see cref="BusyMbps"/>, since a switch briefly drops the connection.</summary>
    public bool HoldOffWhenBusy { get => _holdOffWhenBusy; set => Set(ref _holdOffWhenBusy, value); }
    public int BusyMbps { get => _busyMbps; set => Set(ref _busyMbps, Math.Clamp(value, 1, 50)); }

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

    // Updates
    public bool CheckForUpdates { get => _checkForUpdates; set => Set(ref _checkForUpdates, value); }
    public bool InstallUpdatesAutomatically { get => _installUpdatesAutomatically; set => Set(ref _installUpdatesAutomatically, value); }
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user chose to skip.</summary>
    public string? SkippedVersion { get; set; }

    // Flyout
    public ApSort Sort { get => _sort; set => Set(ref _sort, value); }

    // ---- Access point names ----

    /// <summary>Names the user gave access points, keyed by <see cref="AccessPoint.DeviceKey"/>.</summary>
    public Dictionary<string, string> ApNames { get; set; } = new();

    public NameSourceKind NameSource { get => _nameSource; set => Set(ref _nameSource, value); }
    /// <summary>Controller URL or host (UniFi, Omada, Ruckus), cloud region host (Mist), or imported file path.</summary>
    public string ControllerAddress { get => _controllerAddress; set => Set(ref _controllerAddress, value?.Trim() ?? ""); }
    /// <summary>User name (Ruckus) or client ID (Omada).</summary>
    public string ControllerUser { get => _controllerUser; set => Set(ref _controllerUser, value?.Trim() ?? ""); }
    /// <summary>API key, token, password or client secret, encrypted for this Windows user (DPAPI).</summary>
    public string ControllerSecretProtected { get => _controllerSecret; set => Set(ref _controllerSecret, value ?? ""); }
    /// <summary>Omada ID (Omada) or organization ID (Meraki, Mist; optional).</summary>
    public string ControllerExtra { get => _controllerExtra; set => Set(ref _controllerExtra, value?.Trim() ?? ""); }
    public List<ControllerDevice> NamedDevices { get; set; } = [];
    public DateTime? ControllerSyncedAt { get; set; }

    // Settings files from 1.0 stored a UniFi controller under these names.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ControllerDevices { get => null; set { } }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnifiHost
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && ControllerAddress.Length == 0) { _nameSource = NameSourceKind.Unifi; _controllerAddress = value; } }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnifiApiKeyProtected
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && _controllerSecret.Length == 0) _controllerSecret = value; }
    }

    public void ForgetName(string deviceKey)
    {
        if (ApNames.Remove(deviceKey)) Raise(nameof(ApNames));
    }

    /// <summary>Your own name, then the controller's, then whatever the AP announces, then a short ID.</summary>
    public string NameOf(AccessPoint ap) =>
        ApNames.TryGetValue(ap.DeviceKey, out var n) ? n
        : Controller(ap)?.Name is { Length: > 0 } c ? c
        : ap.DefaultName;

    public string HardwareOf(AccessPoint ap) =>
        Controller(ap) is { Model.Length: > 0 } d ? $"{d.Maker ?? ap.Manufacturer} {d.Model}".Trim() : ap.Hardware;

    /// <summary>
    /// Finds the controller's record for a radio. Controllers list a device's base MAC; its radios use that MAC
    /// (UniFi announces it), share its middle octets, or count up a little from it, depending on the vendor.
    /// </summary>
    private ControllerDevice? Controller(AccessPoint ap)
    {
        if (NamedDevices.Count == 0) return null;
        byte[] radio = ap.DeviceMac ?? ap.BssidBytes;
        ControllerDevice? best = null;
        int bestDistance = int.MaxValue;
        foreach (var d in NamedDevices)
        {
            if (Mac.Parse(d.Mac) is not { } mac) continue;
            if (mac.AsSpan().SequenceEqual(radio)) return d;
            if (mac.AsSpan(1, 4).SequenceEqual(ap.BssidBytes.AsSpan(1, 4))) return d;
            if (mac[1] != ap.BssidBytes[1] || mac[2] != ap.BssidBytes[2]) continue;
            int distance = Math.Abs(Tail(mac) - Tail(ap.BssidBytes));
            if (distance < 64 && distance < bestDistance) { best = d; bestDistance = distance; }
        }
        return best;

        static int Tail(byte[] m) => (m[3] << 16) | (m[4] << 8) | m[5];
    }

    public void SetNamedDevices(IEnumerable<ControllerDevice> devices)
    {
        NamedDevices = devices.Where(d => Mac.Parse(d.Mac) is not null).ToList();
        ControllerSyncedAt = NamedDevices.Count > 0 ? DateTime.Now : null;
        Raise(nameof(ApNames));
    }

    /// <summary>Sets or clears (empty name) a custom name for every radio of this access point.</summary>
    public void RenameAp(AccessPoint ap, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name == ap.DefaultName) ApNames.Remove(ap.DeviceKey);
        else ApNames[ap.DeviceKey] = name;
        Raise(nameof(ApNames));
    }

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
        Raise(name);
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal void RaiseListsChanged() => Raise(nameof(Scope));
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
