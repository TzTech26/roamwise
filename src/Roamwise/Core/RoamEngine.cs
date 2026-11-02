using Roamwise.Native;
using Roamwise.Wifi;

namespace Roamwise.Core;

public enum EngineState { Starting, NoAdapter, WifiOff, NeedsLocation, Disconnected, Connected, Problem }

/// <summary>What the engine knows after its latest check. Immutable; the UI just renders it.</summary>
public sealed record EngineSnapshot
{
    public EngineState State { get; init; } = EngineState.Starting;
    public WifiConnection? Connection { get; init; }
    /// <summary>Every access point broadcasting the connected network, best score first.</summary>
    public IReadOnlyList<ApScore> Siblings { get; init; } = [];
    public ApScore? Current { get; init; }
    public ApScore? Candidate { get; init; }
    public bool IsManaged { get; init; }
    public bool IsRoaming { get; init; }
    public int NearbyNetworks { get; init; }
    /// <summary>One plain sentence on what Roamwise decided and why.</summary>
    public string Decision { get; init; } = "Starting up…";
    public string? Problem { get; init; }
    public DateTime? LastCheck { get; init; }
    public DateTime? NextCheck { get; init; }
}

public sealed record RoamResult(bool Success, string Ssid, AccessPoint From, AccessPoint To);

/// <summary>
/// The background loop: scan, rank the access points of the current network, and move to a better one
/// once it has stayed better for long enough.
/// </summary>
public sealed class RoamEngine : IRoamEngine, IDisposable
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailedApBlock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinGapBetweenScans = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;
    private readonly WlanClient _wlan;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);

    private readonly Dictionary<string, DateTime> _blocked = new();
    private string? _streakBssid;
    private int _streak;
    private DateTime _lastRoam = DateTime.MinValue;
    private DateTime _lastScan = DateTime.MinValue;
    private string? _manualTarget;
    private (Guid iface, bool on)? _lowLatencyApplied;
    private string? _lastProblem;

    public EngineSnapshot Snapshot { get; private set; } = new();

    public event Action<EngineSnapshot>? SnapshotChanged;
    public event Action<RoamResult>? Roamed;

    public RoamEngine(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
        _wlan = new WlanClient();
        _wlan.ConnectionChanged += _ => Wake();
        _settings.PropertyChanged += (_, e) =>
        {
            // Rule changes should show up in the flyout right away, not at the next scheduled check.
            if (e.PropertyName is not nameof(AppSettings.Theme) and not nameof(AppSettings.StartWithWindows)
                and not nameof(AppSettings.NotifyOnRoam))
                Wake();
        };
    }

    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

    public void CheckNow() => Wake();

    /// <summary>Move to a specific access point now, skipping the usual thresholds.</summary>
    public void SwitchTo(string bssid)
    {
        _manualTarget = bssid;
        Wake();
    }

    public IReadOnlyList<string> SavedNetworks()
    {
        try
        {
            var adapter = _wlan.GetAdapters().FirstOrDefault();
            return adapter is null ? [] : _wlan.GetProfileNames(adapter.Id).Distinct().Order().ToList();
        }
        catch (WlanException) { return []; }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Publish(Snapshot with { State = EngineState.Problem, Problem = ex.Message, Decision = ex.Message, LastCheck = DateTime.Now });
                NoteProblem(ex.Message);
            }

            var interval = TimeSpan.FromSeconds(_settings.ScanIntervalSeconds);
            if (_settings.SlowerOnBattery && OnBattery()) interval *= 2;
            Publish(Snapshot with { NextCheck = DateTime.Now + interval });

            try
            {
                await _wake.WaitAsync(interval, ct);
                // Bursts of Wi-Fi events (connect, roam, disconnect) arrive together; let them settle.
                await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
                while (_wake.CurrentCount > 0) _wake.Wait(0);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var adapter = _wlan.GetAdapters().OrderByDescending(a => a.IsConnected).FirstOrDefault();
        if (adapter is null)
        {
            Publish(new EngineSnapshot { State = EngineState.NoAdapter, Decision = "No Wi-Fi adapter found.", LastCheck = DateTime.Now });
            return;
        }

        ApplyLowLatency(adapter.Id);

        IReadOnlyList<AccessPoint> all;
        try
        {
            if (DateTime.Now - _lastScan >= MinGapBetweenScans)
            {
                _lastScan = DateTime.Now;
                await _wlan.ScanAsync(adapter.Id, ScanTimeout, ct);
            }
            all = _wlan.GetAccessPoints(adapter.Id);
        }
        catch (WlanException ex) when (ex.IsLocationDenied)
        {
            Publish(new EngineSnapshot
            {
                State = EngineState.NeedsLocation,
                Decision = "Windows hides nearby access points until location access is on for desktop apps.",
                LastCheck = DateTime.Now,
            });
            NoteProblem("Location access is off, so access points can't be read.");
            return;
        }
        catch (WlanException ex) when (ex.Code == WlanNative.ErrorNdisDot11PowerStateInvalid)
        {
            Publish(new EngineSnapshot { State = EngineState.WifiOff, Decision = "Wi-Fi is turned off.", LastCheck = DateTime.Now });
            return;
        }

        var connection = _wlan.GetConnection(adapter.Id);
        int nearby = all.Select(a => a.Ssid).Where(s => s.Length > 0).Distinct().Count();
        if (connection is null)
        {
            ResetStreak();
            Publish(new EngineSnapshot
            {
                State = EngineState.Disconnected,
                NearbyNetworks = nearby,
                Decision = "Not connected to Wi-Fi. Roamwise starts working once you join a network.",
                LastCheck = DateTime.Now,
            });
            return;
        }

        _lastProblem = null;
        var siblings = SiblingsOf(connection, all, adapter.Id);
        var scored = siblings.Select(ap => ApScorer.Score(ap, _settings, all)).OrderByDescending(s => s.Total).ToList();
        var current = scored.First(s => Mac.Equal(s.Ap.BssidBytes, connection.BssidBytes));
        bool managed = _settings.AppliesTo(connection.Ssid);

        PruneBlocked();
        var best = scored
            .Where(s => s != current && s.Ap.Rssi >= _settings.MinCandidateDbm && !_blocked.ContainsKey(s.Ap.Bssid))
            .MaxBy(s => s.Total);

        var snapshot = new EngineSnapshot
        {
            State = EngineState.Connected,
            Connection = connection,
            Siblings = scored,
            Current = current,
            Candidate = best,
            IsManaged = managed,
            NearbyNetworks = nearby,
            LastCheck = DateTime.Now,
        };

        // A switch the user asked for from the flyout.
        if (Interlocked.Exchange(ref _manualTarget, null) is string target)
        {
            var chosen = scored.FirstOrDefault(s => s.Ap.Bssid == target);
            if (chosen is not null && chosen != current)
            {
                await RoamAsync(adapter.Id, snapshot, current, chosen, manual: true, ct);
                return;
            }
        }

        string decision = Decide(snapshot, current, best, out bool roam);
        Publish(snapshot with { Decision = decision });
        if (roam && best is not null)
            await RoamAsync(adapter.Id, snapshot, current, best, manual: false, ct);
    }

    private string Decide(EngineSnapshot snap, ApScore current, ApScore? best, out bool roam)
    {
        roam = false;
        string ssid = snap.Connection!.Ssid;
        int others = snap.Siblings.Count - 1;

        if (others == 0)
        {
            ResetStreak();
            return $"This is the only access point for {ssid} in range, so there's nothing to switch to.";
        }
        if (!_settings.Enabled)
        {
            ResetStreak();
            return best is not null && best.Total - current.Total >= _settings.MinImprovementDb
                ? $"Auto-roam is paused. {best.Ap.ShortBssid} would be {Gain(best, current)} better."
                : "Auto-roam is paused. You're on the best access point anyway.";
        }
        if (!snap.IsManaged)
        {
            ResetStreak();
            return _settings.Scope == NetworkScope.OnlyIncluded
                ? $"{ssid} isn't on your list of networks to manage."
                : $"{ssid} is on your excluded list, so Roamwise leaves it alone.";
        }
        if (_settings.OnlyWhenWeak && current.Ap.Rssi > _settings.WeakSignalDbm)
        {
            ResetStreak();
            return $"Signal is {WifiText.Dbm(current.Ap.Rssi)} dBm, above your {WifiText.Dbm(_settings.WeakSignalDbm)} dBm trigger. Staying put.";
        }
        if (best is null)
        {
            ResetStreak();
            string them = others == 1 ? "The other access point is" : $"The other {others} access points are";
            return $"{them} weaker than your {WifiText.Dbm(_settings.MinCandidateDbm)} dBm minimum. Staying put.";
        }

        double gain = best.Total - current.Total;
        if (gain < _settings.MinImprovementDb)
        {
            ResetStreak();
            return gain <= 0
                ? $"You're on the best of {snap.Siblings.Count} access points for {ssid}."
                : $"{best.Ap.ShortBssid} is only {Gain(best, current)} better. Switching needs +{_settings.MinImprovementDb}.";
        }

        if (_streakBssid != best.Ap.Bssid) { _streakBssid = best.Ap.Bssid; _streak = 0; }
        _streak++;
        int remaining = _settings.ConfirmScans - _streak;
        if (remaining > 0)
            return $"{best.Ap.ShortBssid} is {Gain(best, current)} better. Confirming over {Plural(remaining, "more check")}.";

        var wait = _lastRoam + TimeSpan.FromSeconds(_settings.CooldownSeconds) - DateTime.Now;
        if (wait > TimeSpan.Zero)
            return $"{best.Ap.ShortBssid} is {Gain(best, current)} better. Waiting {Math.Ceiling(wait.TotalSeconds)} s after the last switch.";

        roam = true;
        return $"Switching to {best.Ap.ShortBssid} ({Gain(best, current)} better)…";
    }

    private async Task RoamAsync(Guid iface, EngineSnapshot snap, ApScore from, ApScore to, bool manual, CancellationToken ct)
    {
        var connection = snap.Connection!;
        Publish(snap with { IsRoaming = true, Candidate = to, Decision = $"Switching to {to.Ap.ShortBssid}…" });

        bool attempted;
        try
        {
            attempted = await _wlan.ConnectToBssidAsync(iface, connection.ProfileName, to.Ap.BssidBytes, ConnectTimeout, ct);
        }
        catch (WlanException ex)
        {
            attempted = false;
            NoteProblem(ex.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        var now = _wlan.GetConnection(iface);
        bool success = attempted && now is not null && Mac.Equal(now.BssidBytes, to.Ap.BssidBytes);

        _lastRoam = DateTime.Now;
        ResetStreak();

        string change = $"{WifiText.Dbm(from.Ap.Rssi)} to {WifiText.Dbm(to.Ap.Rssi)} dBm";
        if (success)
        {
            _log.Add(ActivityKind.Roamed, $"Switched {connection.Ssid} to {to.Ap.ShortBssid}, {WifiText.Band(to.Ap.Band)} ({change}){(manual ? ", by request" : "")}.");
        }
        else
        {
            _blocked[to.Ap.Bssid] = DateTime.Now + FailedApBlock;
            _log.Add(ActivityKind.RoamFailed, $"Couldn't switch {connection.Ssid} to {to.Ap.ShortBssid}. Skipping it for 15 minutes.");
            if (now is null)
            {
                // The attempt dropped us; ask Windows to reconnect to the profile normally.
                try { await _wlan.ConnectToBssidAsync(iface, connection.ProfileName, from.Ap.BssidBytes, ConnectTimeout, ct); }
                catch (WlanException) { }
            }
        }

        Roamed?.Invoke(new RoamResult(success, connection.Ssid, from.Ap, to.Ap));
        _lastScan = DateTime.MinValue; // refresh the picture straight away
        Wake();
    }

    /// <summary>All BSSIDs broadcasting the connected SSID, one entry each, always including the current one.</summary>
    private List<AccessPoint> SiblingsOf(WifiConnection connection, IReadOnlyList<AccessPoint> all, Guid iface)
    {
        var siblings = all
            .Where(a => a.Ssid == connection.Ssid)
            .GroupBy(a => a.Bssid)
            .Select(g => g.MaxBy(a => a.Rssi)!)
            .ToList();

        int? liveRssi = _wlan.GetRssi(iface);
        int idx = siblings.FindIndex(a => Mac.Equal(a.BssidBytes, connection.BssidBytes));
        if (idx >= 0)
        {
            // The driver's live RSSI for the current link is fresher than the scan's.
            if (liveRssi is int r && r < 0) siblings[idx] = siblings[idx] with { Rssi = r };
        }
        else
        {
            siblings.Add(new AccessPoint
            {
                Ssid = connection.Ssid,
                BssidBytes = connection.BssidBytes,
                Rssi = liveRssi ?? QualityToRssi(connection.SignalQuality),
                LinkQuality = connection.SignalQuality,
                FrequencyMHz = 0,
                Band = WifiBand.Unknown,
                Channel = 0,
                WidthMHz = 20,
                Generation = WifiGeneration.Legacy,
            });
        }
        return siblings;
    }

    private void ApplyLowLatency(Guid iface)
    {
        bool want = _settings.LowLatencyMode;
        if (_lowLatencyApplied is { } applied && applied.iface == iface && applied.on == want) return;

        uint a = _wlan.SetOption(iface, WlanIntfOpcodeOption.BackgroundScan, !want);
        uint b = _wlan.SetOption(iface, WlanIntfOpcodeOption.MediaStreaming, want);
        _lowLatencyApplied = (iface, want);

        if (want && (a != 0 || b != 0))
            _log.Add(ActivityKind.Problem, $"Low-latency mode couldn't be fully applied (errors {a}, {b}).");
        else if (want)
            _log.Add(ActivityKind.Info, "Low-latency mode on: Windows background scans paused.");
    }

    private void Publish(EngineSnapshot s)
    {
        Snapshot = s;
        SnapshotChanged?.Invoke(s);
    }

    private void NoteProblem(string message)
    {
        if (_lastProblem == message) return;
        _lastProblem = message;
        _log.Add(ActivityKind.Problem, message);
    }

    private void ResetStreak()
    {
        _streakBssid = null;
        _streak = 0;
    }

    private void PruneBlocked()
    {
        foreach (var key in _blocked.Where(kv => kv.Value < DateTime.Now).Select(kv => kv.Key).ToList())
            _blocked.Remove(key);
    }

    private static string Gain(ApScore a, ApScore b) => $"+{a.Total - b.Total:0}";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static int QualityToRssi(int quality) => quality / 2 - 100;

    private static bool OnBattery() =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;

    public void Dispose()
    {
        _stop.Cancel();
        if (_lowLatencyApplied is { on: true } applied)
        {
            _wlan.SetOption(applied.iface, WlanIntfOpcodeOption.BackgroundScan, true);
            _wlan.SetOption(applied.iface, WlanIntfOpcodeOption.MediaStreaming, false);
        }
        _wlan.Dispose();
    }
}
