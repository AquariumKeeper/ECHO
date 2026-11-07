#Requires -PSEdition Core
param(
    [Parameter(Mandatory)][string]$SourcePack,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
# A bounded migration of the historical ShadeTech collection, not a general game-pack converter.
# The presentation pin authenticates every mapping, source notice and individual image hash.
$presentationPin = '991df35e5b67bd75a2c6a03cc5ed68e65f3c69a05951c075f594ca2750a6d418'
$root = [IO.Path]::GetFullPath($SourcePack)
if (!$Output) { $Output = Join-Path (Split-Path $PSScriptRoot) 'artifacts/shadetech-legacy-artwork-v1.zip' }
$target = [IO.Path]::GetFullPath($Output)
if ($target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output must be outside the source pack'
}
function Read-Source([string]$Relative) {
    if ($Relative -notmatch '^[a-zA-Z0-9_./-]+$' -or $Relative.Split('/') -contains '..' -or [IO.Path]::IsPathRooted($Relative)) {
        throw "Unsafe source member: $Relative"
    }
    $current = $root
    if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source directory' }
    foreach ($part in $Relative.Split('/')) {
        $current = Join-Path $current $part
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source member: $Relative" }
    }
    return ,([IO.File]::ReadAllBytes($current))
}
function Hash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
$raw = Read-Source 'presentation.json'
if ((Hash $raw) -cne $presentationPin) { throw 'Historical presentation pin mismatch' }
$presentation = [Text.Encoding]::UTF8.GetString($raw) | ConvertFrom-Json -AsHashtable
$members = [Collections.Generic.SortedDictionary[string,byte[]]]::new([StringComparer]::Ordinal)
$assets = @($presentation.artwork.assets | ForEach-Object {
    $bytes = Read-Source $_.path
    if ((Hash $bytes) -cne $_.sha256) { throw "Image pin mismatch: $($_.path)" }
    $path = 'assets/' + $_.id + '.png'
    $members.Add($path, $bytes)
    [ordered]@{ id = $_.id; path = $path; sha256 = $_.sha256 }
})
$credits = @($presentation.artwork.sources | ForEach-Object {
    $path = 'licenses/' + $_.id + '.txt'
    # Preserve the recorded license claim, attribution, origin URL and preparation notes verbatim.
    $license = if ($_.license) { $_.license } else { 'No license recorded' }
    $notice = "Historical source record (missing properties remain absent):`n$($_ | ConvertTo-Json -Depth 10)`n"
    $members.Add($path, [Text.Encoding]::UTF8.GetBytes($notice))
    [ordered]@{ author = $_.attribution; license = $license; licenseFile = $path }
})
$manifest = [ordered]@{
    schemaVersion = 1; id = 'shadetech.legacy-artwork'; name = 'ShadeTech historical artwork'
    version = '1'; author = 'ShadeTech collection; see individual source notices'; gamePackId = 'division_2'
    description = 'Historical 555-image collection, converted without changing pixels or game data. Source notices retain their original license claims.'
    credits = $credits; assets = $assets
    bindings = [ordered]@{ icons = $presentation.icons; itemArt = $presentation.itemArt; specializationImages = $presentation.specializationImages }
}
$members.Add('artwork.json', [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 30) + "`n"))
# Capture and verify every input before creating the output; refuse replacement of an existing ZIP.
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($member in $members.GetEnumerator()) {
            $entry = $zip.CreateEntry($member.Key, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $destination = $entry.Open()
            try { $destination.Write($member.Value) } finally { $destination.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output "Artwork ZIP: $target"
Write-Output "Images: $($assets.Count); source notices: $($credits.Count)"
Write-Output "SHA-256: $((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant())"
