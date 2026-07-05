# Antigravity adapter (IDE only)

Adapter #2. Lights the pill for **Google Antigravity IDE** sessions, side by side with Claude Code
ones (state files keyed `antigravity__<conversation_id>`). Built strictly on what
[Phase 0 verified live](phase0/FINDINGS.md) against the IDE.

> **Scope: the Antigravity IDE only.** The Antigravity **CLI is future/unverified** — Phase 0 never
> exercised it, its hook scope (`~/.gemini/antigravity-cli/hooks.json`) and conversation storage may
> differ, and nothing here is installed for it. Do not assume this adapter works there.

## How it works

Antigravity's hook set has **no session-start/session-end/permission events**, and **nothing fires
at (or during, or at the resolution of) an approval prompt**. On top of that, the current engine
build (verified live 2026-07-06) **never actually spawns invocation-level hook commands** —
`PreInvocation`/`PostInvocation`/`Stop` are logged as `executing command` in language_server.log
but no process ever runs (two different programs left zero trace, while the same commands on tool
events demonstrably spawned; even a command's stderr only ever appears for tool hooks). **Only
`PreToolUse`/`PostToolUse` really execute.** So this adapter uses two sources:

| Signal | Source | Mapping |
|--------|--------|---------|
| yellow | hooks `PreToolUse` / `PostToolUse` + db poller | tool activity (hooks, instant) and model streaming (`status=8`, db — covers no-tool stretches) |
| green  | **db poller**: debounced quiescence | no running/streaming/pending step for **5s** (`DbQuiet`). Covers completion AND cancel (a cancel finalises its steps), so no stuck-yellow. Phase-0 timeline data: mid-turn all-final gaps max ~1.9s, so 5s clears the worst observed case ~2.7×; a longer pathological gap would flash green and self-correct. Green therefore shows ~5s late — accepted trade-off, the engine gives no turn-end event that actually runs |
| red    | **db poller** | a step with `status=9` (awaiting approval) in the conversation SQLite |

`PreInvocation`→yellow and `Stop`→green stay registered anyway: they cost nothing, and if a future
Antigravity build starts running invocation hooks they give an instant turn-start yellow and an
exact green that simply preempts the debounced one. A conversation that never uses a tool is only
picked up once some tool run registers it (the poller watches conversations that have session
files); from then on even its no-tool turns light via the db.

The poller watches `~/.gemini/antigravity/conversations/<conversation_id>.db` (the filename IS the
conversation id / session key) every 250 ms: it stats the db + `-wal` first and re-reads only on
change, always copying `db`/`-wal`/`-shm` and opening the **copy** — never the live file. Decoded
`steps.status`: 2 tool executing · 3 completed · 6 cancelled · 7 denied · 8 model streaming ·
9 awaiting approval.

The poller's rules:

- session not red + a **new** status-9 step → **red**;
- session red + no 9 left → **yellow** if a step is running/streaming (2/8), else green — so
  **red ends at the approval instant** (the 9→2 flip), strictly better than Claude Code's red,
  which unavoidably spans the approved tool's execution; a **deny** (9→7) clears immediately;
- session green + a running/streaming step → **yellow** (`DbActive` — no-tool turns, which fire
  no tool hooks);
- session yellow + fully quiescent for 5s → **green** (`DbQuiet` — the turn-end edge, since Stop
  never actually runs; the debounce keeps mid-turn model-spin-up gaps, observed ≤1.9s, yellow);
- a stale 9 orphaned by a cancel-at-the-prompt (left in a db not written since the session went
  green) is remembered and ignored, never re-reddening an idle pill.

It runs as a self-terminating daemon: every antigravity hook write spawns it if absent (a lock file
keeps it single-instance), and it exits ~60 s after the last antigravity session disappears.

**Liveness / cleanup:** Antigravity has no session-end hook. The writer captures the IDE's PID at
first write (process ancestry: `antigravity`, falling back to `language_server`), the widget drops
dead-PID sessions as always, and the poller also deletes session files whose IDE is gone (covering
the widget-not-running case and failed PID captures) — ended sessions leave no ghost pills.

**Session identity:** the conversation id, resolved from (in order) stdin `conversation_id` /
`session_id`, the `ANTIGRAVITY_CONVERSATION_ID` env var, and finally the most-recently-written
conversation db (the hook just fired, so the active conversation's db was just touched). With
`AGENTSIGNAL_DEBUG=1` the writer logs which source won (`~/.agentsignal/antigravity-writer.log`)
and the poller logs every transition (`~/.agentsignal/antigravity-poller.log`).

## Install

```powershell
AgentSignal.Writer.exe install antigravity
```

Deploys the writer to `~/.agentsignal/` and merges the hooks into **exactly one scope**, the global
`~/.gemini/config/hooks.json`. Do **not** also add a workspace `.agents/hooks.json` copy — the
engine loads BOTH scopes and both fire for every event (Phase 0 §1), which would double-fire the
writer. The hook command path is written **unquoted** (Antigravity passes quote characters through
literally — Phase 0 §5), so the install refuses a home directory whose path contains a space.

If the Phase 0 logging hooks are still installed, remove them first:
`adapters/antigravity/phase0/uninstall-phase0.ps1`.

**hooks.json must be UTF-8 *without* a BOM.** The engine's Go JSON parser rejects a BOM'd file
outright (`invalid character '﻿'` in language_server.log) and then loads **zero** hooks from
it — observed live 2026-07-06 after a manual edit with PowerShell's `Set-Content -Encoding utf8`,
which writes a BOM. The installer's own writes are BOM-less; if you ever hand-edit the file, save
it BOM-free (`[IO.File]::WriteAllText` with `UTF8Encoding($false)`).

## Diagnostics

- `AgentSignal.Writer poll antigravity --test` — full approval-lifecycle self-test against a real
  WAL-mode SQLite db in temp dirs (prompt→red, approve→yellow at that instant, deny→clears,
  mid-turn gap stays yellow, stale cancel-9 ignored, dead IDE → session removed). Exit 0 = ALL PASS.
- `AgentSignal.Writer poll antigravity --once` — one diagnostic pass over the live session files,
  printing what the poller sees/decides per conversation.

## Timer semantics

Better than Claude's on red: because red ends at the approval instant, the widget's timer resumes
right when you click approve and ticks through the tool's execution live — no `durationMs`
back-credit needed (the writer never sends it for antigravity), and Claude's locked Decision #2
limitation does not apply here. One trade the other way: green arrives via the 5s quiescence
debounce, so the frozen "last run" time reads up to ~5s long.
