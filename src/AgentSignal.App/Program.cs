using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AgentSignal.App.Services;
using AgentSignal.App.ViewModels;
using AgentSignal.App.Views;
using AgentSignal.Core;

namespace AgentSignal.App;

internal static class Program
{
    // Avalonia entry point.
    //   --screenshot <path>  render a static preview PNG (no display needed)
    //   --dump               print the live sessions + aggregate colour and exit (diagnostic)
    //   --timer-test         replay the §8 permission scenario through the real WorkTimer (diagnostic)
    //   --watch [seconds]    run the real reconcile loop over live files, printing the model (diagnostic)
    //   (no args)            run the always-on-top widget
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
            return Preview.Render(args[1]);

        if (args.Length >= 1 && args[0] == "--dump")
            return Dump();

        if (args.Length >= 1 && args[0] == "--timer-test")
            return TimerTest();

        if (args.Length >= 1 && args[0] == "--watch")
            return WatchLive(args.Length >= 2 && int.TryParse(args[1], out int s) ? s : 30);

        if (args.Length >= 1 && args[0] == "--config")
            return PrintConfig();

        if (args.Length >= 2 && args[0] == "--settings-demo")
            return SettingsDemo(args[1]);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Runs the *real* WidgetViewModel reconcile loop against the live session files for a few seconds,
    // printing the aggregate plus one line per expanded row each second. This is exactly the model the
    // expanded view binds to, so it shows rows appear/update/disappear as real sessions come and go.
    private static int WatchLive(int seconds)
    {
        var vm = new WidgetViewModel(live: false);
        Console.WriteLine($"watching {AgentPaths.SessionsDir} for {seconds}s (poll 250ms)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int tick = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            vm.PollOnce();
            if (tick++ % 4 == 0) // print ~1x/sec; poll 4x/sec so timers stay accurate
            {
                string aggTimer = vm.HasTimer ? vm.TimerText : "-";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] aggregate={vm.State,-6} timer={aggTimer,-7} sessions={vm.SessionCount}");
                foreach (var r in vm.Sessions)
                {
                    string mode = r.IsYellowActive ? "running" : r.IsRedActive ? "paused" : "frozen/idle";
                    Console.WriteLine($"    {r.SessionId,-38} {r.State,-6} {(r.HasTimer ? r.TimerText : "-"),-7} {mode}");
                }
            }
            System.Threading.Thread.Sleep(250);
        }
        return 0;
    }

    // Prints the persisted config as loaded fresh from disk — run in a new process after editing to
    // confirm settings survive a relaunch.
    private static int PrintConfig()
    {
        Console.WriteLine($"config file : {AgentPaths.ConfigFile}");
        Console.WriteLine(JsonSerializer.Serialize(ConfigService.Instance.Current, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"launch-on-startup registered: {StartupManager.Current.IsEnabled()}");
        return 0;
    }

    // Live-apply demo (single headless process, no restart): renders the REAL WidgetWindow, then pushes
    // settings edits through the REAL SettingsViewModel → ConfigService.Changed pipeline and re-renders
    // the same window. Also renders the toast and exercises the startup-registry toggle.
    private static int SettingsDemo(string outDir)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        // Mirror App's wiring (OnFrameworkInitializationCompleted doesn't run for a render).
        ThemeService.Apply(ConfigService.Instance.Current);
        ConfigService.Instance.Changed += () => ThemeService.Apply(ConfigService.Instance.Current);

        // Baseline so before/after is unambiguous.
        ConfigService.Instance.Update(c =>
        {
            c.GreenColor = "#22C55E"; c.YellowColor = "#F59E0B"; c.RedColor = "#EF4444";
            c.Scale = 1.0; c.Opacity = 1.0;
        });

        var vm = new WidgetViewModel(live: false) { State = AggregateState.Yellow, TimerText = "1:23" };
        var win = new WidgetWindow { DataContext = vm };
        win.Show();
        RenderWindow(win, Path.Combine(outDir, "settings-before.png"));

        // Simulate the user editing settings — flows through the real Changed pipeline, same window.
        var settings = new SettingsViewModel
        {
            YellowColor = "#3B82F6", // amber → blue (the lit dot recolours live)
            Scale = 1.5,             // whole widget grows
            Opacity = 0.45,          // whole widget fades
        };
        RenderWindow(win, Path.Combine(outDir, "settings-after.png"));
        Console.WriteLine($"live edits applied to running window: YellowColor={settings.YellowColor} Scale={settings.Scale} Opacity={settings.Opacity}");

        var toast = new ToastWindow("AgentSignal", "Agent needs permission");
        toast.Show();
        RenderWindow(toast, Path.Combine(outDir, "toast.png"));

        // Launch-on-startup writes/removes a real HKCU Run entry (verifiable with reg query).
        Console.WriteLine($"startup enabled (before): {StartupManager.Current.IsEnabled()}");
        StartupManager.Current.SetEnabled(true);
        Console.WriteLine($"startup enabled (after enable): {StartupManager.Current.IsEnabled()}");
        StartupManager.Current.SetEnabled(false);
        Console.WriteLine($"startup enabled (after disable): {StartupManager.Current.IsEnabled()}");

        Console.WriteLine("config now persisted to disk:");
        Console.WriteLine(JsonSerializer.Serialize(ConfigService.Instance.Current, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void RenderWindow(Window w, string path)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var frame = w.CaptureRenderedFrame();
        if (frame is null) { Console.Error.WriteLine($"no frame for {path}"); return; }

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using FileStream fs = File.Create(full);
        frame.Save(fs);
        Console.WriteLine($"saved {full} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
    }

    // Drives the real WorkTimer through the canonical §8 sequence with a controlled clock, printing
    // the timer at each event. Proves the pause/resume + durationMs back-credit math (yields 1:10).
    private static int TimerTest()
    {
        var timer = new WorkTimer();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        void Step(string label, string state, string evt, long? durationMs, int atSec)
        {
            var s = new SessionState
            {
                Tool = "claude",
                SessionId = "demo",
                State = state,
                Event = evt,
                DurationMs = durationMs,
                Ts = (long)(t0.AddSeconds(atSec) - DateTime.UnixEpoch).TotalSeconds,
            };
            timer.Observe(s, t0.AddSeconds(atSec));
            string mode = timer.Running ? "running" : state == "red" ? "paused" : "frozen";
            string note = durationMs is long ms ? $"  (+{ms / 1000.0:0.#}s tool back-credited)" : "";
            Console.WriteLine($"  t={atSec,4}s  {label,-20} {state,-7} {WidgetViewModel.FormatElapsed(timer.Elapsed),7}  {mode}{note}");
        }

        Console.WriteLine("§8 scenario: yellow 40s -> permission prompt (sit ~2min, approve a 30s tool) -> resume -> done");
        Console.WriteLine($"  {"",8} {"event",-20} {"state",-7} {"timer",7}");
        Step("UserPromptSubmit",   "yellow", "UserPromptSubmit",   null,  0);   // new run -> reset, start
        Step("PreToolUse",         "yellow", "PreToolUse",         null,  40);  // 40s of work so far
        Step("PermissionRequest",  "red",    "PermissionRequest",  null,  40);  // pause, hold 0:40
        Step("(waiting on you)",   "red",    "Notification",       null,  160); // still red after ~2min -> still 0:40
        Step("PostToolUse",        "yellow", "PostToolUse",       30000, 190);  // approved 30s tool finished -> 1:10
        Step("Stop",               "green",  "Stop",               null,  190); // freeze final value

        Console.WriteLine();
        Console.WriteLine("expected at PostToolUse: 1:10  = 40s yellow + 30s approved-tool exec; the ~2min wait is excluded.");
        Console.WriteLine("(real PostToolUse durations captured this session include e.g. 594ms, 6942ms, 10774ms.)");
        return 0;
    }

    private static int Dump()
    {
        var sessions = new SessionReader().ReadLive();
        Console.WriteLine($"sessions dir : {AgentPaths.SessionsDir}");
        Console.WriteLine($"live sessions: {sessions.Count}");
        foreach (var s in sessions)
            Console.WriteLine($"  {s.Tool}__{s.SessionId}  state={s.State,-6} pid={s.Pid} alive={ProcessHelper.IsAlive(s.Pid)} event={s.Event} durationMs={s.DurationMs}");
        Console.WriteLine($"aggregate    : {WidgetViewModel.Aggregate(sessions)}");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
