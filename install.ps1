# AgentSignal one-step installer (Windows).
#
# Installs to a STABLE path — %LOCALAPPDATA%\AgentSignal — so launch-on-startup and the Claude
# hooks never point at a dev bin\Release that can go stale. Run it from the extracted release
# zip (AgentSignal.exe + AgentSignal.Writer.exe next to this script):
#
#   powershell -ExecutionPolicy Bypass -File install.ps1            # install + launch
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Startup   # ...and launch on logon
#
# What it does:
#   1. Copies AgentSignal.exe + AgentSignal.Writer.exe to %LOCALAPPDATA%\AgentSignal\
#   2. Runs "AgentSignal.Writer install claude" — deploys the writer to ~/.agentsignal and merges
#      the Claude Code hooks into ~/.claude/settings.json (append-only, idempotent)
#   3. Optionally registers launch-on-startup pointing at the installed exe (-Startup)
#   4. Launches the widget (skip with -NoLaunch)
#
# Both exes are self-contained: no .NET install is required on this machine.

param(
    [switch]$Startup,   # also register launch-on-startup (HKCU Run, points at the installed exe)
    [switch]$NoLaunch   # install only; don't start the widget at the end
)

$ErrorActionPreference = 'Stop'

$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'AgentSignal'
$app    = Join-Path $src 'AgentSignal.exe'
$writer = Join-Path $src 'AgentSignal.Writer.exe'

if (-not (Test-Path $app) -or -not (Test-Path $writer)) {
    Write-Host 'ERROR: AgentSignal.exe and AgentSignal.Writer.exe must sit next to this script.' -ForegroundColor Red
    Write-Host 'Download the release zip from https://github.com/ZHC0912/AgentSignal/releases/latest'
    Write-Host 'and run install.ps1 from the extracted folder (or publish from source; see README).'
    exit 1
}

# Stop any running widget (installed exe, dev apphost, or dev dll under the dotnet host) so the
# copy below can't hit a locked file.
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'AgentSignal.exe' -or $_.Name -eq 'AgentSignal.App.exe' -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*AgentSignal.App.dll*')
})
foreach ($p in $running) {
    Write-Host "Stopping running widget (pid $($p.ProcessId))..."
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch {}
}
if ($running.Count -gt 0) { Start-Sleep -Milliseconds 500 }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $app, $writer -Destination $dest -Force
Write-Host "Copied AgentSignal to $dest"

# Deploy the writer + merge the Claude Code hooks (user-level ~/.claude/settings.json).
& (Join-Path $dest 'AgentSignal.Writer.exe') install claude
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: hook install failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host 'Claude Code hooks installed.'

if ($Startup) {
    # The installed exe registers ITSELF (stable path) — see StartupManager.LaunchCommand.
    & (Join-Path $dest 'AgentSignal.exe') --startup on
    Write-Host 'Launch-on-startup registered.'
}

if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $dest 'AgentSignal.exe')
    Write-Host 'Widget launched.'
}

Write-Host ''
Write-Host "Done. AgentSignal is installed at $dest" -ForegroundColor Green
Write-Host 'Open a Claude Code session and the dots light up. Click the widget for settings.'
