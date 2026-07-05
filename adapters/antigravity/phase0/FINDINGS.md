# Antigravity Phase 0 — FINDINGS (locked 2026-07-05)

Result of the live capture runs described in [README.md](README.md), plus a follow-up live
DB-polling investigation. **Bottom line: a full green/yellow/red mapping is buildable for
Antigravity — yellow/green from hooks, red from polling the conversation SQLite — and the red
semantics come out *cleaner* than Claude Code's** (red can end at the approval instant, see §3).

Everything below was observed live on Antigravity IDE (build dated 2026-06-25, hook engine in
`language_server.exe`) on Windows, 2026-07-05, across two instrumented sessions: three scripted
scenarios (normal completion, approval wait on a file-writing command, user cancel mid-run) plus
two follow-ups (a denied command; a slow `ping -n 20` approved after a ~40 s wait, to separate
the approval moment from tool execution).

---

## 1. Hook events (what fires, and when)

All five documented events fire: `PreToolUse`, `PostToolUse`, `PreInvocation`, `PostInvocation`,
`Stop`. There are no session-start/session-end/permission events (confirms the static analysis in
the README).

Observed order for a normal turn:

```
PreInvocation                      ← turn starts (user prompt submitted)
  … model call …
PreToolUse                         ← the model PROPOSES a tool (fires BEFORE any approval UI)
  [approval wait + tool execution — TOTAL HOOK SILENCE]
PostToolUse                        ← only after approval AND execution complete
PostInvocation / PreInvocation     ← bracket each model invocation within the turn
  … model call …
PostInvocation
Stop                               ← turn complete
```

Key findings:

- **No hook fires when the approval prompt appears, while it is showing, or at the moment the
  user approves.** `PreToolUse` fires at proposal time (before the approval UI); the next event
  is `PostToolUse` after the approved tool *finishes*. Confirmed twice with deliberate waits at
  the prompt (103 s and 48 s of hook silence spanning the wait). **Red is not inferable from
  hooks** — not even as "PreToolUse without PostToolUse", since that is indistinguishable from a
  long-running tool.
- **`Stop` fires on a user cancel/abort** — unlike Claude Code, where an Esc-abort fires nothing
  (the cause of the stuck-yellow problem there, see Decision #3 in the project notes). Captured
  live: the cancel killed the in-flight `PostToolUse` hook processes ("context canceled") and
  `Stop` fired immediately after. **Antigravity has no stuck-yellow problem; green is reliable on
  both completion and abort.**
- `PreToolUse`/`PostToolUse` are not strictly 1:1 (extra `PostToolUse` firings were observed,
  e.g. around async command output collection); both map to yellow anyway, so this is harmless.
- **Double-scope firing caveat:** hooks.json is loaded from BOTH the global location
  (`~/.gemini/config/hooks.json`) and the workspace (`<root>/.agents/hooks.json`), and **both
  scopes fire for every event** (the engine logs handlers `_0_0`/`_0_1` per event). A real
  adapter must install to exactly ONE scope — global — or the writer will double-fire on every
  event.
- Hook payload contents (stdin JSON, `ANTIGRAVITY_CONVERSATION_ID` env) remain unverified because
  the Phase 0 logger never executed (see §5) — the event order above was recovered from
  `%APPDATA%\Antigravity\logs\language_server.log`, which logs every hook execution with
  timestamps. A session key is available regardless: the conversation id is the db filename (§2).

## 2. The conversation DB (the red source)

`~/.gemini/antigravity/conversations/<conversation_id>.db` — SQLite (WAL mode), one db per
conversation; **the filename is the conversation id** (usable as the session key). The `steps`
table is the live turn state: one row per step with `idx`, `step_type`, `status`,
`permissions` (protobuf blob), `metadata`, `step_payload`.

Verified live with a 250 ms watcher (copy db + `-wal`/`-shm`, read the copy — never the live
file): two 30-minute sessions, 4 reads/second, zero read errors. Rows and status changes land in
the db within one poll tick (~0.3 s) of the user-visible moment; a streaming model response is
even visible as its payload grows tick by tick.

**Decoded `steps.status` enum (all values observed live):**

| status | meaning                                | observed via |
|--------|----------------------------------------|--------------|
| 2      | tool executing (post-approval)         | approved `ping -n 20` running for ~20 s |
| 3      | completed                              | every finished step |
| 6      | cancelled mid-run (user abort)         | the scenario-C cancel |
| 7      | **denied at the approval prompt**      | the deny follow-up |
| 8      | model response streaming               | every turn |
| 9      | **awaiting approval**                  | all three approval-gated commands |

Approval-gated lifecycle, as captured:

- The tool step appears with `status=9` and **no** `permissions` blob **the instant the approval
  prompt shows** (~0.3 s after the model proposes the command), and holds 9 for the entire wait
  (75 s and 107 s captured live).
- On **approve**: `status` flips 9→**2** and the `permissions` blob lands in the same tick —
  protobuf `{ rule { key:"command", value:"<cmdline>" }, decision:1 }`. On completion: 2→**3**.
- On **deny**: 9→**7**, no permissions blob (the step payload carries the rejection).

Also present but NOT a live source: `brain/<id>/.system_generated/logs/transcript.jsonl` is
human-readable and confirms timelines, but entries are appended only **after** a step completes —
post-hoc, useless for red.

## 3. The red rule (and why it beats Claude's)

```
any step with status 9            → red     (user is being asked for approval)
else any step with status 2 or 8  → yellow  (tool running / model streaming)
else                              → green
```

**Red ends at the approval instant** (the 9→2 transition), not when the approved tool finishes.
This is strictly better than Claude Code's hook-based red, where locked Decision #2 accepts that
red unavoidably spans the approved tool's execution because no hook fires at approval.
Antigravity's db even distinguishes deny (7) from approve (2→3) and cancel (6).

## 4. Poller design notes (for the future adapter — NOT built yet)

- The db grows with conversation length, so **stat the file first and re-read only when
  mtime/size changed** — don't copy every tick. The widget's existing 250 ms cadence fits.
- Always copy `db` + `-wal` (+ `-shm` if present) and open the copy; never open the live db.
- Red/yellow need only the newest/active conversation dbs; hooks (`PreInvocation`/`Stop`) can
  drive the yellow/green edges and delimit which conversation is active.

## 5. Instrumentation gotcha: the hook-command quoting bug (fixed)

Antigravity splits the hook `command` string itself and does **not** strip quotes — a quoted
`-File "<path>"` argument reached PowerShell with literal quote characters, failing every firing
with *"Illegal characters in path"* (visible in `language_server.log`; the Phase 0 log stayed
empty apart from the install-time smoke test). `install-phase0.ps1` now writes the script path
**unquoted** and refuses to install from a path containing spaces. Corollary: the future
adapter's hook command must also avoid quoted arguments (deploy the writer to a space-free path,
e.g. `~/.agentsignal/`).
