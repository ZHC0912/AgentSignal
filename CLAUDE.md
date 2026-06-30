# AgentSignal — Project Brief

Build a small, always-on-top desktop widget that shows the live state of AI coding agents as a horizontal "traffic light" (green / yellow / red), with a work-time timer and a settings page. The first supported agent is **Claude Code**, but the app is designed to support others (Codex, Antigravity, …) through a pluggable adapter layer.

This file is the full spec. Read it fully before writing code, and follow the build order at the end. **Phase 0 was mandatory and came first — it is now done; do not redo it.** Sections 0–14 below are the *original brief* and are unchanged. For where the build actually stands, read **§A (Build status & working notes) first** — it is the source of truth for current state.

---

## A. Build status & working notes (READ FIRST)

> This section is the live state of the project. The numbered spec below (§0–§14) is the original brief and stays stable; this section tracks what's actually built and how to pick the work up on any machine. Last updated **2026-06-30**.

### A.1 Phase status

| Phase | What | Status |
|-------|------|--------|
| 0 | Instrument & verify Claude hook event order | ✅ **done & accepted** — mapping locked, written up in [`adapters/claude/phase0/FINDINGS.md`](adapters/claude/phase0/FINDINGS.md) |
| 1 | `AgentSignal.Core` + `AgentSignal.Writer` + Claude adapter installer | ✅ **done & accepted** — verified end-to-end (session files create/update/delete, PID capture) |
| 2 | Core widget (frameless, always-on-top, translucent pill; 3 dots; 250ms poll; draggable; position persisted) | ✅ **done & accepted** |
| 3 | Work-time timer (per-session accumulate/pause/freeze/reset; Decision #1 back-credit) | ✅ **done & accepted** — `--timer-test` proves the §8 1:10 case |
| 4 | Multiple concurrent sessions (expand/collapse, per-session rows, liveness removal, no flicker) | ✅ **done & accepted** — verified with two real `claude` sessions |
| 5 | Settings + alerts + tray + launch-on-startup | ✅ **done & accepted** — colours/alerts/size/opacity/lock/startup persist & apply live |
| 6 | Alerts & startup **polish** | ⏳ **NEXT** — alerts + startup are already implemented in Phase 5, so this is refinement only (e.g. per-session red granularity, confirming sound/notification on real red/green live, launch-on-startup UX). **Gated:** the owner reviews each phase before the next — don't start Phase 6 without a go-ahead. |
| 7 | Polish + Windows/Linux packaging | ⏳ after 6 — a self-contained App build would remove the `DOTNET_ROOT`-at-logon caveat (see A.4) |

### A.2 Locked decisions (carry forward — do not relitigate)

1. **Accurate timer.** The writer forwards `durationMs` (from Claude's `duration_ms`) on `PostToolUse`. The widget **back-credits it only on the `red → PostToolUse` transition**, so post-approval tool execution is counted but the human's approval wait is excluded. A plain `yellow → yellow` `PostToolUse` is already tick-counted and must **not** be double-credited. (This produces the §8 "1:10" result.)
2. **Red-during-long-approved-tool is an accepted hard limit.** No Claude hook fires at the instant a permission is approved (the next event is `PostToolUse`, which only arrives when the approved tool finishes). So `red` unavoidably spans the wait *and* that tool's execution. **Do not try to work around this** — it's a limit of the hook set, documented in Phase 0.

### A.3 As-built layout (matches §11, with the concrete service names)

- `src/AgentSignal.Core/` — `SessionState` (incl. `DurationMs`), `AgentPaths`, `ProcessHelper` (ancestry + liveness), `SettingsMerger`.
- `src/AgentSignal.Writer/` — console binary; reads hook JSON from stdin, writes/updates/deletes `~/.agentsignal/sessions/<tool>__<session_id>.json`, captures agent PID via ancestry on `SessionStart`, forwards `duration_ms`→`durationMs`. Has an `install <tool>` subcommand (embeds [`adapters/claude/hooks.template.json`](adapters/claude/hooks.template.json)).
- `src/AgentSignal.App/` — Avalonia widget.
  - `Services/`: `WorkTimer` (pure per-session stopwatch), `SessionReader`, `ConfigService` (singleton: `Instance`, `Current`, `Update(mutate)`→save+`Changed` event), `ThemeService` (writes colour brushes + glow `BoxShadows` into `Application.Resources` as **DynamicResource**), `Alerts` (`ISoundPlayer`=`SystemSoundPlayer` Console.Beep on bg thread + `INotifier`=`ToastNotifier`→`ToastWindow`; `AlertService.OnRed/OnGreen/Test`), `StartupManager` (`IStartupManager`; Windows = `reg.exe` HKCU `…\Run` value `AgentSignal`; Noop elsewhere).
  - `ViewModels/`: `DotsViewModel` (abstract base — state + timer text + active flags + static `FormatElapsed`), `SessionRowViewModel : DotsViewModel` (one per session, owns a `WorkTimer`), `WidgetViewModel : DotsViewModel` (reconciles `ObservableCollection<SessionRowViewModel>` **in place** each 250ms tick — no `Clear()` so no flicker — picks the driving session, fires alerts on aggregate edges), `SettingsViewModel`.
  - `Views/`: `WidgetWindow` (+ `LayoutTransformControl` `ScaleHost` for live scale/opacity), `PillView` (collapsed `DotsView` vs expanded `ItemsControl` of rows + gear `Button` at the panel edge), `DotsView`, `SettingsWindow`, `ToastWindow`.
- `adapters/claude/` — locked mapping + `hooks.template.json` + `phase0/`. See [`adapters/claude/README.md`](adapters/claude/README.md).

### A.4 Build & run (do this fresh on each machine)

**Per-machine state is NOT in the repo.** `~/.agentsignal/` (the deployed writer, `config.json`, `sessions/`) and the hook entries in `~/.claude/settings.json` are created by the installer and must be recreated on every PC. Your widget colours/position/size live in `~/.agentsignal/config.json` and won't transfer via git.

1. **Prereq:** .NET 10 SDK. Both projects target `net10.0`; the solution is the new XML format `AgentSignal.slnx` (needs a recent `dotnet`).
2. **Build:** `dotnet build AgentSignal.slnx`
3. **Run the widget:** `dotnet run --project src/AgentSignal.App` (no args = the live always-on-top widget). The App is **framework-dependent** (no RID/`SelfContained` in its csproj), so the machine needs the .NET 10 **runtime** to run it.
4. **Install the Claude integration:** build/publish the writer, then run `AgentSignal.Writer install claude`. This copies the writer to `~/.agentsignal/` and **merges** the hook mapping into the **user-level `~/.claude/settings.json`** (append-only, idempotent), substituting the writer's absolute path.
5. **Writer packaging:** the writer is `IsAotCompatible` and the *goal* is NativeAOT, but that needs a platform C/C++ toolchain (MSVC `link.exe` on Windows). Where that's unavailable, publish **self-contained single-file** instead — both give a dependency-free native exe for the hook path, e.g. `dotnet publish src/AgentSignal.Writer -c Release -r <rid> --self-contained -p:PublishSingleFile=true`.

> **Original dev machine quirk (Windows):** .NET 10 was installed at `~/.dotnet` and **not on PATH**, and since the App is framework-dependent its apphost couldn't find the runtime — so it was launched via `dotnet src/AgentSignal.App/bin/.../AgentSignal.App.dll`, or by setting `DOTNET_ROOT=%USERPROFILE%\.dotnet` before the apphost. If `dotnet` is on PATH on your machine, `dotnet run` just works and you can ignore this.

### A.5 Diagnostics (`AgentSignal.App` flags — no display needed)

- `--dump` — print live sessions + aggregate colour and exit.
- `--timer-test` — replay the §8 permission scenario through the real `WorkTimer` (proves **1:10**).
- `--watch [seconds]` — run the real reconcile loop over live session files, printing rows as sessions come/go.
- `--config` — print the on-disk `config.json` from a fresh process (use after editing to confirm persistence) + whether launch-on-startup is registered.
- `--settings-demo <dir>` — headless: render the real `WidgetWindow` before/after live settings edits + a toast, and toggle the startup registry key.
- `--screenshot <path>` — render a static preview PNG.

### A.6 Gotchas (the ones that bit us)

- **Hooks go in user-level `~/.claude/settings.json` only — never `settings.local.json`**, which some harnesses rewrite and would silently drop the hooks (Phase 0 finding).
- **`AlertOnRed` defaults `true`**, so the running widget **beeps + toasts whenever any session goes red** — including the dev session hitting a permission prompt. Toggle it off in Settings if it's noisy while you work.
- **Live-screenshotting the running transparent/topmost widget is unreliable here** — use the headless `--screenshot` / `--settings-demo` renders instead.
- **Interactive `claude` sitting at a prompt may not fire `SessionStart`** (no session file). Use `claude -p "<prompt>"` print-mode to exercise the full hook lifecycle when testing.
- **`Grid.RowSpacing` isn't in Avalonia 11.2** — use stacked `StackPanel`s with `Spacing` instead.
- **Git workflow for this repo:** solo project — "push to git" means commit straight to `main` and push (no branch/PR).

---

## 0. Naming & shape

**AgentSignal** is a universal agent-status indicator. It is *not* Claude-specific at its core: the widget only ever reads a small set of per-session state files (the "state contract"). Each supported agent has an **adapter** that writes those files. Claude Code is **adapter #1**; Codex and Antigravity are planned. Build the core so adding an agent later means writing one adapter, never touching the widget.

---

## 1. What it does (one paragraph)

A floating pill with three horizontal dots sits at the top-right of the screen, always on top of every other window, and reflects what the agent is doing: **green** = idle/open, **yellow** = working, **red** = waiting for the user to answer a permission prompt, **off** = no agent running. While a request is being worked on, a timer under the dots counts the *active work time*. The widget is draggable, remembers its position, and clicking it reveals a gear that opens a settings page. If several agent sessions run at once, the pill shows the most urgent state and expands to one row per session.

---

## 2. Tech stack & platform

- **Language:** C# / .NET (latest LTS; confirm current version).
- **UI:** Avalonia 11 (cross-platform; **not** WPF, which is Windows-only). MVVM via CommunityToolkit.Mvvm.
- **Writer:** a tiny .NET console app, published **self-contained / NativeAOT** so it's a fast, dependency-free native binary that hooks can invoke.
- **JSON:** System.Text.Json for config and state files.
- **Process liveness:** System.Diagnostics + a small cross-platform process-ancestry helper (see §6 / §13).
- **Targets:** Windows, macOS, and Linux.
  - Build/test on Windows + Ubuntu directly.
  - **macOS caveat:** producing and signing a real macOS build generally requires a Mac (notarization). Ship Windows + Linux first; add macOS when a Mac is available. Keep all code cross-platform so that's just a build step later.
  - A few features are inherently per-OS — launch-on-startup, native notifications, sound, and transparent/always-on-top quirks (Linux compositors are fussiest). Isolate these behind interfaces with one implementation per platform.

---

## 3. Architecture

```
AGENT (e.g. Claude Code)
   │  fires lifecycle hooks
   ▼
ADAPTER hooks  ──invoke──▶  AgentSignal.Writer (native binary)
                                   │ writes/updates
                                   ▼
        ~/.agentsignal/sessions/<tool>__<session_id>.json     ← the STATE CONTRACT
                                   │
                     widget polls dir every ~250ms
                                   ▼
                 AgentSignal.App (Avalonia): lights + timer
```

Three pieces:
1. **Adapters** — per-agent hook configuration that calls the writer on lifecycle events.
2. **`AgentSignal.Writer`** — a tool-agnostic console binary that records the current state of one session as a JSON file.
3. **`AgentSignal.App`** — the Avalonia widget that reads those files, runs the timers, checks liveness, and renders.

The hooks only ever report *the current state word*. **All timer logic lives in the widget**, derived from state transitions — never compute elapsed time in the writer or hooks.

### State contract (the only thing the widget knows about)

One file per live session at `~/.agentsignal/sessions/<tool>__<session_id>.json`:

```json
{
  "tool": "claude",
  "sessionId": "abc123",
  "state": "yellow",          // green | yellow | red  (no "off" — off = file deleted)
  "event": "PreToolUse",
  "toolName": "Bash",         // optional, for a tooltip
  "pid": 48213,               // agent process id, captured at session start
  "ts": 1719750000            // epoch seconds of this update
}
```

---

## 4. State model

| State    | Meaning                                  | Timer        |
|----------|------------------------------------------|--------------|
| `green`  | Agent open / finished a turn (idle)      | frozen, shows last run's time |
| `yellow` | Working (prompt submitted, running tools)| running      |
| `red`    | Waiting on a permission prompt           | paused       |
| `off`    | Session ended / no agent running         | hidden (file deleted) |

---

## 5. Adapter #1 — Claude Code

### 5.1 Event → state mapping (starting point — confirm in Phase 0)

- `SessionStart` → `green` (register the session)
- `UserPromptSubmit` → `yellow` (a new run begins → reset timer)
- `PreToolUse` → `yellow`
- `PostToolUse` → `yellow` (likely the "work resumed after approval" signal — see Phase 0)
- `PermissionRequest` → `red`
- `Stop` → `green`
- `SessionEnd` → `off` (delete the session file)

### 5.2 PHASE 0 — verify the real event order FIRST (do not skip)

The exact firing order around a permission prompt determines whether the timer pauses/resumes correctly. Before finalizing the mapping:

1. Temporarily configure a hook on **every** event that appends the event name + full stdin JSON payload to a log file.
2. Run Claude Code on a task that triggers a real permission prompt (e.g. an un-approved shell command).
3. Read the log and confirm the actual sequence — especially **what fires at the moment the user approves**, so `red` returns to `yellow` at the right instant (a long tool that runs *after* approval must show yellow, not red).
4. If `PermissionRequest` isn't the cleanest red trigger, also check the `Notification` event with matcher `permission_prompt`. Lock the mapping based on what you observe, and write down what you found.

### 5.3 settings.json hooks block

Install at the **user level** (`~/.claude/settings.json`) so it applies to all projects. The installer (§9) deploys the writer binary, substitutes its absolute path, and **merges** these hooks without clobbering existing ones. Pass the tool name + state as args; the writer reads `session_id` from stdin.

```json
{
  "hooks": {
    "SessionStart":      [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude green" }] }],
    "UserPromptSubmit":  [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude yellow" }] }],
    "PreToolUse":        [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude yellow" }] }],
    "PostToolUse":       [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude yellow" }] }],
    "PermissionRequest": [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude red" }] }],
    "Stop":              [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude green" }] }],
    "SessionEnd":        [{ "hooks": [{ "type": "command", "command": "<WRITER_PATH> claude off" }] }]
  }
}
```

`<WRITER_PATH>` is e.g. `~/.agentsignal/AgentSignal.Writer` (or `...Writer.exe` on Windows). Add `"async": true` to the hooks if the installed Claude Code version supports it, so AgentSignal can never slow the agent down.

### 5.4 Future adapters (build the seams now, implement later)

- **Codex (OpenAI)** — nearly identical: it exposes the same hook vocabulary (SessionStart, UserPromptSubmit, PreToolUse, PermissionRequest, PostToolUse, Stop, SubagentStart/Stop) with command hooks that receive JSON on stdin. The adapter is almost the same block in Codex's config location, invoking the same writer with `codex` as the tool arg. Treat this as the easy second adapter.
- **Antigravity (Google)** — feasible but more work: it has hooks (`.agents/hooks.json`) and a Python SDK, but a different/consolidated event set, and the permission/approval signal (the red light) is less cleanly exposed — may need extra mapping or a fallback. It's also new and changing fast. Defer; just don't design anything that blocks it.

Keep all adapter-specific files/scripts under `adapters/<tool>/`.

---

## 6. AgentSignal.Writer (the console binary)

- Invoked as `AgentSignal.Writer <tool> <state>` where `<state>` ∈ {green, yellow, red, off}.
- Reads the hook JSON from **stdin**; extracts `session_id` and (when present) `tool_name` / `hook_event_name`.
- Ensures `~/.agentsignal/sessions/` exists.
- File key is `<tool>__<session_id>.json` (so two agents never collide).
- For `off`: delete that file.
- Otherwise: read-modify-write the file per the state contract (§3).
- **On `SessionStart`**, capture the agent's process PID by walking up this process's ancestry until it finds the agent process (e.g. name/cmdline contains `claude`); store it and preserve it on later writes.
- Must be tiny and fast (cheap file write) so it never blocks the agent. NativeAOT keeps startup latency low; if it ever becomes a problem for the high-frequency events, a minimal script writer is an acceptable fallback.

---

## 7. AgentSignal.App — the widget (UI / UX)

- Avalonia `Window`: `SystemDecorations="None"`, transparent background (`TransparencyLevelHint`), `Topmost="True"`, `ShowInTaskbar="False"`. A rounded "pill" containing three horizontal dots.
- Starts top-right; **draggable** anywhere; remembers last position across launches.
- Must receive clicks (not click-through), so **distinguish click from drag** manually via a movement threshold (handle PointerPressed/Moved/Released; moved more than a few px = drag, else = click). Don't rely on a blind `BeginMoveDrag`.
- **Click** the pill → a **gear icon** fades in to the *side* (the area below is reserved for the timer). **Click the gear** → open the settings window.
- The active dot is visually obvious (brighter/glow). A gentle pulse on yellow is a nice optional touch.
- Optional: a `TrayIcon` with Quit / Open settings — handy but not required for v1.
- Poll the sessions directory with a ~250 ms `DispatcherTimer` (simple and robust). `FileSystemWatcher` is an optional optimization; the timer must keep running anyway to tick the clock.

---

## 8. Timer behaviour

Measures **active work time** for the current request, computed in the widget from state transitions (accumulate elapsed per tick while a session is `yellow`):

- Goes `yellow` (new run via `UserPromptSubmit`) → reset to 0 and start counting.
- `red` → **pause** (hold the value).
- back to `yellow` → **resume** from where it paused.
- `green` → **stop and freeze**, and keep the final value visible as "time the last request took."
- Next run (`yellow` again) → reset to 0 and start over.

Example: yellow 40s → red while the user takes 2 min to approve → yellow 30s should read **1:10**, not 3:10.

Display directly under the dots. Auto-format `m:ss`, switching to `h:mm:ss` past an hour.

---

## 9. Multiple concurrent sessions

- Maintain a **per-session model** keyed by `(tool, session_id)`, each with its own state + timer.
- Default display: **one aggregate pill** whose colour is the most urgent live state — **red > yellow > green** (any red wins, else any yellow, else green). Its timer shows the "driving" session: the red one needing an answer, otherwise the most recently active.
- Auto-detect: a new session file = a new agent session (add it); file removed or its process dead = gone (drop it). No manual config.
- **Click to expand** the pill into one row per live session (dots + timer each); collapse back to the aggregate.
- With a single session running (the common case) it behaves exactly like a simple one-light widget.

---

## 10. Settings page (starter set)

Persist to `~/.agentsignal/config.json` (System.Text.Json) with sensible defaults; apply changes live. Include:

- **Per-state colours** — customise green / yellow / red.
- **Alert on red** — play a sound and show a desktop notification ("Agent needs permission") when any session goes red.
- **Alert on green** — optional toggle: sound/notification when a run finishes.
- **Size & opacity** — scale the whole widget; set translucency.
- **Lock position** — when on, ignore drags so accidental clicks don't move it.
- **Launch on startup** — start with the OS. Implement Windows first (Run registry key or Startup-folder shortcut); provide Linux (`~/.config/autostart/*.desktop`) and macOS (LaunchAgent plist) behind the same interface.

Everything else (history log, per-tool tooltip detail, pulse styles) is out of scope for v1 — but keep the data model able to grow into a per-session history later.

---

## 11. Files & directories

```
~/.agentsignal/
├── AgentSignal.Writer(.exe)     # native binary invoked by hooks
├── config.json                  # user settings
└── sessions/
    └── <tool>__<session_id>.json

<repo>/
├── AgentSignal.sln
├── src/
│   ├── AgentSignal.Core/        # state contract, paths, JSON models, liveness helper
│   ├── AgentSignal.Writer/      # console app (NativeAOT, self-contained)
│   └── AgentSignal.App/         # Avalonia widget
│       ├── Views/   (WidgetWindow, SettingsWindow)
│       ├── ViewModels/
│       ├── Services/ (SessionReader, TimerService, Notifier, SoundPlayer, StartupManager, ConfigService)
│       └── Models/  (SessionState, AppConfig)
├── adapters/
│   ├── claude/                  # install logic + settings snippet
│   └── README.md                # codex / antigravity notes
└── install (a subcommand or small installer that deploys the writer + merges adapter hooks)
```

Resolve home via `Environment.GetFolderPath` / `Environment.SpecialFolder` or a single cross-platform path helper in `AgentSignal.Core`.

---

## 12. Suggested build order

0. **Phase 0 — Instrument & verify event order** (§5.2). Resolve the mapping before anything else.
1. **Core + Writer** — `AgentSignal.Core` (state contract, paths, models) and `AgentSignal.Writer`; the Claude adapter installer that merges hooks. Verify session files appear/update/delete correctly as you actually use Claude Code.
2. **Core widget** — frameless always-on-top translucent pill, three dots, reads sessions dir, shows aggregate colour, draggable, position persisted.
3. **Timer** — per-session accumulator with pause/resume/freeze/reset; display under dots.
4. **Multiple sessions** — expand/collapse, per-session rows, liveness-based removal.
5. **Settings** — click→gear→window; the starter-set options; persistence; live apply.
6. **Alerts & startup** — sound + desktop notification on red (+ optional green); launch-on-startup.
7. **Polish & package** — click-vs-drag threshold, opacity/size, zero-sessions → off; produce Windows + Linux builds.

Build and test each phase against a real Claude Code session before moving on.

---

## 13. Known tricky bits to validate

- **Event ordering / resume-after-permission** — resolved in Phase 0; get it right or the timer pauses/resumes wrongly.
- **Cross-platform PID capture & liveness** — getting parent-process info differs per OS (Windows snapshot/WMI vs `/proc` on Linux vs macOS APIs). Centralise it in one helper and verify on Windows specifically. Liveness rule: **`SessionEnd` OR process-dead → off.** Do **not** use an inactivity timeout — a healthy idle session legitimately sits on `green` forever and must stay green until its process exits.
- **Per-OS shims** — sound (`System.Media.SoundPlayer` is Windows-only; use a cross-platform approach), desktop notifications, launch-on-startup, and transparency quirks. One interface, three implementations.
- **NativeAOT writer latency** — confirm startup is fast enough for high-frequency events (PreToolUse/PostToolUse); prefer `async` hooks.
- **Hook command portability** — correct writer path/extension per OS; the settings.json merge must not clobber existing hooks.

---

## 14. Definition of done

- Opening a Claude Code terminal lights green; submitting a prompt → yellow with a running timer; a permission prompt → red with the timer paused; answering it → yellow, timer resumes; completion → green with the last run's time frozen.
- Closing/killing the terminal turns its light off (clean exit and hard kill both handled).
- Two sessions at once are both tracked; the pill shows the most urgent state and expands to per-session rows.
- Settings (colours, red/green alerts, size/opacity, lock position, launch on startup) persist and apply live.
- The widget stays on top, is draggable, remembers position, and reveals the gear → settings on click.
- The agent integration lives entirely in an adapter; adding Codex later requires no widget changes.
