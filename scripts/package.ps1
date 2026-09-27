<#
.SYNOPSIS
  Builds the Jevdexer installer: a Windows x64 build packed by Velopack.

.DESCRIPTION
  Output in artifacts/releases:
    Jevdexer.App-win-Setup.exe       per-user installer, no admin, Start menu shortcut
    Jevdexer.App-win-Portable.zip    runs without installing
    *.nupkg + releases.win.json      the update feed; scripts/publish-release.ps1 puts it online at
                                     https://jevdexer-releases.vercel.app (settings "updateUrl")
  Framework-dependent: Setup installs the .NET 10 desktop runtime first if the PC lacks it, so the
  installer stays small (a self-contained build is about 104 MB).

  Code signing turns on when one of these environment variables is set:
    JEVDEXER_SIGN_METADATA   path to Azure Trusted Signing metadata.json   -> --azureTrustedSignFile
    JEVDEXER_SIGN_PARAMS     signtool.exe parameters (a certificate)        -> --signParams
  Without either, the build is unsigned and SmartScreen warns on Setup.exe.

.EXAMPLE
  ./scripts/package.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'
$feed = 'https://jevdexer-releases.vercel.app'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw 'vpk not found. Install it with: dotnet tool install -g vpk'
}

Write-Host "Testing..." -ForegroundColor Cyan
dotnet test (Join-Path $root 'Jevdexer.slnx') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }

Write-Host "Publishing $Version..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src/Jevdexer.App') -nologo -c Release -r win-x64 --self-contained false `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host "Fetching the published release (for a delta update)..." -ForegroundColor Cyan
vpk download http --url $feed --outputDir $releases --allowEmptyChannel
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not reach $feed; packing without a delta." }

$sign = @()
if ($env:JEVDEXER_SIGN_METADATA) { $sign = @('--azureTrustedSignFile', $env:JEVDEXER_SIGN_METADATA); Write-Host 'Signing with Azure Trusted Signing.' }
elseif ($env:JEVDEXER_SIGN_PARAMS) { $sign = @('--signParams', $env:JEVDEXER_SIGN_PARAMS); Write-Host 'Signing with signtool.' }
else { Write-Warning 'Unsigned build: SmartScreen will warn on Setup.exe.' }

Write-Host "Packing..." -ForegroundColor Cyan
vpk pack --packId Jevdexer.App --packVersion $Version --packDir $publish --mainExe Jevdexer.exe `
    --packTitle Jevdexer --packAuthors Jevdexer --icon (Join-Path $root 'src/Jevdexer.App/Assets/Jevdexer.ico') `
    --shortcuts StartMenuRoot --framework net10-x64-desktop --outputDir $releases @sign
if ($LASTEXITCODE -ne 0) { throw 'Packing failed.' }

Get-ChildItem $releases | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
