# Adapters

Each supported agent has an **adapter**: per-agent hook configuration that calls `AgentSignal.Writer`
on lifecycle events. The widget never knows about any agent directly — it only reads the state
contract files the writer produces. Adding an agent means writing one adapter here; the widget never
changes.

Keep all adapter-specific files under `adapters/<tool>/`.

## Status

| Adapter | State | Notes |
|---------|-------|-------|
| [`claude/`](claude/) | ✅ implemented (#1) | Event order verified in Phase 0; mapping locked. |
| `codex/`  | planned | Easy second adapter — see below. |
| `antigravity/` | deferred | Feasible but more work — see below. |

## Codex (OpenAI) — planned

Nearly identical to Claude. Codex exposes the same hook vocabulary (SessionStart, UserPromptSubmit,
PreToolUse, PermissionRequest, PostToolUse, Stop, SubagentStart/Stop) with command hooks that receive
JSON on stdin. The adapter is essentially the same hook block in Codex's config location, invoking the
same writer with `codex` as the tool arg (`AgentSignal.Writer codex <state>`). The writer is already
tool-agnostic, so this should only need a `codex/hooks.template.json` plus wiring it as an embedded
template and confirming Codex's real event order (a mini Phase 0).

## Antigravity (Google) — deferred

Feasible but more work. It has hooks (`.agents/hooks.json`) and a Python SDK, but a different /
consolidated event set, and the permission/approval signal (the red light) is less cleanly exposed —
may need extra mapping or a fallback. It is also new and changing fast. Nothing in the core or widget
blocks it; defer until the event model settles.
