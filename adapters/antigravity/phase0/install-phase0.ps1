# Installs the Phase 0 logging hooks for Google Antigravity on THIS machine.
# Writes hooks.json (all five documented events -> log-event.ps1) to every location Antigravity
# is known/suspected to load from:
#   1. ~/.gemini/config/hooks.json            (IDE global customizations root)
#   2. ~/.gemini/antigravity-cli/hooks.json   (Antigravity CLI global scope, if the CLI is used)
#   3. <workspace>/.agents/hooks.json         (pass -Workspace <path> for the project you'll test in)
# Existing hooks.json files are backed up to hooks.json.pre-phase0 first. Remove everything with
# uninstall-phase0.ps1. The logger only appends to ~/.agentsignal/antigravity-phase0-events.log;
# it never blocks anything.
param([string]$Workspace)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = (Resolve-Path (Join-Path $here 'log-event.ps1')).Path -replace '\\', '/'

# Schema per the documented working example: top level = hook NAME, then event -> matcher groups.
# Every event gets a matcher of ".*" (harmless if ignored for non-tool events) and passes the
# event name as an argument, since the stdin payload may not identify the event.
$events = @('PreToolUse', 'PostToolUse', 'PreInvocation', 'PostInvocation', 'Stop')
$eventMap = [ordered]@{}
foreach ($e in $events) {
    $eventMap[$e] = @(
        [ordered]@{
            matcher = '.*'
            hooks = @(
                [ordered]@{
                    type    = 'command'
                    command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`" $e"
                    timeout = 10
                }
            )
        }
    )
}
$json = [ordered]@{ 'agentsignal-phase0-log' = $eventMap } | ConvertTo-Json -Depth 8

$targets = @(
    (Join-Path $env:USERPROFILE '.gemini\config\hooks.json'),
    (Join-Path $env:USERPROFILE '.gemini\antigravity-cli\hooks.json')
)
if ($Workspace) { $targets += (Join-Path $Workspace '.agents\hooks.json') }

foreach ($t in $targets) {
    New-Item -ItemType Directory -Force -Path (Split-Path $t) | Out-Null
    if ((Test-Path $t) -and -not (Test-Path "$t.pre-phase0")) { Copy-Item $t "$t.pre-phase0" }
    Set-Content -Path $t -Value $json -Encoding ascii
    Write-Host "installed: $t"
}
Write-Host "`nLog will appear at: $env:USERPROFILE\.agentsignal\antigravity-phase0-events.log"
Write-Host "Now run the scenarios in README.md, then send that log back."
