param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipEntrySnapshot([string]$Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($archive.Entries | ForEach-Object {
            [pscustomobject]@{ Name = $_.FullName; Length = $_.Length }
        })
    } finally {
        $archive.Dispose()
    }
}

function Assert-PackageContents([string]$Path, [string]$RequiredNotice) {
    $entries = @(Get-ZipEntrySnapshot $Path)
    $notice = @($entries | Where-Object Name -eq $RequiredNotice)
    if ($notice.Count -ne 1 -or $notice[0].Length -eq 0) {
        throw "Package is missing the required third-party notice: $RequiredNotice"
    }
    $unsafe = @($entries | Where-Object {
        $_.Name -match '(^|/)(\.env($|\.)|[^/]+\.(pdb|pfx|p12|pem|key|snk))$'
    })
    if ($unsafe.Count) { throw "Sensitive or debug file in package: $($unsafe[0].Name)" }
}

$packageRoot = (Resolve-Path -LiteralPath $Directory).Path
$requiredNames = @(
    'Rip-win-Setup.exe',
    'Rip-win-Portable.zip',
    'assets.win.json',
    'RELEASES',
    'releases.win.json',
    'SHA256SUMS',
    "Rip-$Version-full.nupkg"
)
foreach ($name in $requiredNames) {
    $file = Get-Item -LiteralPath (Join-Path $packageRoot $name)
    if ($file.PSIsContainer -or $file.Length -eq 0) { throw "Missing or empty release file: $name" }
}

$verified = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS')) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  ([a-zA-Z0-9._-]+)$') { throw 'Invalid checksum entry' }
    $expected = $Matches[1]
    $name = $Matches[2]
    if ($verified.ContainsKey($name)) { throw "Duplicate checksum entry: $name" }
    $actual = (Get-FileHash -LiteralPath (Join-Path $packageRoot $name) -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Checksum mismatch: $name" }
    $verified[$name] = $actual
}
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File) {
    if ($file.Name -ne 'SHA256SUMS' -and !$verified.ContainsKey($file.Name)) { throw "Unlisted release file: $($file.Name)" }
}
foreach ($name in $requiredNames | Where-Object { $_ -ne 'SHA256SUMS' }) {
    if (!$verified.ContainsKey($name)) { throw "Required release file has no checksum: $name" }
}

$feed = Get-Content -LiteralPath (Join-Path $packageRoot 'releases.win.json') -Raw | ConvertFrom-Json
$asset = @($feed.Assets | Where-Object { $_.PackageId -eq 'Rip' -and $_.Version -eq $Version -and $_.Type -eq 'Full' })
if ($asset.Count -ne 1 -or $asset[0].FileName -ne "Rip-$Version-full.nupkg") { throw 'Release feed has no unique matching Rip package' }
$package = Get-Item -LiteralPath (Join-Path $packageRoot $asset[0].FileName)
if ($package.Length -ne $asset[0].Size -or $verified[$package.Name] -ne $asset[0].SHA256) { throw 'Release feed does not match package bytes' }

$assetManifest = @(Get-Content -LiteralPath (Join-Path $packageRoot 'assets.win.json') -Raw | ConvertFrom-Json)
$expectedAssets = @{
    'Rip-win-Portable.zip' = 'Portable'
    'Rip-win-Setup.exe' = 'Installer'
    "Rip-$Version-full.nupkg" = 'Full'
}
if ($assetManifest.Count -ne $expectedAssets.Count) { throw 'Asset manifest contains unexpected entries' }
foreach ($entry in $expectedAssets.GetEnumerator()) {
    $matching = @($assetManifest | Where-Object {
        $_.RelativeFileName -eq $entry.Key -and $_.Type -eq $entry.Value
    })
    if ($matching.Count -ne 1) { throw "Asset manifest is missing or duplicates: $($entry.Key)" }
}

$legacyRelease = (Get-Content -LiteralPath (Join-Path $packageRoot 'RELEASES') -Raw).Trim()
$legacyPattern = '^[a-fA-F0-9]{40} Rip-' + [regex]::Escape($Version) + '-full\.nupkg \d+$'
if ($legacyRelease -notmatch $legacyPattern) { throw 'Legacy release metadata does not match the full package' }

Assert-PackageContents (Join-Path $packageRoot 'Rip-win-Portable.zip') 'current/THIRD-PARTY-NOTICES.md'
Assert-PackageContents $package.FullName 'lib/app/THIRD-PARTY-NOTICES.md'
Write-Output "Verified Rip $Version release files, manifests, embedded notices, and checksums."
