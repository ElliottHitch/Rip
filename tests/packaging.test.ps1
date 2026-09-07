$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$verifier = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging/verify-windows.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('rip-packaging-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

function Write-Checksums([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'SHA256SUMS' |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name } |
        Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS')
}

function Write-ZipFixture([string]$Path, [hashtable]$Entries) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Entries.GetEnumerator()) {
            $entry = $archive.CreateEntry([string]$item.Key)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write([string]$item.Value) }
            finally { $writer.Dispose() }
        }
    } finally {
        $archive.Dispose()
    }
}

function Write-Fixture {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [switch]$OmitPortableNotice,
        [switch]$OmitPackageNotice,
        [switch]$AddDebugSymbol
    )
    $directory = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'Rip-win-Setup.exe'), 'installer fixture')

    $portableEntries = @{ 'current/Rip.exe' = 'portable app fixture' }
    if (-not $OmitPortableNotice) { $portableEntries['current/THIRD-PARTY-NOTICES.md'] = 'notice fixture' }
    if ($AddDebugSymbol) { $portableEntries['current/Rip.pdb'] = 'debug fixture' }
    Write-ZipFixture (Join-Path $directory 'Rip-win-Portable.zip') $portableEntries

    $packageName = 'Rip-1.0.0-full.nupkg'
    $package = Join-Path $directory $packageName
    $packageEntries = @{ 'lib/app/Rip.exe' = 'package app fixture' }
    if (-not $OmitPackageNotice) { $packageEntries['lib/app/THIRD-PARTY-NOTICES.md'] = 'notice fixture' }
    Write-ZipFixture $package $packageEntries

    @(
        [ordered]@{ RelativeFileName = 'Rip-win-Portable.zip'; Type = 'Portable' },
        [ordered]@{ RelativeFileName = 'Rip-win-Setup.exe'; Type = 'Installer' },
        [ordered]@{ RelativeFileName = $packageName; Type = 'Full' }
    ) | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $directory 'assets.win.json')

    $sha1 = (Get-FileHash $package -Algorithm SHA1).Hash
    '{0} {1} {2}' -f $sha1, $packageName, (Get-Item $package).Length |
        Set-Content -LiteralPath (Join-Path $directory 'RELEASES')

    $feed = @{ Assets = @(@{ PackageId = 'Rip'; Version = '1.0.0'; Type = 'Full';
        FileName = $packageName; Size = (Get-Item $package).Length;
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

    $missingPortableNotice = Write-Fixture 'missing-portable-notice' -OmitPortableNotice
    Assert-Rejected $missingPortableNotice 'a portable package without the third-party notice'

    $missingPackageNotice = Write-Fixture 'missing-package-notice' -OmitPackageNotice
    Assert-Rejected $missingPackageNotice 'an update package without the third-party notice'

    $debugSymbol = Write-Fixture 'debug-symbol' -AddDebugSymbol
    Assert-Rejected $debugSymbol 'a package containing a debug-symbol file'

    $missingPortable = Write-Fixture 'missing-portable'
    Remove-Item -LiteralPath (Join-Path $missingPortable 'Rip-win-Portable.zip')
    Write-Checksums $missingPortable
    Assert-Rejected $missingPortable 'a release without its portable package'

    Write-Output 'Passed 9 packaging verification cases.'
} finally {
    $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
    if ($resolved -ne [IO.Path]::GetFullPath($temporaryRoot) -or
        (Split-Path $resolved -Leaf) -notlike 'rip-packaging-test-*') { throw 'Unexpected test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
