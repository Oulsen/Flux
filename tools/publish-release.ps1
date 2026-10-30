<#
.SYNOPSIS
  Packs a Velopack release of Flux: Setup.exe and the update feed.

.DESCRIPTION
  The one recipe for an installer, run by hand to try a build on this PC and by
  .github/workflows/release.yml for a release, so the two cannot drift apart.

  Builds the web app first, because the .NET build only copies what Vite already
  wrote into wwwroot, then publishes a self-contained win-x64 build and packs it
  with the vpk version pinned in .config/dotnet-tools.json.

  Velopack installs per user into %LOCALAPPDATA%\Flux, the folder that already
  holds the log and the window's browser profile. An update replaces only its
  current\ folder. Settings are in %APPDATA%\Flux and are never touched; an
  appsettings.local.json beside the executable is, so it is for development only.

.PARAMETER Version
  The release version. Defaults to <Version> in Directory.Build.props, the only
  place the version lives.

.PARAMETER OutputDir
  Where Setup.exe and the feed land. Defaults to releases\ (gitignored). To get
  delta packages, put the previous release's full package here first:
  vpk download github does that in the release workflow.
#>
[CmdletBinding()]
param(
  [string]$Version,
  [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
  $props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
  $Version = "$(($props.Project.PropertyGroup | Where-Object { $_.Version }).Version)".Trim()
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'No version: pass -Version or set <Version> in Directory.Build.props.' }

$publishDir = Join-Path $repoRoot 'artifacts/publish'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'releases' }

Write-Host 'Building the web app...' -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot 'src/Flux.Web.Frontend')
try {
  npm run build
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed ($LASTEXITCODE)" }
} finally {
  Pop-Location
}
if (-not (Test-Path (Join-Path $repoRoot 'src/Flux.Web.Server/wwwroot/index.html'))) {
  throw 'The web app did not reach src/Flux.Web.Server/wwwroot: the installer would show a blank page.'
}

Write-Host 'Publishing self-contained win-x64...' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# A restore for win-x64 adds a runtime section to every lock file it touches, while
# CI checks the lock files without one. Put back byte for byte afterwards, also when
# the publish fails, so packing never leaves a change to commit.
$locks = Get-ChildItem $repoRoot -Recurse -Filter packages.lock.json |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
$snapshots = @{}
foreach ($lock in $locks) { $snapshots[$lock.FullName] = [System.IO.File]::ReadAllBytes($lock.FullName) }
try {
  & dotnet publish (Join-Path $repoRoot 'src/Flux.App/Flux.App.csproj') `
      -c Release -r win-x64 --self-contained `
      -o $publishDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
} finally {
  foreach ($path in $snapshots.Keys) { [System.IO.File]::WriteAllBytes($path, $snapshots[$path]) }
}

Write-Host 'Restoring the pinned vpk...' -ForegroundColor Cyan
& dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }

Write-Host "Packing Flux $Version..." -ForegroundColor Cyan
& dotnet vpk pack `
    --packId Flux `
    --packTitle Flux `
    --packAuthors Oulsen `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Flux.exe `
    --icon (Join-Path $repoRoot 'assets/branding/flux.ico') `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host "`nFlux $Version is in $OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir | Select-Object Name, Length | Format-Table -AutoSize
Write-Host 'To install it here, run the Setup.exe above. A development build can stay open; an installed Flux has to be quit from the tray first.'
