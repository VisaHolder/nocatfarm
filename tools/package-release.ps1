<#
  package-release.ps1 - build a clean, shippable nocat.farm release zip.

  Produces dist/nocat.farm-v<version>.zip containing ONLY the app: exe, dlls, wwwroot, README.
  It publishes into an empty staging folder, so there is never any of YOUR data in it - config, login
  tokens, logs and authenticators are all created at runtime in whatever folder you run the app from, not
  at build time. A hard safety check aborts the whole thing if anything personal ever slips in.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\package-release.ps1
#>

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot          # the repo root (idle/)
$proj  = Join-Path $root 'src\NocatFarm'
$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'nocat.farm'

# --- version straight from the csproj, so the zip name always matches the build -------------------------
[xml]$csproj = Get-Content (Join-Path $proj 'NocatFarm.csproj')
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '0.0.0' }

Write-Host "Packaging nocat.farm v$version" -ForegroundColor Cyan

# --- fresh staging folder ------------------------------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# --- build ---------------------------------------------------------------------------------------------
# Self-contained win-x64: the release zip runs on a clean Windows box with no .NET install. (Building from
# source, per the README, stays framework-dependent - that path assumes you already have the SDK.)
dotnet publish $proj -c Release -o $stage -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- SAFETY: never ship personal data. Abort loudly if any is present. ---------------------------------
$bad = @()
$bad += Get-ChildItem $stage -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'config','logs','tokens','state','authenticators' }
$bad += Get-ChildItem $stage -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.token','.access' -or $_.Name -like 'netlog-*' }

if ($bad) {
    Write-Host 'ABORTING - personal / config data found in the build output:' -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "   $($_.FullName)" -ForegroundColor Red }
    throw 'refusing to package so nothing private is shipped'
}

# --- bundle the readme so the zip is self-explanatory --------------------------------------------------
Copy-Item (Join-Path $root 'README.md') $stage -Force

# --- zip -----------------------------------------------------------------------------------------------
$zip = Join-Path $dist "nocat.farm-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done  ->  $zip  (${size} MB)" -ForegroundColor Green
Write-Host 'Clean: no accounts, tokens, or logs included.' -ForegroundColor Green
