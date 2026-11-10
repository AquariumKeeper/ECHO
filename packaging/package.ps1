#Requires -PSEdition Core
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Push-Location (Split-Path $PSScriptRoot)
# A release names no build folder, account or commit. Debug records are compressed inside each exe, so a plain search can't see them: this
# opens each one as well as searching the exe's own bytes. Third-party libraries' own source links are theirs to carry. A debug record
# stores a path as separate length-prefixed parts, so $Parts are matched as whole parts, and a folder called "core" can't match CharacterPlanner.Core.
function Assert-NoBuildIdentity([string]$Exe, [string[]]$Words, [string[]]$Parts) {
    $leaf = Split-Path $Exe -Leaf
    $bytes = [IO.File]::ReadAllBytes($Exe)
    $text = [Text.Encoding]::Latin1.GetString($bytes)
    $wide = [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($word in $Words) {
        if ($text.Contains($word, [StringComparison]::OrdinalIgnoreCase) -or $wide.Contains($word, [StringComparison]::OrdinalIgnoreCase)) { throw "$leaf embeds this build's identity: $word" }
    }
    $wanted = @($Words) + @($Parts | ForEach-Object { [string][char]$_.Length + $_ })
    $records = 0
    for ($at = $text.IndexOf('MPDB', [StringComparison]::Ordinal); $at -ge 0; $at = $text.IndexOf('MPDB', $at + 4, [StringComparison]::Ordinal)) {
        if ($at + 8 -gt $bytes.Length) { break }
        $size = [BitConverter]::ToUInt32($bytes, $at + 4)
        if ($size -lt 4 -or $size -gt 100MB) { continue }
        $debug = [byte[]]::new($size)
        $stream = [IO.Compression.DeflateStream]::new([IO.MemoryStream]::new($bytes, $at + 8, $bytes.Length - $at - 8), [IO.Compression.CompressionMode]::Decompress)
        try {
            try { $stream.ReadExactly($debug, 0, $size) } catch [IO.InvalidDataException] { continue } catch [IO.EndOfStreamException] { continue }
        }
        finally { $stream.Dispose() }
        if ([Text.Encoding]::ASCII.GetString($debug, 0, 4) -cne 'BSJB') { continue }
        $records++
        $found = [Text.Encoding]::Latin1.GetString($debug)
        foreach ($word in $wanted) {
            if ($found.Contains($word, [StringComparison]::OrdinalIgnoreCase)) { throw "$leaf embeds this build's identity: $($word.TrimStart([char]$word[0]))" }
        }
    }
    if (!$records) { throw "$leaf has no debug information to check" }
}
$smoke = $null
$scratch = @()
try {
    $root = (Get-Location).Path
    $artifacts = Join-Path $root 'artifacts'
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $version = (& $Dotnet msbuild src/CharacterPlanner.Cli/CharacterPlanner.Cli.csproj -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE -or !$version) { throw 'Could not read the package version' }
    $run = [guid]::NewGuid().ToString('N')
    $stage = Join-Path $artifacts "stage-$run"
    $publish = Join-Path $artifacts "publish-$run"
    $scratch += $stage, $publish
    New-Item -ItemType Directory $stage | Out-Null
    # Each app publishes as one self-contained exe, so a user unzipping the release sees the two programs, the docs and
    # two folders rather than hundreds of runtime files. The exes aren't compressed: the ZIP compresses them as well,
    # and uncompressed bundles start without unpacking their assemblies in memory. The apps' own debug information is
    # embedded, so crash traces keep line numbers, but for the Desktop assembly's own (see its project file); the graphics libraries' native symbol files (about 100 MB) are not
    # shipped. Native DLLs are extracted to %TEMP% on first run. Each publish writes the exact .deps.json it bundles
    # beside its output, for the license check. The two apps then merge into one folder; a file both publish (the
    # pack) must be byte-identical, so neither silently overwrites the other's.
    # Publishing below doesn't restore, and ReadyToRun needs its compiler package from a restore that knows about it.
    & $Dotnet restore CharacterPlanner.slnx --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    foreach ($name in @('Desktop', 'Cli')) {
        $output = Join-Path $publish $name
        # ReleasePackage applies the release debug policy in Directory.Build.props: paths mapped to /_/, no source links.
        $singleFile = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:ReleasePackage=true',
            "-p:PublishDepsFilePath=$(Join-Path $publish "$name.deps.json")")
        & $Dotnet publish "src/CharacterPlanner.$name/CharacterPlanner.$name.csproj" -c Release -r win-x64 --self-contained true --no-restore -o $output @singleFile
        if ($LASTEXITCODE) { throw "Publish failed: $name" }
        foreach ($file in (Get-ChildItem $output -File -Recurse | Where-Object Extension -ne '.pdb')) {
            $relative = [IO.Path]::GetRelativePath($output, $file.FullName)
            $target = Join-Path $stage $relative
            if (Test-Path $target) {
                if ((Get-FileHash $target -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) {
                    throw "Desktop and CLI publish different copies of $relative"
                }
                continue
            }
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $file.FullName $target
        }
    }
    # The release ships no game art. The pack's only artwork is ShadeTech's original slot images (R34): every shipped
    # asset must be registered, and every registered source must be MIT, the notice licenses/ carries.
    $presentation = Get-Content -Raw (Join-Path $stage 'packs/division-2/presentation.json') | ConvertFrom-Json
    $registered = @($presentation.artwork.assets | ForEach-Object path | Sort-Object)
    $shippedAssets = @(Get-ChildItem (Join-Path $stage 'packs/division-2/assets') -File -Recurse | ForEach-Object { 'assets/' + [IO.Path]::GetRelativePath((Join-Path $stage 'packs/division-2/assets'), $_.FullName).Replace([char]92, '/') } | Sort-Object)
    if (!$registered -or (Compare-Object $registered $shippedAssets)) { throw 'Shipped pack assets differ from the registered artwork' }
    if (@($presentation.artwork.sources | Where-Object license -ne 'MIT').Count) { throw 'A shipped artwork source is not MIT-licensed' }
    # Every package and the runtime the apps publish must be one whose license and notices ship in licenses/:
    # the README's covered list matches the .deps.json libraries exactly.
    $covered = @([regex]::Matches((Get-Content -Raw packaging/licenses/README.md), '(?m)^- `([^`]+)`\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    # A single-file app carries its .deps.json inside the exe; the publish also wrote that exact file beside its output.
    $published = @(foreach ($name in @('Desktop', 'Cli')) {
        $deps = Get-Content -Raw (Join-Path $publish "$name.deps.json") | ConvertFrom-Json
        $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'project' } | ForEach-Object Name
    }) | Sort-Object -Unique
    $drift = Compare-Object $covered $published
    if (!$covered -or $drift) { throw "Shipped licenses do not cover the published packages: $(($drift | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', ')" }
    # Release documents: the README, attribution and limits, the CLI guide, the tester checklist and every license.
    Copy-Item packaging/README.txt $stage
    Copy-Item LICENSE (Join-Path $stage 'LICENSE.txt')
    foreach ($name in @('ATTRIBUTION.md', 'CLI.md', 'IN_GAME_CHECKLIST.md', 'USER_ARTWORK.md', 'ARTWORK_PACK_FORMAT.md')) { Copy-Item (Join-Path 'docs' $name) $stage }
    $licenses = Join-Path $stage 'licenses'
    New-Item -ItemType Directory $licenses | Out-Null
    Copy-Item packaging/licenses/* $licenses
    Copy-Item licenses/division2-data-CC-BY-4.0.txt $licenses
    # Exactly these files ship, nothing else: the two programs, the documents, the license texts this repository
    # keeps, the pinned pack files and the manifest written below. A stray file anywhere is a packaging mistake.
    $packPins = @((Get-Content -Raw (Join-Path $root 'data/deliveries/division-2/assembly.json') | ConvertFrom-Json).outputs.PSObject.Properties |
        Where-Object { $_.Name.StartsWith('pack/') } | ForEach-Object { 'packs/division-2/' + $_.Name.Substring(5) })
    $licenseFiles = @(Get-ChildItem packaging/licenses -File | ForEach-Object { 'licenses/' + $_.Name }) + 'licenses/division2-data-CC-BY-4.0.txt'
    $allowed = @('ARTWORK_PACK_FORMAT.md', 'ATTRIBUTION.md', 'CLI.md', 'CharacterPlanner.Cli.exe', 'CharacterPlanner.Desktop.exe',
        'IN_GAME_CHECKLIST.md', 'LICENSE.txt', 'README.txt', 'USER_ARTWORK.md') + $licenseFiles + $packPins
    $staged = @(Get-ChildItem $stage -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName).Replace([char]92, '/') })
    $stray = Compare-Object ($allowed | Sort-Object -Unique) ($staged | Sort-Object)
    if ($stray) { throw "Unexpected release contents: $(($stray | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', ')" }
    # Ordinal order on the archive path, so member order never depends on the machine's culture.
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem $stage -File -Recurse)) { $paths.Add([IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')) }
    $paths.Sort([StringComparer]::Ordinal)
    $members = @($paths | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash (Join-Path $stage $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ version = $version; runtime = 'win-x64'; scope = 'tester release'; files = $members } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'release-manifest.json') -Encoding utf8
    # The top level is exactly twelve entries: two programs, seven documents, the manifest and two folders.
    $top = @(Get-ChildItem $stage | ForEach-Object Name)
    if ($top.Count -ne 12) { throw "Unexpected release top level: $($top -join ', ')" }
    # The version is in the name, so Windows' Extract All makes a folder that says which release it is.
    # Checked before the ZIP exists, so a refused build never takes the release's name.
    $origin = (& git remote get-url origin 2>$null | Out-String).Trim()
    $commit = (& git rev-parse HEAD 2>$null | Out-String).Trim()
    $words = @($root, $root.Replace('\', '/'), $commit) + @($origin -split '[/:]' | Select-Object -Last 2 | ForEach-Object { $_ -replace '\.git$', '' } | Select-Object -First 1) | Where-Object { $_ }
    $parts = @((Split-Path $root -Leaf), $env:USERNAME) | Where-Object { $_ }
    foreach ($name in @('Desktop', 'Cli')) { Assert-NoBuildIdentity (Join-Path $stage "CharacterPlanner.$name.exe") $words $parts }
    $zip = Join-Path $artifacts "character-planner-$version-windows-x64.zip"
    $stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        # The manifest is written after the member list, so it is added here in its ordinal place.
        $entries = [Collections.Generic.List[string]]::new($paths)
        $entries.Add('release-manifest.json')
        $entries.Sort([StringComparer]::Ordinal)
        foreach ($path in $entries) {
            $entry = $archive.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead((Join-Path $stage $path))
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ascii
    $extracted = Join-Path $artifacts "smoke-$run"
    $scratch += $extracted
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
    foreach ($member in $members) {
        if ((Get-FileHash (Join-Path $extracted $member.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $member.sha256) { throw "Archive hash mismatch: $($member.path)" }
    }
    # The smoke run extracts the apps' native DLLs into its own scratch folder, never the user's %TEMP%.
    $bundles = Join-Path $artifacts ('smoke-' + [guid]::NewGuid().ToString('N'))
    $scratch += $bundles
    $previousBundles = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundles
    Push-Location ([IO.Path]::GetTempPath())
    try {
        $reported = & (Join-Path $extracted 'CharacterPlanner.Cli.exe') --json version | ConvertFrom-Json
        if ($LASTEXITCODE -or !$reported.ok -or $reported.result.version -ne $version) { throw 'Packaged CLI failed from unrelated directory' }
        $packCheck = & (Join-Path $extracted 'CharacterPlanner.Cli.exe') pack validate (Join-Path $extracted 'packs/division-2') | ConvertFrom-Json
        # It validates cleanly, with its original artwork present and checked.
        $packNotes = @($packCheck.diagnostics | ForEach-Object { "$($_.severity):$($_.code)" })
        if ($LASTEXITCODE -or !$packCheck.ok -or $packNotes.Count -or $packCheck.result.artworkWithheld) { throw "Bundled producer pack failed validation: $($packNotes -join ', ')" }
        # The bundled pack must be exactly what the adopted delivery pins (the allowlist above already rejects any
        # other file). The pins themselves stay in the project (data/deliveries/division-2), not the release.
        $pins = (Get-Content -Raw (Join-Path $root 'data/deliveries/division-2/assembly.json') | ConvertFrom-Json).outputs
        foreach ($pin in @($pins.PSObject.Properties | Where-Object { $_.Name.StartsWith('pack/') })) {
            $file = Join-Path $extracted ('packs/division-2/' + $pin.Name.Substring(5))
            if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.Value.sha256) {
                throw "Bundled file does not match the delivery pin: $($pin.Name)"
            }
        }
        # The smoke window registers for live control in a scratch registry, never the user's own.
        $sessions = Join-Path $artifacts ('smoke-' + [guid]::NewGuid().ToString('N'))
        $scratch += $sessions
        $previousSessions = $env:CHARACTER_PLANNER_SESSION_DIR
        $env:CHARACTER_PLANNER_SESSION_DIR = $sessions
        try { $smoke = Start-Process (Join-Path $extracted 'CharacterPlanner.Desktop.exe') -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden -PassThru }
        finally { $env:CHARACTER_PLANNER_SESSION_DIR = $previousSessions }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 100
            $smoke.Refresh()
            if ($smoke.HasExited) { throw 'Packaged desktop exited before opening its window' }
        } while ($smoke.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 20)
        if ($smoke.MainWindowHandle -eq 0) { throw 'Packaged desktop did not open a native window' }
        # The packaged CLI must reach the packaged window over its live-session pipe.
        do {
            Start-Sleep -Milliseconds 200
            $live = & (Join-Path $extracted 'CharacterPlanner.Cli.exe') session list --session-dir $sessions | ConvertFrom-Json
        } while (@($live.result.sessions | Where-Object available).Count -eq 0 -and $timer.Elapsed.TotalSeconds -lt 40)
        $available = @($live.result.sessions | Where-Object available)
        if ($available.Count -ne 1 -or $available[0].processId -ne $smoke.Id) { throw 'The packaged CLI could not reach the packaged window as a live session' }
        Write-Host "Package verified: $zip"
    } finally { Pop-Location; $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundles }
} finally {
    if ($smoke -and !$smoke.HasExited) {
        $null = $smoke.CloseMainWindow()
        if (!$smoke.WaitForExit(5000)) { $smoke.Kill(); $smoke.WaitForExit() }
    }
    # Only the ZIP and its checksum are outputs; staging and smoke folders are scratch.
    foreach ($path in $scratch) {
        $absolute = [IO.Path]::GetFullPath($path)
        if (![IO.Path]::GetDirectoryName($absolute).Equals([IO.Path]::GetFullPath($artifacts), [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($absolute) -notmatch '^(stage|publish|smoke)-[0-9a-f]{32}$') {
            throw "Refusing to remove unexpected scratch path: $absolute"
        }
        if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
    }
    Pop-Location
}
