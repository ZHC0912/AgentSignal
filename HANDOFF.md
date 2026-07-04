# HANDOFF — resume AgentSignal on a new machine

> Written 2026-07-03 for a laptop switch. **CLAUDE.md §A is the authoritative status** (read it first);
> this file is the 2-minute version plus the exact commands to get running.

## Where things stand

- **Phases 0–6 + 6.5: done & accepted.** Hook mapping, Core/Writer/installer, widget, timer,
  multi-session, settings/alerts/tray/startup, green-blink, three-piece chip layout.
- **Phase 6.6 + all follow-up fixes: built & headless-verified, committed.**
  - Vertical orientation, smart pill direction (edge flip), collapsible timer, click rewire.
  - FIX 1 per-session timers in the expanded view; FIX 3 direction-aware chevron (`›`/`‹`);
    FIX 4 dots-pill fixed anchor (84×26 reservation slot).
  - **FIX 2: the 30s stale-yellow auto-demotion was built, then REVERSED and removed 2026-07-04**
    (false green while Claude was still thinking — no hook fires while deliberating, so no threshold
    works; see the reversed Decision #3 in CLAUDE.md A.2). **The light only changes on real events**;
    an Esc-abort stuck-yellow is cleared manually via the reset (Ctrl+Alt+R / Settings button).
    The zero-events-on-Esc investigation still stands as ground truth.
- **Manual reset + modal Settings (2026-07-04): built, `--reset-test` ALL PASS** (CLAUDE.md A.9).
  Reset = Settings button or global **Ctrl+Alt+R** → all sessions forced green (files rewritten,
  timers frozen as the last run's time, no alert/blink); hotkey **verified live end-to-end**.
  Modal Settings = widget inert while Settings open (clicks silently ignored — the flash/ding nag
  was tried three ways, did nothing on the owner's machine, and is removed/PARKED); hotkey dead
  while Settings open. **Modal shield confirmed working live by the owner.**
- **Mid-review (awaiting the owner's live eyeball, nothing else blocking):**
  - **The reset UX** (newest): Ctrl+Alt+R / Settings button on a real stuck yellow.
  - **Pill-width swap**: timer pill content-sized/wider; gear + collapsed-chevron = snug
    26×26 icon squares. Check the ⚙ centring live (headless can't render `Segoe Fluent Icons`).
  - The 6.6 behaviours live: orientation switch, edge-flip while dragging, timer collapse/expand,
    two-real-session expanded view, green-blink pulse.
- **Phase 7 (Windows packaging): built 2026-07-04.** Self-contained single-file `AgentSignal.exe`
  (icon, ≈46MB) + trimmed `AgentSignal.Writer.exe` (≈11MB), both verified with no .NET on the
  machine; `Tmds.DBus.Protocol` pinned 0.21.3 (NU1903 cleared); one-step `install.ps1` →
  `%LOCALAPPDATA%\AgentSignal` (stable startup path); portfolio README + docs renders.
  **v1.0.0 GitHub Release staged, awaiting the owner's explicit go-ahead to tag/publish.**
  Linux packaging still to do.

## Get running on this machine

```powershell
# prereq: .NET 10 SDK (repo uses AgentSignal.slnx; App is framework-dependent net10.0)
git pull
dotnet build AgentSignal.slnx -c Release

# run the widget (use the plain `dotnet` if it's on PATH; the ~/.dotnet form if not):
& "$env:USERPROFILE\.dotnet\dotnet.exe" "src\AgentSignal.App\bin\Release\net10.0\AgentSignal.App.dll"

# per-machine, once: deploy the writer + merge the Claude hooks (creates ~/.agentsignal)
dotnet publish src/AgentSignal.Writer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
& "src\AgentSignal.Writer\bin\Release\net10.0\win-x64\publish\AgentSignal.Writer.exe" install claude
```

**Per-machine state does NOT transfer:** `~/.agentsignal/` (writer, config.json = colours/position/
scale, sessions/) and the hooks in `~/.claude/settings.json` are recreated by the installer above.

## Launch-on-startup caveat (bit us twice)

The HKCU `Run` value bakes in an **absolute path to the registering machine's `bin\Release`** and
never updates itself. On a new machine: enable it via the Settings toggle or
`... AgentSignal.App.dll --startup on` **run from the Release build**. It launches whatever sits in
`bin\Release` at logon — a silently-failed rebuild leaves the old dll = "old UI at boot".
Check with `--startup status`; details in CLAUDE.md A.4.

## Immediate next steps

1. Live-eyeball the pill-width swap + the outstanding 6.6 behaviours (list above).
2. Fix anything the eyeball turns up (visual-only tweaks live in `PillView.axaml` styles;
   `--layout` / `--anchor-test` / `--reset-test` are the headless proofs).
3. On the owner's go-ahead: **Phase 7 packaging.**

Useful diagnostics (no display needed): `--dump`, `--watch`, `--layout <png>`, `--anchor-test`,
`--timer-test` (must stay 1:10), `--reset-test`, `--blink-test`, `--config`, `--startup status`.
Full list in CLAUDE.md A.5; gotchas in A.6 (read before debugging anything visual).
