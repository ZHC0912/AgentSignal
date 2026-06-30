# Phase 0 — Claude Code hook event order (verified)

**Date:** 2026-06-30
**Claude Code version:** 2.1.196
**Platform:** Windows 10 (hooks invoked via `powershell.exe`)
**Method:** A "log every event" hook on all lifecycle events appended `timestamp + event + full stdin JSON` to
`~/.agentsignal/phase0-events.log`. Captured (a) a real PowerShell command that triggered a genuine permission
prompt + human approval, with a 6 s sleep *after* approval to expose the post-approval gap, and (b) a throwaway
`claude -p` child session for the session-boundary events. Raw evidence: `phase0-events.log`, logger:
`log-event.ps1`.

---

## 1. Verified event order

### Normal tool call (no permission needed)
```
PreToolUse  →  (tool executes)  →  PostToolUse (has duration_ms)  →  PostToolBatch
```
`PostToolUse` fires **after** the tool finishes and carries `tool_response` + `duration_ms`.
`PostToolBatch` fires once after a whole parallel batch of tool calls resolves.

### Tool call that needs permission (the critical case)
Captured timeline (session `3e4a3b34…`, one PowerShell command, ~6 s sleep after approval):

| # | time         | event             | notes |
|---|--------------|-------------------|-------|
| 1 | 03:30:52.521 | `PreToolUse`      | fires **before** the permission gate |
| 2 | 03:30:53.894 | `PermissionRequest` | +1.4 s — dialog appears; payload has `permission_suggestions` |
| 3 | 03:31:00.213 | `Notification`    | `notification_type:"permission_prompt"`, msg "Claude needs your permission" — a **delayed nudge**, not an approval signal |
| — | ~03:31:25    | *(user approves; tool runs 6 s)* | **NO HOOK FIRES at approval or during execution** |
| 4 | 03:31:32.087 | `PostToolUse`     | only after the tool finishes (`duration_ms: 6942`) |
| 5 | 03:31:32.910 | `PostToolBatch`   | |

So the order around a permission is:
```
PreToolUse → PermissionRequest → [Notification nudge if user is slow] → (approve + run = SILENCE) → PostToolUse
```

### Session lifecycle (child `claude -p` session)
```
SessionStart (source:"startup")  →  UserPromptSubmit (prompt, prompt_id)  →  Stop  →  SessionEnd (reason:"other")
```

---

## 2. KEY FINDING — there is no "approval" event

`PreToolUse` fires **before** the permission dialog, not after approval. After the user approves, the tool runs
and **no hook fires until `PostToolUse`, which only arrives when the (possibly long) tool completes.**

Consequence for the red light + timer:
- `PermissionRequest` is the clean, immediate **red** trigger. ✅
- The earliest signal that ends the wait is `PostToolUse`. So **red unavoidably spans both the waiting time AND
  the post-approval execution of that one tool.** We cannot flip the light to yellow at the instant of approval —
  Claude Code emits nothing there.
- For the usual short tool this is an imperceptible red flash after approval. For a long approved tool
  (e.g. `npm install`, a build), the widget shows red for the duration of that tool. This is an inherent limit of
  the current hook set, not a bug in our mapping.

This directly answers the §5.2 / §13 question: the spec's hoped-for "PostToolUse = work-resumed-after-approval"
is only half right — `PostToolUse` marks work **finished**, not work **resumed**, and the resume instant is not
observable.

---

## 3. Locked event → state mapping

| Hook event          | State | Widget action |
|---------------------|-------|---------------|
| `SessionStart`      | green | register session (capture PID here) |
| `UserPromptSubmit`  | yellow| new run → reset timer to 0 and start |
| `PreToolUse`        | yellow| keep running |
| `PermissionRequest` | red   | pause timer |
| `PostToolUse`       | yellow| resume/keep running (see §4) |
| `Stop`              | green | stop + freeze last run's time |
| `SessionEnd`        | off   | delete session file |

Notes:
- Use **`PermissionRequest`** for red (immediate). `Notification` with `notification_type:"permission_prompt"`
  also fires but **later** (a nudge) — keep it only as a fallback for adapters that lack `PermissionRequest`.
- `PostToolUseFailure` should map like `PostToolUse` (→ yellow); `PostToolBatch` needs no mapping (informational).
- Red only appears in permission modes that actually prompt (`default`). In `acceptEdits`/`dontAsk`/`bypassPermissions`
  the gate is skipped and `PermissionRequest` never fires — correct, since nothing is waiting on the user.

---

## 4. Timer recommendation (resolves the example: yellow 40s → red → yellow 30s = 1:10)

Pure pause-on-red / resume-on-`PostToolUse` would **undercount** by exactly the approved tool's run time (that run
happens inside the red window). The fix keeps all timer logic in the widget and uses a fact already on the payload:

> `PostToolUse` includes **`duration_ms`** (the actual tool execution time). Have the writer forward the last
> tool's `duration_ms` into the state file (one optional field, e.g. `lastToolMs`). When the widget sees a
> `red → PostToolUse` transition, it **back-credits `duration_ms`** to the work timer. The timer value then comes
> out correct (post-approval execution counted, pure waiting excluded), even though the *light* was red during
> that execution.

Bounded-error fallback (v1, no contract change): just resume the timer at `PostToolUse`. Error per permission =
one tool's duration; negligible for the common short-tool case. Recommend shipping the fallback first, adding the
`duration_ms` back-credit if precision matters.

The light staying red until `PostToolUse` cannot be avoided with current hooks (no approval event exists).

---

## 5. stdin payload schema (observed)

Common (all events): `session_id`, `transcript_path`, `cwd`, `permission_mode`
(`default|plan|acceptEdits|auto|dontAsk|bypassPermissions`), `hook_event_name`, plus an `effort` object here.

- Tool events (`PreToolUse`/`PostToolUse`/`PermissionRequest`): `tool_name`, `tool_input`, `tool_use_id`.
- `PostToolUse`: adds `tool_response`, **`duration_ms`**.
- `PostToolBatch`: `tool_calls[]` (each with name/input/response/id).
- `PermissionRequest`: adds `permission_suggestions[]` (suggested allow-rules).
- `Notification`: `message`, `notification_type` (e.g. `"permission_prompt"`); **no** `permission_mode`.
- `UserPromptSubmit`: `prompt`, `prompt_id`.
- `SessionStart`: `source` (`"startup"`, also `"resume"`/`"clear"`).
- `SessionEnd`: `reason` (`"other"`, also `"clear"`/`"logout"`/`"exit"`).

**No `pid` field on any event.** → The writer must capture the agent PID itself at `SessionStart` by walking up
its process ancestry (matching `claude`), as §6 requires. On `source:"resume"` the PID changes, so re-capture.

---

## 6. Platform / install notes for the real adapter

- Hooks run under **Git Bash on Windows, or PowerShell if Git Bash is absent** (`sh -c` on macOS/Linux). Invoking
  `powershell.exe … -File <abs path>` with **forward-slash** paths is shell-agnostic and worked reliably.
- **Hooks reload mid-session** via the settings file watcher — *but* in this environment the harness owns
  `.claude/settings.local.json` (it rewrites it to record permissions and **clobbered** a `hooks` block placed
  there). Project `.claude/settings.json` hooks also did **not** activate for this session. Only **user-level
  `~/.claude/settings.json`** hooks reliably took effect. The real adapter targets user level anyway (§5.3) —
  consistent. Avoid writing hooks to `settings.local.json`.
- `node` is **not** on PATH here, so the NativeAOT writer binary (or PowerShell) is the right call for hooks — do
  not assume a node runtime.

---

## 7. Instrumentation cleanup

Temporary logging hooks were removed from `~/.claude/settings.json` after capture; the throwaway project
`.claude/settings.json` (instrumentation only) was deleted. The logger (`log-event.ps1`) and evidence
(`~/.agentsignal/phase0-events.log`) are kept. Re-enable by re-adding the per-event hooks that call `log-event.ps1`
to `~/.claude/settings.json`.
