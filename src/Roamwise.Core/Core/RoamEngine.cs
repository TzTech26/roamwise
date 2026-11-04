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
    /// <summary>False when the system lets Roamwise read access points but not switch (Linux before the one-time setup).</summary>
    public bool CanSwitch { get; init; } = true;
    /// <summary>Set when switching can't work on this system at all, so asking for permission wouldn't help.</summary>
    public string? Unsupported { get; init; }
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
    private string? _ssid;
    private readonly ActivityLog _log;
    private readonly IWifiBackend _wlan;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);

    private readonly Dictionary<string, DateTime> _blocked = new();
    private string? _streakBssid;
    private int _streak;
    private DateTime _lastRoam = DateTime.MinValue;
    private DateTime _lastScan = DateTime.MinValue;
    private string? _manualTarget;
    private (string iface, bool on)? _lowLatencyApplied;
    private WifiAdapter? _adapter;
    private IReadOnlyList<string> _saved = [];
    private string? _lastProblem;
    private readonly Dictionary<string, (AccessPoint ap, DateTime seen)> _recent = new();
    private (long bytes, DateTime at)? _traffic;
    private double _mbps;

    public EngineSnapshot Snapshot { get; private set; } = new();

    /// <summary>The rules for the network we are on (its own, or the ones for every network), read live so edits apply at once.</summary>
    private RoamRules Rules => _settings.RulesFor(_ssid);

    public event Action<EngineSnapshot>? SnapshotChanged;
    public event Action<RoamResult>? Roamed;

    public RoamEngine(AppSettings settings, ActivityLog log, IWifiBackend wifi)
    {
        _settings = settings;
        _log = log;
        _wlan = wifi;
        _wlan.ConnectionChanged += Wake;
        _settings.PropertyChanged += (_, e) =>
        {
            // Rule changes should show up in the flyout right away, not at the next scheduled check.
            if (e.PropertyName is not nameof(AppSettings.Theme) and not nameof(AppSettings.StartWithWindows)
                and not nameof(AppSettings.NotifyOnRoam) and not nameof(AppSettings.Sort) and not nameof(AppSettings.ShowAllSettings))
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

    /// <summary>Saved networks as of the latest check.</summary>
    public IReadOnlyList<string> SavedNetworks() => _saved;

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DebugLog.Write("check: start");
            try { await CheckAsync(ct); DebugLog.Write($"check: done, {Snapshot.State}, {Snapshot.Siblings.Count} radios. {Snapshot.Decision}"); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Publish(Snapshot with { State = EngineState.Problem, Problem = ex.Message, Decision = ex.Message, LastCheck = DateTime.Now });
                NoteProblem(ex.Message);
                if (ex is WifiException) DebugLog.Write($"check: failed {ex}");
                else DebugLog.Unexpected(ex);
            }

            var interval = TimeSpan.FromSeconds(Rules.ScanIntervalSeconds);
            if (Rules.SlowerOnBattery && _wlan.OnBattery()) interval *= 2;
            Publish(Snapshot with { NextCheck = DateTime.Now + interval });
            DebugLog.Write($"wait: up to {interval.TotalSeconds:0} s");

            try
            {
                bool woken = await _wake.WaitAsync(interval, ct);
                DebugLog.Write(woken ? "wait: woken early" : "wait: interval elapsed");
                // Bursts of Wi-Fi events (connect, roam, disconnect) arrive together; let them settle.
                await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
                while (_wake.CurrentCount > 0) _wake.Wait(0);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var adapter = (await _wlan.GetAdaptersAsync(ct)).OrderByDescending(a => a.IsConnected).FirstOrDefault();
        _adapter = adapter;
        if (adapter is null)
        {
            Publish(new EngineSnapshot { State = EngineState.NoAdapter, Decision = "No Wi-Fi adapter found.", LastCheck = DateTime.Now });
            return;
        }

        ApplyLowLatency(adapter);
        MeasureTraffic(adapter);
        try { _saved = (await _wlan.GetSavedNetworksAsync(adapter, ct)).Distinct().Order().ToList(); }
        catch (WifiException) { }

        IReadOnlyList<AccessPoint> all;
        try
        {
            if (DateTime.Now - _lastScan >= MinGapBetweenScans)
            {
                _lastScan = DateTime.Now;
                await _wlan.ScanAsync(adapter, ScanTimeout, ct);
            }
            all = await _wlan.GetAccessPointsAsync(adapter, ct);
        }
        catch (WifiException ex) when (ex.Kind == WifiErrorKind.LocationDenied)
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
        catch (WifiException ex) when (ex.Kind == WifiErrorKind.WifiOff)
        {
            Publish(new EngineSnapshot { State = EngineState.WifiOff, Decision = "Wi-Fi is turned off.", LastCheck = DateTime.Now });
            return;
        }

        var connection = await _wlan.GetConnectionAsync(adapter, ct);
        _ssid = connection?.Ssid;
        int nearby = all.Select(a => a.Ssid).Where(s => s.Length > 0).Distinct().Count();
        if (connection is null)
        {
            ResetStreak();
            Publish(new EngineSnapshot
            {
                State = EngineState.Disconnected,
                NearbyNetworks = nearby,
                CanSwitch = _wlan.CanSwitch,
                Unsupported = _wlan.Unsupported,
                Decision = "Not connected to Wi-Fi. Roamwise starts working once you join a network.",
                LastCheck = DateTime.Now,
            });
            return;
        }

        _lastProblem = null;
        int? liveRssi = await _wlan.GetRssiAsync(adapter, ct);
        var siblings = ReportedRadios.WithReported(SiblingsOf(connection, all, liveRssi), all, connection.Ssid, _wlan.Supports6Ghz(adapter) != false);
        var scored = siblings.Select(ap => ApScorer.Score(ap, Rules, all)).OrderByDescending(s => s.Total).ToList();
        var current = scored.First(s => Mac.Equal(s.Ap.BssidBytes, connection.BssidBytes));
        bool managed = _settings.AppliesTo(connection.Ssid);

        PruneBlocked();
        var best = scored
            .Where(s => s != current && s.Ap.HeardDirectly && !s.Ap.Stale && s.Ap.Rssi >= Rules.MinCandidateDbm && !_blocked.ContainsKey(s.Ap.Bssid))
            .MaxBy(s => s.Total);

        var snapshot = new EngineSnapshot
        {
            State = EngineState.Connected,
            Connection = connection,
            Siblings = scored,
            Current = current,
            // Only flagged in the UI once it clears the required improvement.
            Candidate = best is not null && best.Total - current.Total >= Rules.MinImprovementDb ? best : null,
            IsManaged = managed,
            CanSwitch = _wlan.CanSwitch,
            Unsupported = _wlan.Unsupported,
            NearbyNetworks = nearby,
            LastCheck = DateTime.Now,
        };

        // A switch the user asked for from the flyout.
        if (Interlocked.Exchange(ref _manualTarget, null) is string target)
        {
            var chosen = scored.FirstOrDefault(s => s.Ap.Bssid == target);
            if (chosen is not null && chosen != current && snapshot.CanSwitch)
            {
                await RoamAsync(adapter, snapshot, current, chosen, manual: true, ct);
                return;
            }
        }

        string decision = Decide(snapshot, current, best, out bool roam);
        Publish(snapshot with { Decision = decision });
        if (roam && best is not null)
            await RoamAsync(adapter, snapshot, current, best, manual: false, ct);
    }

    private string Decide(EngineSnapshot snap, ApScore current, ApScore? best, out bool roam)
    {
        roam = false;
        string ssid = snap.Connection!.Ssid;
        int heard = snap.Siblings.Count(s => s.Ap.HeardDirectly && !s.Ap.Stale);
        int others = heard - 1;

        if (others == 0)
        {
            ResetStreak();
            // The system may hide the others, so "the only one in range" would be a guess.
            if (snap.Unsupported is not null) return $"Roamwise can only see the access point you're on for {ssid} on this system.";
            return $"This is the only access point for {ssid} in range, so there's nothing to switch to.";
        }
        if (!_settings.IsRoaming)
        {
            ResetStreak();
            string paused = _settings.Enabled && _settings.PausedUntil is DateTime until ? $"Paused until {until:t}." : "Auto-roam is paused.";
            return best is not null && best.Total - current.Total >= Rules.MinImprovementDb
                ? $"{paused} {Label(best.Ap)} would be {Gain(best, current)} better."
                : $"{paused} You're on the best access point anyway.";
        }
        if (!snap.IsManaged)
        {
            ResetStreak();
            return _settings.Scope == NetworkScope.OnlyIncluded
                ? $"{ssid} isn't on your list of networks to manage."
                : $"{ssid} is on your excluded list, so Roamwise leaves it alone.";
        }
        if (Rules.OnlyWhenWeak && current.Ap.Rssi > Rules.WeakSignalDbm)
        {
            ResetStreak();
            return $"Signal is {WifiText.Dbm(current.Ap.Rssi)} dBm, above your {WifiText.Dbm(Rules.WeakSignalDbm)} dBm trigger. Staying put.";
        }
        if (best is null)
        {
            ResetStreak();
            string them = others == 1 ? "The other access point is" : $"The other {others} access points are";
            return $"{them} weaker than your {WifiText.Dbm(Rules.MinCandidateDbm)} dBm minimum. Staying put.";
        }

        double gain = best.Total - current.Total;
        if (gain < Rules.MinImprovementDb)
        {
            ResetStreak();
            return gain <= 0
                ? $"You're on the best of {heard} access points for {ssid}."
                : $"{Label(best.Ap)} is only {Gain(best, current)} better. Switching needs +{Rules.MinImprovementDb}.";
        }

        if (!snap.CanSwitch)
        {
            ResetStreak();
            return $"{Label(best.Ap)} is {Gain(best, current)} better, but Roamwise isn't allowed to switch access points yet.";
        }

        if (_streakBssid != best.Ap.Bssid) { _streakBssid = best.Ap.Bssid; _streak = 0; }
        _streak++;
        int remaining = Rules.ConfirmScans - _streak;
        if (remaining > 0)
            return $"{Label(best.Ap)} is {Gain(best, current)} better. Confirming over {Plural(remaining, "more check")}.";

        var wait = _lastRoam + TimeSpan.FromSeconds(Rules.CooldownSeconds) - DateTime.Now;
        if (wait > TimeSpan.Zero)
            return $"{Label(best.Ap)} is {Gain(best, current)} better. Waiting {Math.Ceiling(wait.TotalSeconds)} s after the last switch.";

        // A switch drops the connection for a moment; don't do it in the middle of a call or download,
        // unless the current link is so weak it's barely working anyway.
        if (Rules.HoldOffWhenBusy && _mbps >= Rules.BusyMbps && current.Ap.Rssi > -80)
            return $"{Label(best.Ap)} is {Gain(best, current)} better. Holding off while you're using the network ({_mbps:0.#} Mbps).";

        roam = true;
        return $"Switching to {Label(best.Ap)} ({Gain(best, current)} better)…";
    }

    private async Task RoamAsync(WifiAdapter adapter, EngineSnapshot snap, ApScore from, ApScore to, bool manual, CancellationToken ct)
    {
        var connection = snap.Connection!;
        Publish(snap with { IsRoaming = true, Candidate = to, Decision = $"Switching to {Label(to.Ap)}…" });

        bool attempted;
        try
        {
            attempted = await _wlan.ConnectToBssidAsync(adapter, connection, to.Ap.BssidBytes, ConnectTimeout, ct);
        }
        catch (WifiException ex)
        {
            attempted = false;
            NoteProblem(ex.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        var now = await _wlan.GetConnectionAsync(adapter, ct);
        bool success = attempted && now is not null && Mac.Equal(now.BssidBytes, to.Ap.BssidBytes);

        _lastRoam = DateTime.Now;
        ResetStreak();

        string change = $"{WifiText.Dbm(from.Ap.Rssi)} to {WifiText.Dbm(to.Ap.Rssi)} dBm";
        if (success)
        {
            _log.Add(ActivityKind.Roamed, $"Switched {connection.Ssid} to {Label(to.Ap)} ({change}){(manual ? ", by request" : "")}.");
        }
        else
        {
            _blocked[to.Ap.Bssid] = DateTime.Now + FailedApBlock;
            _log.Add(ActivityKind.RoamFailed, $"Couldn't switch {connection.Ssid} to {Label(to.Ap)}. Skipping it for 15 minutes.");
            if (now is null)
            {
                // The attempt dropped us; go back to the access point we were on.
                try { await _wlan.ConnectToBssidAsync(adapter, connection, from.Ap.BssidBytes, ConnectTimeout, ct); }
                catch (WifiException) { }
            }
        }

        Roamed?.Invoke(new RoamResult(success, connection.Ssid, from.Ap, to.Ap));
        _lastScan = DateTime.MinValue; // refresh the picture straight away
        Wake();
    }

    /// <summary>All BSSIDs broadcasting the connected SSID, one entry each, always including the current one.</summary>
    private List<AccessPoint> SiblingsOf(WifiConnection connection, IReadOnlyList<AccessPoint> all, int? liveRssi)
    {
        var siblings = all
            .Where(a => a.Ssid == connection.Ssid)
            .GroupBy(a => a.Bssid)
            .Select(g => g.MaxBy(a => a.Rssi)!)
            .ToList();

        // Scans don't catch every radio every time. Keep ones heard recently, marked stale, so the list
        // doesn't flicker; stale radios are shown but never picked automatically.
        var now = DateTime.Now;
        var keepFor = TimeSpan.FromSeconds(Math.Max(75, Rules.ScanIntervalSeconds * 2.5));
        foreach (var ap in siblings) _recent[ap.Bssid] = (ap, now);
        foreach (var key in _recent.Where(kv => now - kv.Value.seen > keepFor || kv.Value.ap.Ssid != connection.Ssid).Select(kv => kv.Key).ToList())
            _recent.Remove(key);

        string currentBssid = Mac.Format(connection.BssidBytes);
        foreach (var (bssid, (ap, _)) in _recent)
            if (!siblings.Any(s => s.Bssid == bssid))
                siblings.Add(bssid == currentBssid ? ap : ap with { Stale = true });

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

    /// <summary>Average traffic on the Wi-Fi adapter since the previous check, in Mbps.</summary>
    private void MeasureTraffic(WifiAdapter adapter)
    {
        if (_wlan.TrafficBytes(adapter) is not long bytes) { _mbps = 0; return; }
        var now = DateTime.Now;
        if (_traffic is { } prev && now > prev.at && bytes >= prev.bytes)
            _mbps = (bytes - prev.bytes) * 8 / 1_000_000.0 / (now - prev.at).TotalSeconds;
        _traffic = (bytes, now);
    }

    private void ApplyLowLatency(WifiAdapter adapter)
    {
        bool want = _settings.LowLatencyMode && _wlan.SupportsLowLatency;
        if (_lowLatencyApplied is { } applied && applied.iface == adapter.Id && applied.on == want) return;

        string? problem = _wlan.SetLowLatency(adapter, want);
        _lowLatencyApplied = (adapter.Id, want);

        if (want && problem is not null)
            _log.Add(ActivityKind.Problem, problem);
        else if (want)
            _log.Add(ActivityKind.Info, "Low-latency mode on: background scans paused.");
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

    /// <summary>"Living room (5 GHz)": the access point's name plus the band, since one device has several radios.</summary>
    private string Label(AccessPoint ap) => ap.Band == WifiBand.Unknown ? _settings.NameOf(ap) : $"{_settings.NameOf(ap)} ({WifiText.Band(ap.Band)})";

    private static string Gain(ApScore a, ApScore b) => $"+{a.Total - b.Total:0}";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static int QualityToRssi(int quality) => quality / 2 - 100;

    public void Dispose()
    {
        _stop.Cancel();
        if (_lowLatencyApplied is { on: true } && _adapter is not null) _wlan.SetLowLatency(_adapter, false);
        _wlan.Dispose();
    }
}
