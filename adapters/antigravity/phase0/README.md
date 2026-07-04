# Antigravity Phase 0 — verify the real hook events (instrumentation kit)

Goal: before designing the Antigravity → AgentSignal state mapping, capture **exactly which hook
events fire and when** on a real Antigravity session — especially around a permission/approval
prompt, since Antigravity's documented hook set has **no permission event** (see the findings
section below for what static analysis already established).

## What's known before running (static analysis of the installed build, 2026-07-04)

Binary inspected: `language_server.exe` (Antigravity IDE, build dated 2026-06-25) — the hook engine
lives there (`Loaded hooks.json from %s: %d named hooks, %d total handlers`).

- **Hookable events (complete list in `hooks_pb`):** `PreToolUse`, `PostToolUse`, `PreInvocation`,
  `PostInvocation`, `Stop`. There are **no** permission/approval, session-start, or session-end
  hook messages in the engine — the `PermissionRequest` strings in the binary are unrelated
  protobuf internals (file-permission fields, browser notifications).
- **Hook payload (`HookArgsCommon`):** `conversation_id`, `workspace_paths`, `transcript_path`,
  `artifact_directory_path`, `execution_id`. Hooks also get `ANTIGRAVITY_CONVERSATION_ID` in their
  environment — a usable session key.
- **Hook responses** support `allow_tool` / `deny_reason` (gating), and there is an internal
  agent-state component (`ActivitySnapshot` / `FullyIdle`, `WaitForConversationFullyIdle` API) that
  is NOT exposed as a hook — a possible future fallback surface for "red".
- **Config locations:** global `~/.gemini/config/hooks.json` (IDE) and
  `~/.gemini/antigravity-cli/hooks.json` (CLI); per-workspace `<root>/.agents/hooks.json`.
- A `json-hooks-enabled` feature-flag string exists in the binary — JSON hooks **may be gated**;
  if the run below logs nothing, that gate (or a schema mismatch) is the first suspect.

## Run the capture (≈5 minutes, on a machine with Antigravity)

1. `powershell -ExecutionPolicy Bypass -File install-phase0.ps1 -Workspace <test folder>`
   — installs logging hooks (all five events → `log-event.ps1`) globally and in the test folder.
2. Open the test folder in Antigravity and start an agent conversation.
3. **Scenario A — normal completion:** ask something that runs a safe tool and finishes, e.g.
   *"Run `git status` and summarize the output."* Let it finish completely.
4. **Scenario B — permission prompt:** make sure the agent is NOT in an auto-approve/turbo mode,
   then ask for something that needs review/approval, e.g. *"Run the shell command
   `echo hello > phase0-test.txt` "* — when Antigravity asks for approval, **wait ~30 seconds
   before approving** (so the wait window is visible in timestamps), approve, let it finish.
5. **Scenario C — abort:** start another request and cancel/stop it mid-run.
6. Inspect `~/.agentsignal/antigravity-phase0-events.log`. Each line is
   `timestamp | event | env:ANTIGRAVITY_* | full stdin JSON`.
7. `powershell -ExecutionPolicy Bypass -File uninstall-phase0.ps1 -Workspace <test folder>`
   when done. Keep the log.

## What the log must answer

1. Which of the five events actually fire, and in what order, for a normal tool call?
2. Around the approval in Scenario B: does ANYTHING fire between the pre-tool moment and the
   user's approval? Does the pre-tool event fire before the approval UI or only after approval?
   (This decides whether a red light is even inferable from hooks.)
3. Does `Stop` fire on normal completion? On the Scenario C abort?
4. What exactly is in each payload (`conversation_id`? tool name? a "requires approval" flag?)
   and in the environment?
5. Do global hooks, workspace hooks, or both fire (duplicate lines = both scopes ran)?
