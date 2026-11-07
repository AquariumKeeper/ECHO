#Requires -PSEdition Core
param([string]$Output = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'examples/artwork/minimal-slots'
if (!$Output) { $Output = Join-Path $root 'artifacts/minimal-slots-artwork.zip' }
$target = [IO.Path]::GetFullPath($Output)
if ($target.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output must be outside the example source directory'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
# This builds only the committed, fixed example. It is not a general artwork exporter or installer.
$members = @('artwork.json', 'assets/placeholder.png', 'licenses/LICENSE.txt')
$manifest = Get-Content -Raw -LiteralPath (Join-Path $source 'artwork.json') | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $source 'assets/placeholder.png') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.assets[0].sha256) {
    throw 'The example image does not match its manifest'
}
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($member in $members) {
            $entry = $zip.CreateEntry($member, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $destination = $entry.Open()
            try {
                $inputFile = [IO.File]::OpenRead((Join-Path $source $member))
                try { $inputFile.CopyTo($destination) } finally { $inputFile.Dispose() }
            } finally { $destination.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$digest = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Example artwork ZIP: $target"
Write-Output "SHA-256: $digest"
