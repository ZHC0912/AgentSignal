# Removes the Phase 0 logging hooks installed by install-phase0.ps1 (restores any backed-up
# hooks.json, else deletes the file). Pass the same -Workspace you installed with, if any.
param([string]$Workspace)

$targets = @(
    (Join-Path $env:USERPROFILE '.gemini\config\hooks.json'),
    (Join-Path $env:USERPROFILE '.gemini\antigravity-cli\hooks.json')
)
if ($Workspace) { $targets += (Join-Path $Workspace '.agents\hooks.json') }

foreach ($t in $targets) {
    if (Test-Path "$t.pre-phase0") {
        Move-Item "$t.pre-phase0" $t -Force
        Write-Host "restored: $t"
    } elseif (Test-Path $t) {
        Remove-Item $t -Force
        Write-Host "removed: $t"
    }
}
Write-Host "The event log (~/.agentsignal/antigravity-phase0-events.log) is kept as evidence."
