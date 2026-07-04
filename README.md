<p align="center">
  <img src="docs/icon.png" width="128" alt="AgentSignal icon" />
</p>

<h1 align="center">AgentSignal</h1>

<p align="center">
  An always-on-top traffic light for your AI coding agents.<br/>
  <b>Green</b> = idle &nbsp;·&nbsp; <b>Yellow</b> = working &nbsp;·&nbsp; <b>Red</b> = waiting on you &nbsp;·&nbsp; plus a live work-time timer.
</p>

---

AI coding agents like **Claude Code** run long, unattended stretches — then silently block on a permission prompt while you're reading something in another window. AgentSignal is a tiny desktop widget that floats above everything and tells you at a glance what your agent is doing, so you never leave it stuck waiting (or interrupt it mid-run). When an agent needs permission, the light goes red and the widget can beep and toast; when a run finishes, it goes green and the timer freezes at the run's true working time.

<p align="center">
  <img src="docs/widget-states.png" width="640" alt="Widget states: off, green idle, yellow working, red waiting, multi-session expanded" />
</p>

## Download (Windows)

**[⬇ Download the latest release](https://github.com/ZHC0912/AgentSignal/releases/latest/download/AgentSignal-win-x64.zip)** — `AgentSignal-win-x64.zip`

The exes are fully self-contained (bundled .NET runtime): no .NET install, nothing on PATH. Unzip, then from the extracted folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1            # install + launch
powershell -ExecutionPolicy Bypass -File install.ps1 -Startup   # …and start on logon
```

The installer copies the app to a stable path (`%LOCALAPPDATA%\AgentSignal`), deploys the hook writer to `~/.agentsignal`, and merges the Claude Code hooks into your user-level `~/.claude/settings.json` (append-only and idempotent — your existing hooks are untouched). Open a Claude Code session and the dots light up.

Prefer just trying the widget? Grab the bare [`AgentSignal.exe`](https://github.com/ZHC0912/AgentSignal/releases/latest/download/AgentSignal.exe) and run it — you'll still need `install.ps1` (or `AgentSignal.Writer.exe install claude`) once so the agent can report its state.

## What it does

- **Live state, per session** — green / yellow / red dots driven by the agent's real lifecycle events, never by guesswork or timeouts. Multiple concurrent sessions each get their own row (auto-expands at 2+), with the aggregate pill showing the most urgent state (red > yellow > green).
- **A truthful work timer** — counts *active* work only: it runs while the agent works, pauses while a permission prompt waits on you, back-credits approved tool time, and freezes at the run's total when the agent finishes. Waiting two minutes to click "allow" doesn't inflate a 1:10 run into 3:10.
- **Alerts** — optional sound + toast when an agent goes red (needs you) or green (finished).
- **Stays out of the way** — frameless, translucent, draggable, remembers its position, horizontal or vertical, scalable 0.6–3×, opacity control, lock-position, collapsible timer, launch-on-startup.
- **Manual reset** — <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>R</kbd> (or a Settings button) forces all sessions green, for the one case no hook covers: aborting a run with <kbd>Esc</kbd> fires no event, so the light can't clear itself.

<p align="center">
  <img src="docs/layout-matrix.png" width="640" alt="Orientation, smart pill direction and collapsible timer layouts" />
</p>

## How it works

AgentSignal is **adapter-based** — the widget knows nothing about any particular agent. Each supported agent gets a small adapter that translates its lifecycle events into a shared state contract; **Claude Code is adapter #1**, and the same seam is designed to take Codex and others without touching the widget.

```
AGENT (Claude Code)
   │  lifecycle hooks (SessionStart, UserPromptSubmit, PreToolUse, …)
   ▼
AgentSignal.Writer  ──  a tiny self-contained exe the hooks invoke
   │  writes one JSON file per live session
   ▼
~/.agentsignal/sessions/<tool>__<session_id>.json      ← the state contract
   │  polled every 250 ms
   ▼
AgentSignal.App  ──  the Avalonia widget: dots, timers, alerts
```

The hooks only ever report a state word (`green` / `yellow` / `red` / `off`); all timing logic lives in the widget, derived from state transitions. Session liveness is real: a session disappears when it ends *or* its process dies (PID captured at session start) — never on an inactivity timeout, so a healthy idle agent stays green forever.

Full documentation: **[architecture & behaviour](docs/architecture.md)** · **[Claude Code hook events (verified)](docs/claude-hook-events.md)**

| State | Meaning | Timer |
|---|---|---|
| 🟢 green | idle / run finished | frozen at the last run's time |
| 🟡 yellow | working | running |
| 🔴 red | waiting on a permission prompt | paused |
| ⚫ off | no agent running | hidden |

## Building from source

Requires the **.NET 10 SDK**. The widget and writer are plain .NET projects (Avalonia UI, MVVM):

```powershell
git clone https://github.com/ZHC0912/AgentSignal.git
cd AgentSignal
dotnet build AgentSignal.slnx
dotnet run --project src/AgentSignal.App          # run the widget from the dev tree

# self-contained single-file publish (what the release ships):
dotnet publish src/AgentSignal.App    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish src/AgentSignal.Writer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true
```

Layout: `src/AgentSignal.Core` (state contract, paths, process liveness) · `src/AgentSignal.Writer` (hook-invoked writer + `install claude`) · `src/AgentSignal.App` (Avalonia widget) · `adapters/claude` (hook mapping + the Phase 0 event-order findings).

The app ships with headless diagnostics — `--timer-test`, `--anchor-test`, `--reset-test`, `--blink-test`, `--screenshot <png>`, `--dump`, `--watch` — that prove the timer math, layout stability and reset behaviour without a display. The design and its verified underpinnings are documented in [`docs/`](docs/README.md).

## Platform notes

Windows is fully supported and shipped. The codebase is cross-platform (Avalonia; per-OS features like startup, sound and the global hotkey sit behind interfaces) — Linux packaging is next, macOS when signing hardware is available.

## Roadmap

- 📱 **Phone notifications** (via [ntfy](https://ntfy.sh)) — get the red "agent needs you" alert on your phone when you step away.
- 🤖 **Codex adapter** — same hook vocabulary, same writer; the seam is already in place.
- 🐧 **Linux packaging** (and startup/sound/hotkey implementations behind the existing interfaces).

## License

[MIT](LICENSE)
