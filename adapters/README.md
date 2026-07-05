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
| [`antigravity/`](antigravity/) | ✅ implemented (#2) — **IDE only** | Hooks (`PreInvocation`→yellow, `Stop`→green) + a conversation-db poller for red; Phase 0 findings locked. The Antigravity **CLI is future/unverified**. |
| `codex/`  | planned | Easy third adapter — see below. |

## Codex (OpenAI) — planned

Nearly identical to Claude. Codex exposes the same hook vocabulary (SessionStart, UserPromptSubmit,
PreToolUse, PermissionRequest, PostToolUse, Stop, SubagentStart/Stop) with command hooks that receive
JSON on stdin. The adapter is essentially the same hook block in Codex's config location, invoking the
same writer with `codex` as the tool arg (`AgentSignal.Writer codex <state>`). The writer is already
tool-agnostic, so this should only need a `codex/hooks.template.json` plus wiring it as an embedded
template and confirming Codex's real event order (a mini Phase 0).

## Antigravity (Google) — implemented (IDE only)

Yellow/green come from hooks (`PreInvocation`/`Stop` — and `Stop` fires on cancel, so no
stuck-yellow); **red comes from polling the conversation SQLite** (`steps.status` 9 = awaiting
approval), because no hook fires around an approval prompt. Red ends at the approval instant —
cleaner than Claude's. Full design and constraints in [`antigravity/README.md`](antigravity/README.md);
the ground truth is [`antigravity/phase0/FINDINGS.md`](antigravity/phase0/FINDINGS.md).
The Antigravity **CLI** remains future/unverified.
