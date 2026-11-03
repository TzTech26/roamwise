using Avalonia;
using Roamwise.Core;
using Roamwise.Linux.Platform;

namespace Roamwise.Linux;

public static class Program
{
    public static string[] Args { get; private set; } = [];
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);

        if (Array.IndexOf(args, "--diagnose") is int di && di >= 0)
        {
            Diagnostics.WriteReportAsync(args.ElementAtOrDefault(di + 1) ?? "roamwise-diagnostics.txt", () => new LinuxWifi()).GetAwaiter().GetResult();
            return 0;
        }

        // For the CI test against mock services: switch to one BSSID and report.
        if (Array.IndexOf(args, "--roam-test") is int ri && ri >= 0) return RoamTest(args.ElementAtOrDefault(ri + 1));
        if (args.Contains("--engine-test")) return EngineTest();

        // After an update the new copy waits for the old one to exit, then removes the old file.
        if (args.SkipWhile(a => a != "--wait").Skip(1).FirstOrDefault() is string pidText && int.TryParse(pidText, out int pid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(15000); }
            catch (ArgumentException) { } // already gone
        }
        Updater.CleanUpAfterUpdate();

        // One copy at a time; launching again just opens the running copy's panel.
        bool demo = args.Contains("--demo") || !OperatingSystem.IsLinux();
        if (!demo)
        {
            Instance = new SingleInstance("roamwise");
            if (!Instance.IsFirst) return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally { Instance?.Dispose(); }
    }

    private static int RoamTest(string? target)
    {
        using var wifi = new LinuxWifi();
        var ct = CancellationToken.None;
        var adapter = wifi.GetAdaptersAsync(ct).GetAwaiter().GetResult().FirstOrDefault();
        var connection = adapter is null ? null : wifi.GetConnectionAsync(adapter, ct).GetAwaiter().GetResult();
        if (adapter is null || connection is null || Roamwise.Wifi.Mac.Parse(target) is not { } bssid)
        {
            Console.WriteLine("roam-test: no adapter, connection or BSSID");
            return 2;
        }
        bool ok = wifi.ConnectToBssidAsync(adapter, connection, bssid, TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult();
        Console.WriteLine(ok ? $"roam-test: now on {target}" : "roam-test: switch did not complete");
        return ok ? 0 : 1;
    }

    /// <summary>Runs the engine's checks for a few seconds, the way the tray app does, and reports the result.</summary>
    private static int EngineTest()
    {
        var settings = new AppSettings();
        using var engine = new RoamEngine(settings, new ActivityLog(), new LinuxWifi());
        var checks = new List<EngineSnapshot>();
        engine.SnapshotChanged += s => { lock (checks) checks.Add(s); };
        engine.Start();
        Thread.Sleep(4000);
        engine.CheckNow();
        Thread.Sleep(4000);
        lock (checks)
        {
            var last = checks.LastOrDefault();
            Console.WriteLine($"engine-test: {checks.Count} updates, state {last?.State}, {last?.Siblings.Count} radios, problem '{last?.Problem}'");
            Console.WriteLine($"engine-test: {last?.Decision}");
            return last is { State: EngineState.Connected } ? 0 : 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    public static void Crash(Exception? ex)
    {
        try { File.AppendAllText(Path.Combine(SettingsStore.DataFolder, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n"); }
        catch (IOException) { }
    }
}
