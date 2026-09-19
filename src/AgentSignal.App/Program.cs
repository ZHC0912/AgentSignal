using System.Linq;
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
    //   --blink-test         drive a real SessionRowViewModel to prove the green-blink start/cancel/settle
    //   --reset-test         prove the manual reset (force-green files + frozen timer, quiet, fresh next run)
    //   --label-test         prove the auto-label (folder · tool) and the per-session hide/show
    //   --liveness-test      prove dead/ghost session files are pruned (reused pid, no pid, unknown, corrupt)
    //   --watch [seconds]    run the real reconcile loop over live files, printing the model (diagnostic)
    //   --startup <on|off|status>  toggle/inspect the real launch-on-startup entry (diagnostic)
    //   (no args)            run the always-on-top widget
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
            return Preview.Render(args[1]);

        if (args.Length >= 2 && args[0] == "--layout")
            return Preview.RenderLayouts(args[1]);

        if (args.Length >= 1 && args[0] == "--anchor-test")
            return Preview.AnchorTest();

        if (args.Length >= 1 && args[0] == "--dump")
            return Dump();

        if (args.Length >= 1 && args[0] == "--timer-test")
            return TimerTest();

        if (args.Length >= 1 && args[0] == "--blink-test")
            return BlinkTest();

        if (args.Length >= 1 && args[0] == "--reset-test")
            return ResetTest();

        if (args.Length >= 1 && args[0] == "--label-test")
            return LabelTest();

        if (args.Length >= 1 && args[0] == "--liveness-test")
            return LivenessTest();

        if (args.Length >= 1 && args[0] == "--watch")
            return WatchLive(args.Length >= 2 && int.TryParse(args[1], out int s) ? s : 30);

        if (args.Length >= 1 && args[0] == "--config")
            return PrintConfig();

        if (args.Length >= 2 && args[0] == "--settings-demo")
            return SettingsDemo(args[1]);

        if (args.Length >= 1 && args[0] == "--startup")
            return StartupDiag(args.Length >= 2 ? args[1] : "status");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Exercises the real launch-on-startup manager and prints the actual OS state. On Windows this
    // adds/removes/reads the HKCU\…\Run value, so it confirms the registered command (which must use
    // the ~/.dotnet host since plain "dotnet" on PATH is 9.0 here and can't run the net10 app).
    private static int StartupDiag(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "on": StartupManager.Current.SetEnabled(true); break;
            case "off": StartupManager.Current.SetEnabled(false); break;
            case "status": break;
            default: Console.Error.WriteLine("usage: --startup <on|off|status>"); return 1;
        }
        Console.WriteLine($"launch-on-startup registered: {StartupManager.Current.IsEnabled()}");
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
                int hidden = vm.AllSessions.Count - vm.SessionCount;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] aggregate={vm.State,-6} timer={aggTimer,-7} label=\"{vm.FullLabel}\" shown={vm.SessionCount} hidden={hidden}");
                // AllSessions so hidden sessions stay visible to the diagnostic (they're still tracked).
                foreach (var r in vm.AllSessions)
                {
                    string mode = r.IsYellowActive ? "running" : r.IsRedActive ? "paused" : "frozen/idle";
                    string blink = r.IsGreenPulsing ? " blink=ON" : "";
                    string vis = r.IsShown ? " " : "H";
                    Console.WriteLine($"  {vis} {r.FullLabel,-28} {r.SessionId,-38} {r.State,-6} {(r.HasTimer ? r.TimerText : "-"),-7} {mode}{blink}");
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

        // The real Settings window, including the live SESSION TRACKING list. Two tracked sessions,
        // one of them hidden — exactly what the widget hands it (its AllSessions collection).
        var tracking = new WidgetViewModel(live: false);
        tracking.AllSessions.Add(new SessionRowViewModel("claude", "alpha")
        {
            State = AggregateState.Yellow, TimerText = "1:10",
            InitialsLabel = "C · C", FullLabel = "calorie-tracker · Claude",
        });
        tracking.AllSessions.Add(new SessionRowViewModel("antigravity", "bravo", shown: false)
        {
            State = AggregateState.Green, TimerText = "0:48",
            InitialsLabel = "M · A", FullLabel = "my-app · Antigravity",
        });
        var settingsWin = new SettingsWindow { DataContext = new SettingsViewModel(null, null, tracking) };
        settingsWin.Show();
        RenderWindow(settingsWin, Path.Combine(outDir, "settings-window.png"));

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

    // Drives the REAL SessionRowViewModel through the green-blink lifecycle, printing IsGreenPulsing at
    // each step. Deterministic (no UI, no timing race): cancellation is synchronous on the state edge,
    // and auto-settle uses a real sleep past the configured window.
    private static int BlinkTest()
    {
        double secs = Math.Clamp(ConfigService.Instance.Current.BlinkOnGreenSeconds, 0, 5);
        Console.WriteLine($"green-blink test (BlinkOnGreenSeconds={secs})");
        var row = new SessionRowViewModel("claude", "blink");

        SessionState S(string state, string evt) => new()
        {
            Tool = "claude", SessionId = "blink", State = state, Event = evt,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        row.Observe(S("yellow", "UserPromptSubmit"), DateTime.UtcNow);
        Console.WriteLine($"  working (yellow)           IsGreenPulsing={row.IsGreenPulsing,-5}  expect False");

        row.Observe(S("green", "Stop"), DateTime.UtcNow);
        Console.WriteLine($"  run finished -> green      IsGreenPulsing={row.IsGreenPulsing,-5}  expect {secs > 0}");

        // Cancellation: a new run begins before the window elapses -> blink must clear at once.
        row.Observe(S("yellow", "UserPromptSubmit"), DateTime.UtcNow);
        Console.WriteLine($"  green->yellow (new run)    IsGreenPulsing={row.IsGreenPulsing,-5}  expect False (cancelled mid-blink)");

        if (secs > 0)
        {
            row.Observe(S("green", "Stop"), DateTime.UtcNow);
            Console.WriteLine($"  re-enter green             IsGreenPulsing={row.IsGreenPulsing,-5}  expect True");
            System.Threading.Thread.Sleep((int)(secs * 1000) + 400);
            row.Observe(S("green", "Stop"), DateTime.UtcNow); // a later poll, still green
            Console.WriteLine($"  poll {secs}s later (steady)  IsGreenPulsing={row.IsGreenPulsing,-5}  expect False (auto-settled)");
        }
        Console.WriteLine("(set BlinkOnGreenSeconds=0 in settings to disable the blink entirely.)");
        return 0;
    }

    // Proves the manual reset (Feature A) headlessly, in two halves. FILE half: ForceGreen against a
    // temp sessions dir rewrites yellow AND red files to green/ManualReset (pid preserved) and leaves
    // green ones untouched. MODEL half: a real SessionRowViewModel + WorkTimer observing the forced
    // green freezes the timer at its current value with NO blink, and the next UserPromptSubmit
    // starts a fresh run from 0 — exactly the normal Stop behaviour. Exit 0 = ALL PASS.
    private static int ResetTest()
    {
        bool ok = true;
        void Check(string label, bool pass)
        {
            ok &= pass;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label}");
        }

        Console.WriteLine("manual-reset test (force all sessions green, quiet)");

        // ---- file half: rewrite semantics against a temp dir (never the real sessions dir) --------
        string dir = Path.Combine(Path.GetTempPath(), "agentsignal-reset-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Planted sessions must be LIVE under SessionLiveness (the reset deliberately skips ghosts):
            // pid = this test process, ts = just now (after this process started).
            int livePid = Environment.ProcessId;
            long plantedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            void Put(string id, string state, string evt, int pid, long ts) => File.WriteAllText(
                Path.Combine(dir, $"claude__{id}.json"),
                JsonSerializer.Serialize(new SessionState
                {
                    Tool = "claude", SessionId = id, State = state, Event = evt, Pid = pid, Ts = ts,
                }, AgentJsonContext.Default.SessionState));

            Put("stuckyellow", "yellow", "PreToolUse", livePid, plantedTs);  // the Esc-mid-tool gap this feature exists for
            Put("waitingred", "red", "PermissionRequest", livePid, plantedTs);
            Put("alreadygreen", "green", "Stop", livePid, plantedTs);
            // A ghost: a pid that isn't running, last event in 2023. Rewriting its ts=now could revive
            // it (a recycled pid would then look older than the "last event"), so reset leaves it alone.
            Put("ghostyellow", "yellow", "PreToolUse", FindNonRunningPid(), 1_700_000_000);

            var now = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            int n = SessionResetService.ForceGreen(dir, now);
            Check("rewrites exactly the two live non-green files", n == 2);

            SessionState Load(string id) => JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(dir, $"claude__{id}.json")), AgentJsonContext.Default.SessionState)!;

            SessionState y = Load("stuckyellow"), r = Load("waitingred"), g = Load("alreadygreen"), ghost = Load("ghostyellow");
            Check("stuck yellow → green, event=ManualReset, ts=now, pid preserved",
                y.State == "green" && y.Event == SessionResetService.EventName &&
                y.Ts == (long)(now - DateTime.UnixEpoch).TotalSeconds && y.Pid == livePid);
            Check("red also cleared to green", r.State == "green" && r.Event == SessionResetService.EventName);
            Check("already-green file left untouched (still event=Stop, original ts)",
                g.Event == "Stop" && g.Ts == plantedTs);
            Check("a GHOST is not touched (still yellow, ts not refreshed — can't be revived)",
                ghost.State == "yellow" && ghost.Ts == 1_700_000_000);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        // ---- model half: what the widget does when the forced green arrives ----------------------
        var row = new SessionRowViewModel("claude", "reset");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SessionState S(string state, string evt, int tsSec) => new()
        {
            Tool = "claude", SessionId = "reset", State = state, Event = evt,
            Ts = (long)(t0.AddSeconds(tsSec) - DateTime.UnixEpoch).TotalSeconds,
        };

        row.Observe(S("yellow", "UserPromptSubmit", 0), t0);
        row.Observe(S("yellow", "PreToolUse", 5), t0.AddSeconds(47)); // 47s into a run, tool in flight
        Check("working: yellow with the timer running (0:47)",
            row.State == AggregateState.Yellow && row.TimerText == "0:47");

        // Ctrl+Alt+R / the Settings button rewrote the file; the next poll observes the forced green.
        row.Observe(S("green", SessionResetService.EventName, 48), t0.AddSeconds(48));
        Check("reset observed: green with the timer frozen as the last run's time (0:48)",
            row.State == AggregateState.Green && row.TimerText == "0:48");
        Check("  ...no green-blink on a manual clear", !row.IsGreenPulsing);

        row.Observe(S("green", SessionResetService.EventName, 48), t0.AddSeconds(120));
        Check("later polls: still green, value still frozen (0:48)",
            row.State == AggregateState.Green && row.TimerText == "0:48");

        row.Observe(S("yellow", "UserPromptSubmit", 130), t0.AddSeconds(130));
        row.Observe(S("yellow", "PreToolUse", 133), t0.AddSeconds(139));
        Check("next UserPromptSubmit: fresh run from 0 (0:09)",
            row.State == AggregateState.Yellow && row.TimerText == "0:09");

        // The aggregate view of a reset: real state IS green (files rewritten), so the display agrees.
        var forced = S("green", SessionResetService.EventName, 48);
        Check("aggregate of a reset session is green (real, not display-only)",
            WidgetViewModel.Aggregate(new[] { forced }) == AggregateState.Green);

        Console.WriteLine(ok ? "ALL PASS" : "FAILURES above");
        return ok ? 0 : 1;
    }

    // Proves the auto-label and the per-session hide, headlessly. LABEL half: the pure formatting in
    // AgentSignal.Core (folder from cwd, tool display, truncation, the no-cwd fallback). HIDE half:
    // the REAL WidgetViewModel polling a TEMP sessions dir — hiding drops the pill but keeps the
    // session tracked (timer still running), the hide is per EXACT session (a new id in the same
    // folder shows normally), it survives later hook events and a relaunch, and the key is pruned
    // once the session ends. Exit 0 = ALL PASS.
    private static int LabelTest()
    {
        bool ok = true;
        void Check(string label, bool pass)
        {
            ok &= pass;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label}");
        }

        Console.WriteLine("auto-label + per-session hide test");
        Console.WriteLine(" LABEL");
        Check("folder from a Windows cwd", SessionLabel.FolderName(@"C:\Users\me\calorie-tracker") == "calorie-tracker");
        Check("folder from a POSIX cwd with a trailing slash", SessionLabel.FolderName("/home/me/my-app/") == "my-app");
        Check("full label = folder · Tool", SessionLabel.Full("claude", @"C:\src\calorie-tracker") == "calorie-tracker · Claude");
        Check("antigravity gets its own name", SessionLabel.Full("antigravity", "/w/my-app") == "my-app · Antigravity");
        Check("resting label = initials only",
            SessionLabel.Initials("claude", @"C:\src\AgentSignal") == "A · C" &&
            SessionLabel.Initials("antigravity", @"C:\src\calorie-tracker") == "C · A");
        Check("initials skip leading punctuation and accept digits",
            SessionLabel.Initials("claude", "/w/.config") == "C · C" &&
            SessionLabel.Initials("claude", "/w/2048-game") == "2 · C");
        Check("no cwd → the tool alone (full), its initial alone (resting)",
            SessionLabel.Full("claude", null) == "Claude" && SessionLabel.Initials("claude", null) == "C");

        Console.WriteLine(" HIDE (real WidgetViewModel over a temp sessions dir)");
        string dir = Path.Combine(Path.GetTempPath(), "agentsignal-label-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        List<string> savedHidden = ConfigService.Instance.Current.HiddenSessions;
        try
        {
            // pid = this process, so the liveness check keeps every planted session "live".
            void Put(string id, string state, string evt, string cwd) => File.WriteAllText(
                Path.Combine(dir, $"claude__{id}.json"),
                JsonSerializer.Serialize(new SessionState
                {
                    Tool = "claude", SessionId = id, State = state, Event = evt, Cwd = cwd,
                    Pid = Environment.ProcessId, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }, AgentJsonContext.Default.SessionState));

            const string TrackerDir = @"C:\src\calorie-tracker";
            const string AppDir = @"C:\src\my-app";
            Put("alpha", "yellow", "UserPromptSubmit", TrackerDir);
            Put("bravo", "red", "PermissionRequest", AppDir);

            ConfigService.Instance.Current.HiddenSessions = new List<string>(); // nothing hidden yet
            var vm = new WidgetViewModel(live: false, sessionsDir: dir);
            vm.PollOnce();
            Check("two sessions: both visible, red wins the aggregate",
                vm.SessionCount == 2 && vm.AllSessions.Count == 2 && vm.Sessions.Count == 2 &&
                vm.State == AggregateState.Red);
            Check("each row is labelled folder · tool (initials at rest)",
                Row(vm, "alpha").FullLabel == "calorie-tracker · Claude" &&
                Row(vm, "alpha").LabelText == "C · C" &&
                Row(vm, "bravo").FullLabel == "my-app · Claude" &&
                Row(vm, "bravo").LabelText == "M · C");
            Row(vm, "alpha").IsLabelExpanded = true;
            Check("hovering ONE row expands only that row's label",
                Row(vm, "alpha").LabelText == "calorie-tracker · Claude" &&
                Row(vm, "bravo").LabelText == "M · C");
            Row(vm, "alpha").IsLabelExpanded = false;

            // Right-click → Hide on the red one (the settings checkbox does exactly the same thing).
            Row(vm, "bravo").HideCommand.Execute(null);
            Check("hide takes effect immediately (1 pill shown, 2 still tracked)",
                vm.Sessions.Count == 1 && vm.Sessions[0].SessionId == "alpha" && vm.AllSessions.Count == 2);
            vm.PollOnce();
            Check("the hidden red session no longer drives the colour (aggregate = yellow)",
                vm.State == AggregateState.Yellow && vm.SessionCount == 1);
            Check("single pill shows the remaining session's label: initials at rest",
                !vm.IsExpanded && vm.IsLabelShown &&
                vm.InitialsLabel == "C · C" && vm.FullLabel == "calorie-tracker · Claude" &&
                vm.LabelText == "C · C");
            vm.IsLabelExpanded = true; // what the view sets while the pointer is over the pill
            Check("hover expands the label text in place to the full label",
                vm.LabelText == "calorie-tracker · Claude");
            vm.IsLabelExpanded = false;
            Check("mouse-out collapses it back to the initials", vm.LabelText == "C · C");

            // Tracking continues underneath: a hidden session keeps observing state AND running its timer.
            Put("bravo", "yellow", "UserPromptSubmit", AppDir);
            vm.PollOnce();
            string t0 = Row(vm, "bravo").TimerText;
            Check("a later hook event for the same session id stays hidden",
                Row(vm, "bravo").State == AggregateState.Yellow && !Row(vm, "bravo").IsShown && vm.Sessions.Count == 1);
            System.Threading.Thread.Sleep(1200);
            vm.PollOnce();
            Check($"hidden session keeps tracking — its timer advanced ({(t0.Length == 0 ? "-" : t0)} → {Row(vm, "bravo").TimerText})",
                Row(vm, "bravo").TimerText != t0 && Row(vm, "bravo").TimerText.Length > 0);

            // Hide is per EXACT session, not per folder.
            Put("charlie", "yellow", "UserPromptSubmit", AppDir); // same folder as the hidden bravo
            vm.PollOnce();
            Check("a NEW session in the SAME folder appears normally",
                Row(vm, "charlie").IsShown && vm.Sessions.Count == 2 && vm.IsExpanded);

            // Un-hiding (the Settings checkbox) brings the pill straight back.
            Row(vm, "bravo").IsShown = true;
            Check("re-showing restores the pill immediately", vm.Sessions.Count == 3);
            Row(vm, "bravo").IsShown = false;

            // A hide lasts only as long as its session.
            Check("hidden key is held while the session lives", vm.HiddenKeys.Contains("claude__bravo"));
            File.Delete(Path.Combine(dir, "claude__bravo.json")); // session ended
            vm.PollOnce();
            Check("session ended → the hidden key is pruned",
                !vm.HiddenKeys.Contains("claude__bravo") && vm.AllSessions.Count == 2);

            // Relaunch: a hidden key in config is honoured for a session that is still live.
            ConfigService.Instance.Current.HiddenSessions = new List<string> { "claude__alpha" };
            var relaunched = new WidgetViewModel(live: false, sessionsDir: dir);
            relaunched.PollOnce();
            Check("after a relaunch a hidden session is still hidden (but still tracked)",
                relaunched.AllSessions.Count == 2 && relaunched.Sessions.Count == 1 &&
                relaunched.Sessions[0].SessionId == "charlie" && !Row(relaunched, "alpha").IsShown);
        }
        finally
        {
            ConfigService.Instance.Current.HiddenSessions = savedHidden; // never disturb the real config
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine(ok ? "ALL PASS" : "FAILURES above");
        return ok ? 0 : 1;
    }

    private static SessionRowViewModel Row(WidgetViewModel vm, string sessionId)
    {
        foreach (SessionRowViewModel r in vm.AllSessions)
            if (r.SessionId == sessionId) return r;
        throw new InvalidOperationException($"no tracked row for session '{sessionId}'");
    }

    // Proves ghost sessions are pruned. Plants every ghost shape seen in the wild into a TEMP sessions
    // dir, runs the REAL SessionReader over it (exactly what the widget does at startup and on every
    // 250ms poll), then checks both what would render as a pill AND what is left on disk:
    //   live      — this process, fresh ts                      → shown, kept
    //   reused    — SAME live pid, but ts from months ago       → the pid now belongs to a process that
    //               started long after the session's last event, so it cannot be that session's agent
    //   nopid     — pid 0, months old                           → nothing to probe; no agent process
    //               that old is running, so it's dead
    //   deadpid   — a pid that isn't running                    → dead (was hidden before, but its
    //               file was never deleted, so the dir grew for months)
    //   unknown   — claude__unknown.json (no session_id ever)   → never a real session; always removed
    //   corrupt   — unparseable JSON, old mtime                 → garbage, removed
    // Exit 0 = ALL PASS.
    private static int LivenessTest()
    {
        bool ok = true;
        void Check(string label, bool pass)
        {
            ok &= pass;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label}");
        }

        Console.WriteLine("ghost-session pruning test (temp sessions dir, real SessionReader)");
        string dir = Path.Combine(Path.GetTempPath(), "agentsignal-liveness-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long monthsAgo = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            int self = Environment.ProcessId;
            int dead = FindNonRunningPid();

            void Put(string id, int pid, long ts) => File.WriteAllText(
                Path.Combine(dir, $"claude__{id}.json"),
                JsonSerializer.Serialize(new SessionState
                {
                    Tool = "claude", SessionId = id, State = "yellow", Event = "PreToolUse",
                    Cwd = @"C:\src\" + id, Pid = pid, Ts = ts,
                }, AgentJsonContext.Default.SessionState));

            Put("live", self, now);
            Put("reused", self, monthsAgo);   // same pid as "live" — a textbook PID reuse
            Put("nopid", 0, monthsAgo);
            Put("deadpid", dead, monthsAgo);
            Put("unknown", self, now);        // even with a live pid + fresh ts, "unknown" isn't a session
            string corrupt = Path.Combine(dir, "claude__corrupt.json");
            File.WriteAllText(corrupt, "{ \"tool\": \"claude\", \"sessionId\": ");
            File.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow.AddMinutes(-10));

            Console.WriteLine($"  planted: live(pid {self}), reused(pid {self}, ts 2026-07-17), nopid, deadpid(pid {dead}), unknown, corrupt");

            var shown = new SessionReader(dir).ReadLive().Select(s => s.SessionId).OrderBy(x => x).ToList();
            var onDisk = Directory.GetFiles(dir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f)["claude__".Length..]).OrderBy(x => x).ToList();
            Console.WriteLine($"  rendered as pills : [{string.Join(", ", shown)}]");
            Console.WriteLine($"  left on disk      : [{string.Join(", ", onDisk)}]");

            Check("the live session is shown", shown.Contains("live"));
            Check("a months-old session whose pid was REUSED is not shown", !shown.Contains("reused"));
            Check("a months-old session with NO pid is not shown", !shown.Contains("nopid"));
            Check("claude__unknown.json is not shown", !shown.Contains("unknown"));
            Check("exactly one pill renders", shown.Count == 1);
            Check("every ghost file is DELETED from disk (only 'live' remains)",
                onDisk.Count == 1 && onDisk[0] == "live");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine(ok ? "ALL PASS" : "FAILURES above");
        return ok ? 0 : 1;
    }

    private static int FindNonRunningPid()
    {
        for (int pid = 1_000_000; pid > 4; pid -= 7919)
            if (!ProcessHelper.IsAlive(pid))
                return pid;
        return 999_999;
    }

    // READ-ONLY: lists every session file with the liveness verdict the widget would reach, and why.
    // Uses SessionReader.Inspect(), which never deletes — a diagnostic must not modify the sessions
    // dir (ReadLive/SweepDead remove ghosts; the running widget does that on its own poll).
    private static int Dump()
    {
        var reader = new SessionReader();
        var files = reader.Inspect().ToList();
        var live = files.Where(f => f.State is not null && f.Verdict.Alive).Select(f => f.State!).ToList();
        int onDisk = Directory.Exists(AgentPaths.SessionsDir)
            ? Directory.GetFiles(AgentPaths.SessionsDir, "*.json").Length : 0;

        Console.WriteLine($"sessions dir : {AgentPaths.SessionsDir}  (read-only — nothing is deleted)");
        Console.WriteLine($"files on disk: {onDisk}   live: {live.Count}   ghosts: {files.Count - live.Count}");
        foreach ((string file, SessionState? s, SessionLiveness.Verdict v) in files)
        {
            string verdict = v.Alive ? "LIVE " : "GHOST";
            if (s is null)
            {
                Console.WriteLine($"  {verdict} {Path.GetFileName(file)}");
            }
            else
            {
                Console.WriteLine($"  {verdict} {s.Tool}__{s.SessionId}  state={s.State,-6} pid={s.Pid} event={s.Event} ts={DateTime.UnixEpoch.AddSeconds(s.Ts):yyyy-MM-dd HH:mm}Z");
                Console.WriteLine($"        label=[{SessionLabel.Full(s.Tool, s.Cwd)}]  cwd={s.Cwd ?? "(none)"}");
            }
            Console.WriteLine($"        why: {v.Reason}");
        }
        if (onDisk > files.Count)
            Console.WriteLine($"  ({onDisk - files.Count} unparseable file(s) younger than the corrupt grace period not listed — likely a write in flight)");
        Console.WriteLine($"aggregate    : {WidgetViewModel.Aggregate(live)}");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
