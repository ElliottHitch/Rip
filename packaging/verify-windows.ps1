param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $Directory).Path
foreach ($name in @('Rip-win-Setup.exe', 'releases.win.json', 'SHA256SUMS', "Rip-$Version-full.nupkg")) {
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
$feed = Get-Content -LiteralPath (Join-Path $packageRoot 'releases.win.json') -Raw | ConvertFrom-Json
$asset = @($feed.Assets | Where-Object { $_.PackageId -eq 'Rip' -and $_.Version -eq $Version -and $_.Type -eq 'Full' })
if ($asset.Count -ne 1 -or $asset[0].FileName -ne "Rip-$Version-full.nupkg") { throw 'Release feed has no unique matching Rip package' }
$package = Get-Item -LiteralPath (Join-Path $packageRoot $asset[0].FileName)
if ($package.Length -ne $asset[0].Size -or $verified[$package.Name] -ne $asset[0].SHA256) { throw 'Release feed does not match package bytes' }
Write-Output "Verified Rip $Version installer, update feed, and file checksums."
