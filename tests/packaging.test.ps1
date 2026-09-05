$ErrorActionPreference = 'Stop'
$verifier = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging/verify-windows.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('rip-packaging-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

function Write-Checksums([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'SHA256SUMS' |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name } |
        Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS')
}

function Write-Fixture([string]$Name) {
    $directory = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'Rip-win-Setup.exe'), 'installer fixture')
    $package = Join-Path $directory 'Rip-1.0.0-full.nupkg'
    [IO.File]::WriteAllText($package, 'package fixture')
    $feed = @{ Assets = @(@{ PackageId = 'Rip'; Version = '1.0.0'; Type = 'Full';
        FileName = 'Rip-1.0.0-full.nupkg'; Size = (Get-Item $package).Length;
        SHA256 = (Get-FileHash $package -Algorithm SHA256).Hash }) }
    $feed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'releases.win.json')
    Write-Checksums $directory
    return $directory
}

function Assert-Rejected([string]$Directory, [string]$Reason) {
    $rejected = $false
    try { & $verifier -Directory $Directory -Version 1.0.0 | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw "Verifier accepted $Reason" }
}

try {
    $valid = Write-Fixture 'valid'
    & $verifier -Directory $valid -Version 1.0.0 | Out-Null
    $corrupt = Write-Fixture 'corrupt'
    Add-Content -LiteralPath (Join-Path $corrupt 'Rip-1.0.0-full.nupkg') -Value 'corruption'
    Assert-Rejected $corrupt 'a corrupt package'
    $extra = Write-Fixture 'extra'
    Set-Content -LiteralPath (Join-Path $extra 'unexpected.txt') -Value 'unlisted'
    Assert-Rejected $extra 'an unlisted release file'
    $wrongFeed = Write-Fixture 'wrong-feed'
    $feedPath = Join-Path $wrongFeed 'releases.win.json'
    $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    $feed.Assets[0].Size = 999
    $feed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $feedPath
    Write-Checksums $wrongFeed
    Assert-Rejected $wrongFeed 'a feed whose size disagrees with its package'
    $wrongVersion = Write-Fixture 'wrong-version'
    $feedPath = Join-Path $wrongVersion 'releases.win.json'
    $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    $feed.Assets[0].Version = '9.9.9'
    $feed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $feedPath
    Write-Checksums $wrongVersion
    Assert-Rejected $wrongVersion 'a feed for another version'
    Write-Output 'Passed 5 packaging verification cases.'
} finally {
    $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
    if ($resolved -ne [IO.Path]::GetFullPath($temporaryRoot) -or
        (Split-Path $resolved -Leaf) -notlike 'rip-packaging-test-*') { throw 'Unexpected test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
