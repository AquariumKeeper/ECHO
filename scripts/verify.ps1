#Requires -PSEdition Core
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot)
try {
    & $Dotnet restore CharacterPlanner.slnx --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    & $Dotnet format CharacterPlanner.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE) { throw 'Formatting verification failed' }
    & $Dotnet build CharacterPlanner.slnx -c Release --no-restore
    if ($LASTEXITCODE) { throw 'Release build failed' }
    # A hung test (e.g. a deadlock) fails verification instead of stalling it.
    # Run projects serially: calculation suites must not starve the live-session probes' real timeouts on small runners.
    & $Dotnet test CharacterPlanner.slnx -c Release --no-build --no-restore --blame-hang-timeout 5m -m:1
    if ($LASTEXITCODE) { throw 'Tests failed' }
} finally { Pop-Location }
