# AgentSignal documentation

- **[Architecture & behaviour](architecture.md)** — the writer → state-files → widget design, the
  state model, timer semantics, multi-session handling, manual reset, settings, and the headless
  diagnostics.
- **[Claude Code hook events](claude-hook-events.md)** — the verified event → state mapping, the
  real event order around permission prompts, known limits of the hook set, and install/uninstall.

Images in this folder (`widget-states.png`, `layout-matrix.png`) are rendered by the app itself —
`AgentSignal.App --screenshot <png>` / `--layout <png>` — so they always reflect the built UI.
`icon.png` is the app icon.
