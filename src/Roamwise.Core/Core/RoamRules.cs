using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Roamwise.Core;

public enum RulePreset { Passive, Balanced, Aggressive, Custom }

/// <summary>
/// When to check, when to switch and how access points are ranked. One set applies to every network;
/// any network can have its own copy (<see cref="AppSettings.NetworkRules"/>).
/// </summary>
public sealed class RoamRules : INotifyPropertyChanged
{
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

    // ---- Presets: ready-made switching rules, from staying put to chasing the best signal ----

    public static RoamRules Preset(RulePreset preset) => preset switch
    {
        RulePreset.Passive => new()
        {
            ScanIntervalSeconds = 60, OnlyWhenWeak = true, WeakSignalDbm = -70, MinImprovementDb = 12,
            MinCandidateDbm = -70, ConfirmScans = 3, CooldownSeconds = 600,
        },
        RulePreset.Aggressive => new()
        {
            ScanIntervalSeconds = 15, OnlyWhenWeak = false, MinImprovementDb = 5,
            MinCandidateDbm = -75, ConfirmScans = 1, CooldownSeconds = 45,
        },
        _ => new(),
    };

    public static string Describe(RulePreset preset) => preset switch
    {
        RulePreset.Passive => "Leaves a working connection alone. Looks around only once your signal gets weak, and needs a big, steady improvement.",
        RulePreset.Aggressive => "Checks often and moves as soon as another access point is clearly better. Best for walking around a large space.",
        RulePreset.Balanced => "Switches when another access point is noticeably better for a minute. Right for most people.",
        _ => "Your own mix of rules.",
    };

    /// <summary>The preset these rules match, or Custom once a switching rule is changed by hand.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RulePreset MatchingPreset =>
        new[] { RulePreset.Passive, RulePreset.Balanced, RulePreset.Aggressive }.FirstOrDefault(p => SameSwitching(Preset(p)), RulePreset.Custom);

    public void ApplyPreset(RulePreset preset)
    {
        var p = Preset(preset);
        ScanIntervalSeconds = p.ScanIntervalSeconds;
        OnlyWhenWeak = p.OnlyWhenWeak;
        WeakSignalDbm = p.WeakSignalDbm;
        MinImprovementDb = p.MinImprovementDb;
        MinCandidateDbm = p.MinCandidateDbm;
        ConfirmScans = p.ConfirmScans;
        CooldownSeconds = p.CooldownSeconds;
    }

    // Presets cover how eagerly Roamwise switches; battery, busy-network and ranking choices are left as they are.
    private bool SameSwitching(RoamRules p) =>
        ScanIntervalSeconds == p.ScanIntervalSeconds && OnlyWhenWeak == p.OnlyWhenWeak && (!OnlyWhenWeak || WeakSignalDbm == p.WeakSignalDbm)
        && MinImprovementDb == p.MinImprovementDb && MinCandidateDbm == p.MinCandidateDbm
        && ConfirmScans == p.ConfirmScans && CooldownSeconds == p.CooldownSeconds;

    public RoamRules Copy()
    {
        var copy = new RoamRules();
        copy.CopySwitchingFrom(this);
        copy.CopyRankingFrom(this);
        return copy;
    }

    public void CopySwitchingFrom(RoamRules o)
    {
        ScanIntervalSeconds = o.ScanIntervalSeconds;
        SlowerOnBattery = o.SlowerOnBattery;
        OnlyWhenWeak = o.OnlyWhenWeak;
        WeakSignalDbm = o.WeakSignalDbm;
        MinImprovementDb = o.MinImprovementDb;
        MinCandidateDbm = o.MinCandidateDbm;
        ConfirmScans = o.ConfirmScans;
        CooldownSeconds = o.CooldownSeconds;
        HoldOffWhenBusy = o.HoldOffWhenBusy;
        BusyMbps = o.BusyMbps;
    }

    public void CopyRankingFrom(RoamRules o)
    {
        Bonus5GhzDb = o.Bonus5GhzDb;
        Bonus6GhzDb = o.Bonus6GhzDb;
        Avoid24Ghz = o.Avoid24Ghz;
        ConsiderLoad = o.ConsiderLoad;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
