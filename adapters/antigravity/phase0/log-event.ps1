# Phase 0 logger for Google Antigravity hooks (mirrors adapters/claude/phase0/log-event.ps1).
# Registered for every hook event in hooks.json; appends one line per firing to the log:
#   <ISO timestamp> | <event name (arg)> | env:<ANTIGRAVITY_* / AGY_* vars> | <full stdin JSON>
# Emits NOTHING on stdout (no decision = the hook expresses no opinion, nothing is blocked)
# and always exits 0 so a logging failure can never break the agent.
param([string]$EventName = "?")

try {
    $stdin = [Console]::In.ReadToEnd()
    $env2 = (Get-ChildItem env: | Where-Object { $_.Name -like 'ANTIGRAVITY*' -or $_.Name -like 'AGY_*' } |
        ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
    $line = "{0:o} | {1} | env:{2} | {3}" -f (Get-Date), $EventName, $env2, ($stdin -replace "`r?`n", " ")
    $log = Join-Path $env:USERPROFILE '.agentsignal\antigravity-phase0-events.log'
    New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
    Add-Content -Path $log -Value $line -Encoding utf8
} catch { }
exit 0
