# Claude Code hook events — verified reference

How the Claude Code adapter drives the [state contract](architecture.md#the-state-contract), and
what Claude Code's hooks actually do around the cases that matter. Everything here was verified
empirically (Claude Code 2.1.196, 2026-06-30) by logging every lifecycle event with its full stdin
payload; the raw evidence and logger live in
[`adapters/claude/phase0/`](../adapters/claude/phase0/FINDINGS.md).

## Event → state mapping (locked)

| Hook event | State | Notes |
|---|---|---|
| `SessionStart` | `green` | register session; the writer captures the agent PID here (re-captured on `source:"resume"`) |
| `UserPromptSubmit` | `yellow` | new run → the widget resets the timer |
| `PreToolUse` | `yellow` | working |
| `PostToolUse` | `yellow` | still working; the writer forwards `duration_ms` → `durationMs` |
| `PermissionRequest` | `red` | waiting on a permission prompt |
| `Stop` | `green` | turn finished; timer freezes |
| `SessionEnd` | `off` | the writer deletes the session file |

The hook block is [`adapters/claude/hooks.template.json`](../adapters/claude/hooks.template.json);
it's embedded in the writer so `AgentSignal.Writer install claude` can self-install: it deploys the
writer to `~/.agentsignal/` and merges the hooks into the **user-level** `~/.claude/settings.json`
(append-only, idempotent — existing hooks are preserved). It never writes to
`.claude/settings.local.json`: some harnesses rewrite that file and would silently drop the hooks.

## Verified event order

Normal tool call:

```
PreToolUse → (tool executes) → PostToolUse (carries duration_ms)
```

Tool call that needs permission — the critical case:

```
PreToolUse → PermissionRequest → [Notification nudge if you're slow]
          → (you approve; tool runs — SILENCE) → PostToolUse
```

Session lifecycle:

```
SessionStart → UserPromptSubmit → … → Stop → SessionEnd
```

`PermissionRequest` is the clean, immediate red trigger. The `Notification` event
(`notification_type:"permission_prompt"`) also fires, but seconds later — it's a nudge, not an
approval signal. `PermissionRequest` only fires in permission modes that actually prompt
(`default`); in `acceptEdits`/`bypassPermissions` the gate is skipped, which is correct — nothing
is waiting on you.

## Known limits (by design of the hook set)

**1. There is no "approval" event.** After you approve a permission, the tool runs and *no hook
fires* until `PostToolUse` — which only arrives when the (possibly long) approved tool finishes.
So the light unavoidably stays **red through the wait *and* that tool's execution**; the resume
instant is not observable. The *timer* stays accurate anyway: `PostToolUse` carries the tool's real
`duration_ms`, and the widget back-credits it on the red → yellow transition (execution counted,
your waiting excluded).

**2. Aborting a run with Esc fires no event.** A user interrupt skips `Stop` entirely (an interrupt
hook is an open Claude Code feature request), and it even swallows the `PostToolUse` a just-finished
tool was owed — verified live with full-event logging: the aborted session's trail simply stops. The
light therefore stays yellow after an Esc-abort. No timeout can fix this truthfully (long thinking
stretches also fire no hooks and are indistinguishable from an abort), so the widget's answer is the
**manual reset** — <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>R</kbd> or the Settings button forces green
through the normal contract.

## Payload notes (observed)

All events carry `session_id`, `cwd`, `transcript_path`, `permission_mode`, `hook_event_name` on
stdin as JSON. Tool events add `tool_name`/`tool_input`; `PostToolUse` adds `tool_response` and
**`duration_ms`**; `UserPromptSubmit` adds `prompt`; `SessionStart`/`SessionEnd` carry
`source`/`reason`. **No event carries a PID** — that's why the writer captures the agent's PID
itself at `SessionStart` by walking up its process ancestry (it's what session liveness probes).

## Uninstall

Remove the AgentSignal entries (the `… AgentSignal.Writer … claude …` commands) from the `hooks`
block in `~/.claude/settings.json`, and delete `~/.agentsignal/`.

## Future adapters

The seam is the writer + one hook block per agent. **Codex** exposes a near-identical hook
vocabulary (command hooks, JSON on stdin) and is the natural second adapter — same writer, `codex`
as the tool argument. Adapter files live under `adapters/<tool>/`.
