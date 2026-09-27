<#
.SYNOPSIS
  Publishes artifacts/releases (built by package.ps1) and the download page to
  https://jevdexer-releases.vercel.app, the update feed installed copies read.

.DESCRIPTION
  Copies site/ and the release files into artifacts/site, links it to the Vercel project
  jevdexer-releases (created on first run), and deploys to production. Feed files are served
  uncached; packages are immutable. Run package.ps1 first: it downloads the published release so
  the new one gets a delta update against it.

.EXAMPLE
  ./scripts/package.ps1 -Version 0.1.1
  ./scripts/publish-release.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$releases = Join-Path $root 'artifacts/releases'
$out = Join-Path $root 'artifacts/site'

if (-not (Test-Path (Join-Path $releases 'releases.win.json'))) { throw 'No release built. Run scripts/package.ps1 first.' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null
Copy-Item (Join-Path $root 'site/*') $out -Recurse
Copy-Item (Join-Path $root 'src/Jevdexer.App/Assets/Jevdexer-256.png') (Join-Path $out 'icon.png')
Copy-Item (Join-Path $releases '*') $out -Exclude '*.log'

Push-Location $out
try {
    vercel link --yes --project jevdexer-releases | Out-Null
    vercel deploy --prod --yes
    if ($LASTEXITCODE -ne 0) { throw 'Deploy failed.' }
}
finally { Pop-Location }
