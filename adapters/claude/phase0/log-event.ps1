param([string]$EventName = "UNKNOWN", [string]$LogFile = "phase0-events.log")

# Phase 0 instrumentation logger (reused for later event-order investigations).
# Reads the hook's stdin JSON payload and appends one timestamped, single-line
# record to ~/.agentsignal/<LogFile> so we can reconstruct the exact firing
# order of Claude Code lifecycle hooks (e.g. around permission prompts, or an
# Esc-cancelled turn). Temporary instrumentation — removed once the mapping is
# locked. Pass -LogFile to isolate a capture into its own file.

$ErrorActionPreference = "SilentlyContinue"

# Capture the timestamp as early as possible (PowerShell startup already
# happened, but this is still the firing order we care about relative to other
# hooks, since hooks run synchronously).
$ts = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss.fffffff")

# Read the full raw stdin payload.
$payload = [Console]::In.ReadToEnd()
if ($null -eq $payload) { $payload = "" }
# Flatten to a single line so each event is one log record.
$payload = $payload -replace "`r", "" -replace "`n", " "

$logDir = Join-Path $env:USERPROFILE ".agentsignal"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
$logFile = Join-Path $logDir $LogFile

$line = "$ts`t$EventName`t$payload"

# Use a named mutex so concurrent hook invocations don't corrupt the file.
$mutex = New-Object System.Threading.Mutex($false, "Global\AgentSignalPhase0Log")
[void]$mutex.WaitOne()
try {
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
