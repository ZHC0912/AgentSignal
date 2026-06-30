# Claude Code adapter

Adapter #1. Maps Claude Code's lifecycle hooks onto the AgentSignal state contract. The exact event
order was verified empirically before this mapping was locked — see [`phase0/FINDINGS.md`](phase0/FINDINGS.md).

## Event → state mapping (locked)

| Hook event          | State    | Notes |
|---------------------|----------|-------|
| `SessionStart`      | `green`  | register session; writer captures the agent PID here |
| `UserPromptSubmit`  | `yellow` | new run → widget resets the timer |
| `PreToolUse`        | `yellow` | working |
| `PostToolUse`       | `yellow` | still working; writer forwards `duration_ms` → `durationMs` |
| `PermissionRequest` | `red`    | waiting on a permission prompt |
| `Stop`              | `green`  | turn finished; timer freezes |
| `SessionEnd`        | `off`    | writer deletes the session file |

The hook block lives in [`hooks.template.json`](hooks.template.json) with a `{{WRITER_PATH}}`
placeholder. It is embedded into `AgentSignal.Writer` so the deployed binary can self-install.

> **Known limit (accepted):** no hook fires at the instant a permission is approved — the next event
> is `PostToolUse`, which only arrives when the (possibly long) approved tool finishes. So `red`
> unavoidably spans the wait *and* that tool's execution. The widget keeps the timer accurate by
> back-crediting `durationMs` on the `red → PostToolUse` transition; the light staying red during a
> long approved tool is a hard limit of the hook set, not worked around. (See Phase 0 §2/§4.)

## Install

From a built/published writer:

```sh
AgentSignal.Writer install claude
```

This:
1. copies the writer to `~/.agentsignal/AgentSignal.Writer[.exe]`,
2. merges the mapping above into the **user-level** `~/.claude/settings.json`, substituting the
   deployed writer's absolute path,
3. preserves any existing settings and hooks (append-only, idempotent).

**Never** writes to `.claude/settings.local.json` — that file is rewritten by some harnesses and would
drop the hooks (Phase 0 finding).

## Uninstall

Remove the AgentSignal entries (the `… AgentSignal.Writer … claude …` commands) from the `hooks`
block in `~/.claude/settings.json`, and delete `~/.agentsignal/`.
