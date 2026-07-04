# AgentSignal — architecture & behaviour

AgentSignal is three small pieces connected by one file-based contract. The widget knows nothing
about any particular AI agent; each agent gets an **adapter** that translates its lifecycle events
into the shared contract. Claude Code is adapter #1 (see [claude-hook-events.md](claude-hook-events.md)).

```
AGENT (e.g. Claude Code)
   │  fires lifecycle hooks
   ▼
adapter hooks ──invoke──▶ AgentSignal.Writer        (tiny self-contained console exe)
                               │ writes / updates / deletes
                               ▼
        ~/.agentsignal/sessions/<tool>__<session_id>.json     ← the STATE CONTRACT
                               │
                 widget polls the directory every 250 ms
                               ▼
                AgentSignal.App (Avalonia): dots + timers + alerts
```

- **`AgentSignal.Writer`** — invoked by the agent's hooks as `AgentSignal.Writer <tool> <state>`.
  Reads the hook's JSON from stdin, extracts the session id, and records the session's current
  state as a JSON file. On session start it captures the agent's process ID by walking up its own
  process ancestry. It also self-installs adapters (`AgentSignal.Writer install claude`).
- **`AgentSignal.App`** — the always-on-top widget. Reads the session files, derives all timing
  from state *transitions*, checks process liveness, renders, and fires alerts.
- **`AgentSignal.Core`** — the shared contract: state model, paths, JSON serialization,
  cross-platform process helpers.

## The state contract

One file per live session at `~/.agentsignal/sessions/<tool>__<session_id>.json`:

```json
{
  "tool": "claude",
  "sessionId": "abc123",
  "state": "yellow",          // green | yellow | red   (off = file deleted)
  "event": "PreToolUse",
  "toolName": "Bash",
  "durationMs": 6942,         // PostToolUse only: the tool's real execution time
  "pid": 48213,               // agent process id, captured at session start
  "ts": 1719750000
}
```

Hooks only ever report a state word. **All timer logic lives in the widget**, derived from state
transitions — nothing computes elapsed time in the writer or the hooks.

## State model

| State | Meaning | Timer |
|---|---|---|
| 🟢 `green` | agent idle / run finished | frozen, shows the last run's time |
| 🟡 `yellow` | working (prompt submitted, tools running) | running |
| 🔴 `red` | waiting on a permission prompt | paused |
| ⚫ off | session ended / process dead (file deleted) | hidden |

**The light only changes on real events.** There is deliberately no inactivity timeout or
display-side guessing: a healthy idle session sits green forever, and a working session stays
yellow through arbitrarily long thinking stretches (thinking fires no hooks, so any timeout would
eventually show a false "done"). The one consequence — a run aborted with <kbd>Esc</kbd> fires no
event and leaves a stuck yellow — is handled by the **manual reset** below.

## The work timer

The timer measures **active work time** for the current request:

- `yellow` (new prompt) → reset to 0 and run.
- `red` → pause (the human's approval wait is not work).
- back to `yellow` → resume.
- `green` → stop and freeze; the value stays visible as "what the last run took".

One subtlety: when a permission is approved, the agent emits nothing until the approved tool
*finishes* (see the [hook-event reference](claude-hook-events.md)), so the approved tool executes
inside the red window. The writer forwards the tool's real `duration_ms`, and the widget
**back-credits it on the `red → yellow` transition** — so post-approval execution is counted, the
human wait is excluded. Example: 40 s of work, a 2-minute wait on you, then a 30 s approved tool
reads **1:10**, not 3:10.

## Multiple sessions & liveness

Each `(tool, session_id)` gets its own model with its own state and timer. The collapsed pill shows
the most urgent aggregate state (**red > yellow > green**) and the "driving" session's timer (a red
session needing an answer wins, else the most recently active). At 2+ live sessions the widget
auto-expands to one row per session — each with its own dots and its own independent timer — and
collapses back at one.

A session is live until its file is deleted (`SessionEnd`) **or its process dies** (the PID captured
at session start is probed). Never on a timeout.

## Manual reset

<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>R</kbd> (global, registered with the OS) or the **Reset** button
in Settings forces every current session to green, as if its turn completed: the session files are
rewritten through the normal state contract (`state: "green"`, `event: "ManualReset"`), each timer
freezes at its current value, and the next prompt starts a fresh run. The reset green is quiet — no
alert, no blink. It exists for the one case no hook covers: an <kbd>Esc</kbd>-aborted run.

The hotkey is disabled while the Settings window is open (the in-window button covers that case);
the widget itself is inert (no click/drag) while Settings is open.

## Settings

Persisted to `~/.agentsignal/config.json`, applied live: per-state colours · red/green alerts
(sound + toast) · green-blink duration (0–5 s) · orientation (horizontal/vertical) · size (0.6–3×)
· opacity · lock position · launch on startup. The widget is draggable, remembers its position,
flips its attached pills toward the screen near an edge, and has a collapsible timer.

## Headless diagnostics

The app self-verifies without a display (`AgentSignal.App <flag>`):

| Flag | Proves |
|---|---|
| `--timer-test` | the permission-scenario timer math (the 1:10 case above) |
| `--reset-test` | manual reset end-to-end (file rewrite + frozen timers + quiet green) |
| `--anchor-test` | the dots pill never moves when timer/chevron/gear toggle, in every layout |
| `--blink-test` | the green-blink lifecycle (start / cancel / auto-settle) |
| `--dump`, `--watch` | live session files + aggregate state, one-shot or continuous |
| `--screenshot <png>`, `--layout <png>` | rendered previews of states and layouts |
| `--config`, `--startup <on\|off\|status>` | config persistence and the startup registration |
