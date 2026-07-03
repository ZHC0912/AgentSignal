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
  - **FIX 2 resolved — locked Decision #3** (CLAUDE.md A.2): Esc-abort fires **no** hook (proven by a
    live full-event capture), so a session yellow + no file update for **30s** is *displayed* green.
    Display-only (WorkTimer untouched, snaps back intact), never demotes a dangling `PreToolUse`
    (installs/builds stay yellow), no alert/blink on demotion. **Owner confirmed working live.**
    Proven by `--demote-test`.
- **Mid-review (awaiting the owner's live eyeball, nothing else blocking):**
  - **Pill-width swap** (newest): timer pill content-sized/wider; gear + collapsed-chevron = snug
    26×26 icon squares. Check the ⚙ centring live (headless can't render `Segoe Fluent Icons`).
  - The 6.6 behaviours live: orientation switch, edge-flip while dragging, timer collapse/expand,
    two-real-session expanded view, green-blink pulse.
- **Next: Phase 7 (packaging)** — gated on those eyeballs + an explicit go-ahead. Self-contained
  App build (kills the `DOTNET_ROOT`/stale-`bin\Release` startup caveats) + bump `Tmds.DBus.Protocol`
  (NU1903), Windows + Linux.

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
   `--layout` / `--anchor-test` / `--demote-test` are the headless proofs).
3. On the owner's go-ahead: **Phase 7 packaging.**

Useful diagnostics (no display needed): `--dump`, `--watch`, `--layout <png>`, `--anchor-test`,
`--timer-test` (must stay 1:10), `--demote-test`, `--blink-test`, `--config`, `--startup status`.
Full list in CLAUDE.md A.5; gotchas in A.6 (read before debugging anything visual).
