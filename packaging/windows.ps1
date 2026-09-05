param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Output = 'artifacts/windows',
    [string]$UpdateRepository = 'https://github.com/ElliottHitch/Rip'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $root $Output) $Version))
$publishPath = Join-Path $outputPath 'publish'
$releases = Join-Path $outputPath 'releases'
Push-Location $root
try {
    dotnet restore src/Rip.App/Rip.App.csproj --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    dotnet publish src/Rip.App/Rip.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -p:UpdateRepository=$UpdateRepository -o $publishPath
    if ($LASTEXITCODE) { throw 'Publish failed' }
    $smoke = Start-Process -FilePath (Join-Path $publishPath 'Rip.exe') -ArgumentList '--deterministic-smoke' -Wait -PassThru -WindowStyle Hidden
    if ($smoke.ExitCode) { throw 'Packaged application smoke check failed' }
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Velopack tool restore failed' }
    dotnet tool run vpk pack --packId Rip --packTitle Rip --packAuthors ElliottHitch --packVersion $Version --packDir $publishPath --mainExe Rip.exe --icon src/Rip.App/Assets/Rip.ico --shortcuts Desktop,StartMenuRoot --channel win --delta None --outputDir $releases
    if ($LASTEXITCODE) { throw 'Installer packaging failed' }
    Get-ChildItem -LiteralPath $releases -File | Where-Object Name -ne 'SHA256SUMS' |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
        Set-Content -LiteralPath (Join-Path $releases 'SHA256SUMS') -Encoding ascii
    & (Join-Path $PSScriptRoot 'verify-windows.ps1') -Directory $releases -Version $Version
} finally { Pop-Location }
