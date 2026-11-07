#Requires -Version 7
# The C12 library gate, with real releases: an older release and a newer one share a library, as they do when a tester
# upgrades or rolls back. Each must read what the other wrote, and neither may lose or change a build it only read.
# It covers saved builds through the CLI, in a temporary library, so it can't touch real ones. Run it on each release,
# with the last release's ZIP as -OldZip. It needs two ZIPs, so verify.ps1 doesn't run it.
param(
    [Parameter(Mandatory)][string]$OldZip,
    [Parameter(Mandatory)][string]$NewZip
)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('planner-rollback-' + [guid]::NewGuid().ToString('N'))
$library = Join-Path $work 'library'
$failures = [Collections.Generic.List[string]]::new()
$extractBase = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR

function Check([bool]$Passed, [string]$What) {
    Write-Host "$(if ($Passed) { 'ok  ' } else { 'FAIL' }) $What"
    if (!$Passed) { $failures.Add($What) }
}
# Not "Cli": that's PowerShell's alias for Clear-Item, and aliases win over functions.
function Invoke-Planner([string]$Side, [string[]]$Arguments) {
    $output = & (Join-Path $work $Side 'CharacterPlanner.Cli.exe') @Arguments
    [pscustomobject]@{ Exit = $LASTEXITCODE; Json = ($output -join "`n") | ConvertFrom-Json -Depth 64 }
}
function Invoke-Library([string]$Side, [string[]]$Arguments) { Invoke-Planner $Side (@('library') + $Arguments + @('--library', $library)) }
function Require($Result, [string]$What) {
    if ($Result.Exit) { throw "$What failed: $($Result.Json | ConvertTo-Json -Depth 8 -Compress)" }
    $Result.Json.result
}
function Write-Json($Value, [string]$Name) {
    $path = Join-Path $work $Name
    ConvertTo-Json -InputObject $Value -Depth 64 | Set-Content $path -Encoding utf8NoBOM
    $path
}
function Pack([string]$Side) { Join-Path $work $Side 'packs/division-2' }
function Edit-Build([string]$Side, [string]$Document, [object[]]$Operations, [string]$Name) {
    $applied = Invoke-Planner $Side @('build', 'apply', (Pack $Side), $Document, (Write-Json $Operations "$Name.operations.json"))
    Write-Json (Require $applied "Editing $Name on the $Side release").document "$Name.json"
}
function New-Build([string]$Side, [object[]]$Operations, [string]$Name) {
    $blank = Require (Invoke-Planner $Side @('build', 'new', (Pack $Side))) "A new build on the $Side release"
    Edit-Build $Side (Write-Json $blank "$Name-blank.json") $Operations $Name
}
function Save-Build([string]$Side, [string]$Document) { Require (Invoke-Library $Side @('save', $Document)) "Saving on the $Side release" }
function Get-Records { Get-ChildItem $library -Filter '*.build.json' | Sort-Object Name | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" } }
function Test-Unchanged($Before) { (Compare-Object $Before (Get-Records)).Count -eq 0 }

try {
    New-Item -ItemType Directory $library | Out-Null
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $work 'bundles'
    Expand-Archive -LiteralPath $OldZip -DestinationPath (Join-Path $work 'old')
    Expand-Archive -LiteralPath $NewZip -DestinationPath (Join-Path $work 'new')
    $oldVersion = (Invoke-Planner 'old' @('version')).Json.result.version
    $newVersion = (Invoke-Planner 'new' @('version')).Json.result.version
    Write-Host "Old release $oldVersion, new release $newVersion"
    Check ($oldVersion -ne $newVersion) 'the two releases differ'

    $gear = @(
        @{ kind = 'equipment'; slot = 'primary_weapon'; id = 'primary_weapon_acr' },
        @{ kind = 'talent'; slot = 'primary_weapon'; target = 'talent'; id = 'weapon_talent_ignited' },
        @{ kind = 'active_skill'; slot = 'skill_slot_1'; id = 'skill_explosive_seeker_mine' })

    # Upgrading: the new release reads what the old one saved, and reading it changes nothing.
    $old = Save-Build 'old' (New-Build 'old' $gear 'from-old')
    $saved = Get-Records
    $entry = (Invoke-Library 'new' @('list')).Json.result.entries | Where-Object id -EQ $old.id
    Check ($entry -and !$entry.problem) 'the new release lists the old release''s build'
    $loaded = Invoke-Library 'new' @('load', $old.id)
    Check ($loaded.Exit -eq 0) 'the new release loads it'
    $copy = Write-Json $loaded.Json.result.document 'from-old-loaded.json'
    Check ((Invoke-Planner 'new' @('build', 'resolve', (Pack 'new'), $copy)).Exit -eq 0) 'the new release resolves it on its own pack'
    Check (Test-Unchanged $saved) 'the new release''s reading changed no record'

    # Rolling back: the old release meets a build holding a skill mod it may not know.
    $mod = @{ kind = 'skill_mod'; slot = 'skill_slot_1'; target = 'payload'; id = 'payload_damage'; value = 0.05 }
    $new = Save-Build 'new' (New-Build 'new' ($gear + $mod) 'from-new')
    $saved = Get-Records
    $list = (Invoke-Library 'old' @('list')).Json.result.entries
    Check (@($list).Count -eq 2 -and !(@($list).problem | Where-Object { $_ })) 'the old release lists both builds'
    $rolled = Invoke-Library 'old' @('load', $new.id)
    Check ($rolled.Exit -eq 0) 'the old release loads the new release''s build'
    $rolledFile = Write-Json $rolled.Json.result.document 'from-new-loaded.json'
    $resolved = Invoke-Planner 'old' @('build', 'resolve', (Pack 'old'), $rolledFile)
    $codes = @($resolved.Json.diagnostics | Where-Object severity -EQ 'error').code
    if ($resolved.Exit -eq 0) { Write-Warning 'The old release knows this mod, so unknown content wasn''t exercised. Pick an older -OldZip, or a newer mod.' }
    Check ($resolved.Exit -eq 0 -or $codes -contains 'invalid-skill-mod') "the old release refuses what it doesn't know by name ($($codes -join ', '))"
    Check (Test-Unchanged $saved) 'the old release''s reading changed no record'

    # Saving back from the old release keeps what it can't use, so rolling forward loses nothing.
    Check ((Invoke-Library 'old' @('save', $rolledFile, '--replace', $new.id, '--expected-version', $rolled.Json.result.version)).Exit -eq 0) 'the old release saves that build back'
    $forward = Invoke-Library 'new' @('load', $new.id)
    Check ($forward.Exit -eq 0) 'the new release loads it again'
    $roll = if ($forward.Exit -eq 0) { $forward.Json.result.document.build.skills.skill_slot_1.mods.rolls.payload } else { @() }
    Check (@($roll).Count -eq 2 -and $roll[0] -eq 'payload_damage' -and $roll[1] -eq 0.05) 'the skill mod survives the round trip'

    # A record's version is its content, so a real edit makes the other release's copy stale.
    $renamed = Edit-Build 'old' $copy @(@{ kind = 'name'; text = 'Edited by the old release' }) 'renamed'
    Check ((Invoke-Library 'old' @('save', $renamed, '--replace', $old.id, '--expected-version', $old.version)).Exit -eq 0) 'the old release edits its build'
    $stale = Invoke-Library 'new' @('save', $copy, '--replace', $old.id, '--expected-version', $old.version)
    Check ($stale.Exit -ne 0 -and @($stale.Json.diagnostics).code -contains 'library-conflict') 'a stale edit conflicts across releases'
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractBase
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures.Count) { throw "$($failures.Count) library check(s) failed: $($failures -join '; ')" }
Write-Host 'Library upgrade and rollback passed'
exit 0
