# Antigravity adapter (IDE only)

Adapter #2. Lights the pill for **Google Antigravity IDE** sessions, side by side with Claude Code
ones (state files keyed `antigravity__<conversation_id>`). Built strictly on what
[Phase 0 verified live](phase0/FINDINGS.md) against the IDE.

> **Scope: the Antigravity IDE only.** The Antigravity **CLI is future/unverified** — Phase 0 never
> exercised it, its hook scope (`~/.gemini/antigravity-cli/hooks.json`) and conversation storage may
> differ, and nothing here is installed for it. Do not assume this adapter works there.

## How it works

Antigravity's hook set has **no session-start/session-end/permission events**, and **nothing fires
at (or during, or at the resolution of) an approval prompt** — so this adapter uses two sources:

| Signal | Source | Mapping |
|--------|--------|---------|
| yellow | hook `PreInvocation` | turn started / model invoked again |
| green  | hook `Stop` | turn finished — **fires on user cancel too** (unlike Claude Code), so there is no stuck-yellow and the manual reset is never needed for Antigravity |
| red    | **db poller** (`AgentSignal.Writer poll antigravity`) | a step with `status=9` (awaiting approval) in the conversation SQLite |

The poller watches `~/.gemini/antigravity/conversations/<conversation_id>.db` (the filename IS the
conversation id / session key) every 250 ms: it stats the db + `-wal` first and re-reads only on
change, always copying `db`/`-wal`/`-shm` and opening the **copy** — never the live file. Decoded
`steps.status`: 2 tool executing · 3 completed · 6 cancelled · 7 denied · 8 model streaming ·
9 awaiting approval.

The poller owns only the approval lifecycle (hooks own the turn edges):

- session not red + a **new** status-9 step → **red**;
- session red + no 9 left → **yellow** if a step is running/streaming (2/8), else green — so
  **red ends at the approval instant** (the 9→2 flip), strictly better than Claude Code's red,
  which unavoidably spans the approved tool's execution; a **deny** (9→7) clears immediately;
- it never writes green from "all steps completed" (mid-turn gaps would flash a false green —
  green belongs to the real `Stop` hook), and a stale 9 orphaned by a cancel-at-the-prompt is
  remembered and ignored, never re-reddening an idle pill.

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

## Diagnostics

- `AgentSignal.Writer poll antigravity --test` — full approval-lifecycle self-test against a real
  WAL-mode SQLite db in temp dirs (prompt→red, approve→yellow at that instant, deny→clears,
  mid-turn gap stays yellow, stale cancel-9 ignored, dead IDE → session removed). Exit 0 = ALL PASS.
- `AgentSignal.Writer poll antigravity --once` — one diagnostic pass over the live session files,
  printing what the poller sees/decides per conversation.

## Timer semantics

Better than Claude's: because red ends at the approval instant, the widget's timer resumes right
when you click approve and ticks through the tool's execution live — no `durationMs` back-credit
needed (the writer never sends it for antigravity), and Claude's locked Decision #2 limitation does
not apply here.
