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
#   1. Copies AgentSignal.exe + AgentSignal.Writer.exe (+ native sidecar dlls, e.g. e_sqlite3.dll
#      for the Antigravity poller) to %LOCALAPPDATA%\AgentSignal\
#   2. Runs "AgentSignal.Writer install claude" — deploys the writer to ~/.agentsignal and merges
#      the Claude Code hooks into ~/.claude/settings.json (append-only, idempotent)
#   3. If the Antigravity IDE is present (~/.gemini/antigravity exists), also runs
#      "AgentSignal.Writer install antigravity" (merges hooks into ~/.gemini/config/hooks.json)
#   4. Optionally registers launch-on-startup pointing at the installed exe (-Startup)
#   5. Launches the widget (skip with -NoLaunch)
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
# Native sidecars (e_sqlite3.dll: SQLite for the Antigravity red poller) must travel with the
# writer — its self-deploy copies dlls from beside ITSELF, so they have to reach $dest first.
Get-ChildItem -Path $src -Filter '*.dll' -ErrorAction SilentlyContinue |
    Copy-Item -Destination $dest -Force
Write-Host "Copied AgentSignal to $dest"

# Deploy the writer + merge the Claude Code hooks (user-level ~/.claude/settings.json).
& (Join-Path $dest 'AgentSignal.Writer.exe') install claude
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: hook install failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host 'Claude Code hooks installed.'

# Antigravity IDE adapter (optional; auto-detected). Non-fatal: a failure here (e.g. a home path
# containing a space, which Antigravity's unquoted hook commands cannot express) skips the adapter
# but leaves the Claude install intact.
if (Test-Path (Join-Path $env:USERPROFILE '.gemini\antigravity')) {
    & (Join-Path $dest 'AgentSignal.Writer.exe') install antigravity
    if ($LASTEXITCODE -eq 0) { Write-Host 'Antigravity IDE hooks installed.' }
    else { Write-Host "WARNING: Antigravity hook install failed (exit $LASTEXITCODE); Claude install is unaffected." -ForegroundColor Yellow }
}

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
