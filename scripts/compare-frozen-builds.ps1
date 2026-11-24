#Requires -PSEdition Core
<#
.SYNOPSIS
Runs the six frozen builds under seven fight setups with a base ref's CLI and pack and with this checkout's, and reports which outputs differ.

.DESCRIPTION
The frozen fixtures' document.json files are Go-era (v1) documents, which the CLI refuses: comparing them compares refusals. This
script builds a v2 document for each build instead, on each side with its own CLI and pack, from a new build and the fixture's
operations.json (the removed "level" operation is dropped, and a party member's Go-era share code goes through "share convert"). The
two sides' documents are compared too, with their pack version left out. The active weapon is the primary weapon.

Each run's output is compared with the pack version left out. The script fails when any document or run differs (unless
-AllowDifferences), and when the number of runs giving results on the base side isn't -ExpectedResults: a setup that is refused
everywhere can't pass as a match. A base of origin/<branch> is fetched first. Both CLIs are published with locked restore.
#>
param(
    [string]$Base = 'origin/main',
    [string]$Dotnet = 'dotnet',
    [int]$ExpectedResults = 23,
    [switch]$AllowDifferences
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$setups = [ordered]@{
    plain = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1 }
    targets = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1; targets = $true }
    buffs = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1; targets = $true; driveSimpleBuffs = $true }
    skills = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1; targets = $true; driveSimpleBuffs = $true; includeSkills = $true; includeIncoming = $true }
    shield = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1; targets = $true; driveSimpleBuffs = $true; includeIncoming = $true; includeShield = $true }
    average = @{ duration = 120; mode = 'average'; iterations = 1; seed = 1 }
    group = @{ duration = 120; mode = 'random'; iterations = 20; seed = 1; targets = $true; driveSimpleBuffs = $true; includeSkills = $true; includeIncoming = $true; enemiesAtOnce = 3 }
}

function Publish-Cli([string]$source, [string]$output) {
    & $Dotnet publish (Join-Path $source 'src/CharacterPlanner.Cli') -c Release -o $output --nologo -v q -p:RestoreLockedMode=true | Out-Host
    if ($LASTEXITCODE) { throw "Publishing the CLI from $source failed" }
    return Join-Path $output 'CharacterPlanner.Cli'
}

# A CLI call's output, parsed; output that isn't JSON (a crash) comes back as a failed result carrying its text and exit code.
function Invoke-Cli([string]$cli, [string[]]$arguments) {
    $text = (& $cli @arguments) -join "`n"
    $code = $LASTEXITCODE
    try { return $text | ConvertFrom-Json }
    catch { return [pscustomobject]@{ ok = $false; crashed = $code; output = $text } }
}

function Read-Result([string]$cli, [string[]]$arguments) {
    $parsed = Invoke-Cli $cli $arguments
    if (-not $parsed.ok) { throw "$($arguments[0..1] -join ' ') failed: $($parsed | ConvertTo-Json -Compress -Depth 5)" }
    return $parsed.result
}

function Format-Json($value) { $value | ConvertTo-Json -Compress -Depth 100 }

# The JSON paths where two parsed outputs differ, for the report.
function Get-Differences($a, $b, [string]$path = '') {
    if ($a -is [pscustomobject] -and $b -is [pscustomobject]) {
        $names = @($a.PSObject.Properties.Name) + @($b.PSObject.Properties.Name) | Sort-Object -Unique
        foreach ($name in $names) { Get-Differences $a.$name $b.$name "$path/$name" }
    } elseif ($a -is [array] -and $b -is [array]) {
        if ($a.Count -ne $b.Count) { "$path count $($a.Count) => $($b.Count)" }
        for ($i = 0; $i -lt [Math]::Min($a.Count, $b.Count); $i++) { Get-Differences $a[$i] $b[$i] "$path[$i]" }
    } elseif ((Format-Json $a) -cne (Format-Json $b)) {
        "$path $($a | ConvertTo-Json -Compress -Depth 3) => $($b | ConvertTo-Json -Compress -Depth 3)"
    }
}

function Write-Differences([string]$label, $a, $b) {
    Write-Host "DIFFER $label"
    Get-Differences $a $b | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}

# One side's v2 document for a frozen build, from a new build and the fixture's operations.
function New-Document([string]$cli, [string]$pack, [string]$operationsPath, [string]$folder) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    $document = Join-Path $folder 'build.json'
    Format-Json (Read-Result $cli @('build', 'new', $pack)) | Set-Content $document
    $operations = @(Get-Content $operationsPath -Raw | ConvertFrom-Json | Where-Object kind -ne 'level')
    # As ShareCode.IsAppCode: an app code is D2P, its version digits and a dot; anything else is Go-era.
    foreach ($operation in $operations | Where-Object { $_.kind -eq 'party_member' -and $_.text -and $_.text -notmatch '^\s*D2P\d+\.' }) {
        $operation.text = (Read-Result $cli @('share', 'convert', $pack, $operation.text)).code
    }
    $kept = Join-Path $folder 'operations.json'
    ConvertTo-Json -InputObject $operations -Depth 100 | Set-Content $kept
    Format-Json (Read-Result $cli @('build', 'apply', $pack, $document, $kept)).document | Set-Content $document
    return $document
}

if ($Base -match '^origin/(.+)$') {
    git -C $repo fetch -q origin $Matches[1]
    if ($LASTEXITCODE) { throw "Could not fetch $Base" }
}
$work = Join-Path ([IO.Path]::GetTempPath()) "planner-compare-$([guid]::NewGuid().ToString('N'))"
$worktree = Join-Path $work 'base'
try {
    New-Item -ItemType Directory $work | Out-Null
    git -C $repo worktree add --detach $worktree $Base -q
    if ($LASTEXITCODE) { throw "Could not check out $Base" }
    $sides = @(
        @{ Cli = Publish-Cli $worktree (Join-Path $work 'cli-base'); Pack = Join-Path $worktree 'data/packs/division-2'; Name = 'base' },
        @{ Cli = Publish-Cli $repo (Join-Path $work 'cli-head'); Pack = Join-Path $repo 'data/packs/division-2'; Name = 'head' }
    )
    $context = Join-Path $work 'context.json'
    Set-Content $context '{"weapon_slot":"primary_weapon"}' -NoNewline

    $same = 0; $differ = 0; $produced = 0; $refused = 0
    foreach ($fixture in Get-ChildItem (Join-Path $repo 'baseline/fixtures/builds') -Directory | Sort-Object Name) {
        $operationsPath = Join-Path $fixture.FullName 'operations.json'
        if (-not (Test-Path $operationsPath)) { continue }
        $documents = foreach ($side in $sides) { New-Document $side.Cli $side.Pack $operationsPath (Join-Path $work "$($side.Name)/$($fixture.Name)") }
        $built = foreach ($document in $documents) {
            $parsed = Get-Content $document -Raw | ConvertFrom-Json
            $parsed.build.PSObject.Properties.Remove('packVersion')
            , $parsed
        }
        if ((Format-Json $built[0]) -cne (Format-Json $built[1])) { $differ++; Write-Differences "$($fixture.Name) document" $built[0] $built[1] }

        foreach ($setup in $setups.GetEnumerator()) {
            $options = Join-Path $work "$($setup.Key).json"
            $setup.Value | ConvertTo-Json | Set-Content $options
            $outputs = for ($i = 0; $i -lt 2; $i++) {
                $parsed = Invoke-Cli $sides[$i].Cli @('build', 'simulate', $sides[$i].Pack, $documents[$i], '--options', $options, '--context', $context)
                if ($parsed.result.pack) { $parsed.result.pack.PSObject.Properties.Remove('version') }
                , $parsed
            }
            if ($outputs[0].ok) { $produced++ } else { $refused++ }
            if ((Format-Json $outputs[0]) -ceq (Format-Json $outputs[1])) { $same++ }
            else { $differ++; Write-Differences "$($fixture.Name) $($setup.Key)" $outputs[0] $outputs[1] }
        }
    }
    Write-Host "$same runs same, $differ differ (documents and runs); on $Base, $produced runs gave results and $refused were refused"
    if ($produced -ne $ExpectedResults) { throw "Expected $ExpectedResults runs with results on $Base, got $($produced): the comparison setup is suspect" }
    if ($differ -and -not $AllowDifferences) { throw "$differ documents or runs differ from $Base" }
} finally {
    if (Test-Path $worktree) { git -C $repo worktree remove --force $worktree }
    git -C $repo worktree prune
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
