<#
  package-release.ps1 - build clean, shippable nocat.farm release zips.

  Produces, in dist/:
    nocat.farm-v<version>-setup.exe         Windows installer (the recommended download) - needs Inno Setup 7
    nocat.farm-v<version>-portable.zip      Windows (win-x64) portable - also the one the in-app updater installs
    nocat.farm-v<version>_linux-x64.zip     Linux on 64-bit Intel/AMD
    nocat.farm-v<version>_linux-arm64.zip   Linux on 64-bit ARM (a Raspberry Pi 4/5 on a 64-bit OS, ARM servers)
  The Mac zips (_osx-arm64, _osx-x64) are NOT made here: a Mac only runs a signed app, and it's only signed when
  built on a Mac. After creating the release: gh workflow run macos.yml -f tag=v<version>

  Each contains ONLY the app: the program, its dlls, wwwroot, README. It publishes into empty staging folders,
  so there is never any of YOUR data in them - config, login tokens, logs and authenticators are all created at
  runtime in whatever folder you run the app from, not at build time. A hard safety check aborts the whole
  thing if anything personal ever slips in.

  Why "_linux" and not "-linux": GitHub lists a release's files alphabetically, and every copy up to 1.3.8
  updates itself with simply the FIRST .zip in that list. "-" sorts before the "." of ".zip", so a
  "-linux-..." zip would come first and every one of those copies would download the Linux build and refuse
  it. "_" sorts after, so the Windows zip stays first. (Newer copies pick the zip for their own platform.)
  The Windows one is "-portable.zip" for the same reason: "-portable" sorts before "_linux", so it stays first.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\package-release.ps1

  For the Linux CI (pwsh on Linux): only one Linux zip, exactly as the release makes it, optionally as another version -
    pwsh tools/package-release.ps1 -OnlyRid linux-x64 [-AsVersion 9.9.9]
  Nothing for Windows, no installer, no docs regenerated.
#>
param(
    [ValidateSet('', 'linux-x64', 'linux-arm64')][string]$OnlyRid = '',
    [string]$AsVersion = ''
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot          # the repo root
$proj  = Join-Path $root 'src/NocatFarm'
$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'nocat.farm'

# --- version straight from the csproj, so the zip name always matches the build -------------------------
[xml]$csproj = Get-Content (Join-Path $proj 'NocatFarm.csproj')
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '0.0.0' }
# Built as another version: the update tests need a "newer" one of this very code.
$versionArgs = @()
if ($AsVersion) { $version = $AsVersion; $versionArgs = @("-p:Version=$AsVersion") }

Write-Host "Packaging nocat.farm v$version" -ForegroundColor Cyan

# --- SAFETY: never ship personal data. Abort loudly if any is present. ---------------------------------
function Assert-Clean([string]$dir) {
    $bad = @()
    $bad += Get-ChildItem $dir -Recurse -Force -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in 'config','logs','tokens','state','authenticators' }
    # backups/ is the 'backup' command's folder, every saved login in it - next to the program, not in config/. And no zip
    # belongs in a package at all: one inside is a backup, a settings copy or an old release that slipped in.
    $bad += Get-ChildItem $dir -Force -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'backups' }
    $bad += Get-ChildItem $dir -Recurse -Force -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.token','.access','.key','.zip' -or $_.Name -like 'netlog-*' }

    if ($bad) {
        Write-Host 'ABORTING - personal / config data found in the build output:' -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "   $($_.FullName)" -ForegroundColor Red }
        throw 'refusing to package so nothing private is shipped'
    }
}

# --- zip -----------------------------------------------------------------------------------------------
# Every entry is named by hand. On Windows PowerShell 5.1 both Compress-Archive and ZipFile.CreateFromDirectory
# write "nocat.farm\wwwroot\app.js" with backslashes, which the zip format doesn't allow - Windows copes, but
# unzip warns and other tools can flatten the folders. Forward slashes.
#
# And FLAT - nocatFarm.exe at the top of the zip, not inside a nocat.farm/ folder. The in-app updater looks for
# the exe at the top, and every copy already installed runs that check, so a foldered zip is one nobody can update
# to. Unzipping by hand still gives a folder of its own (Windows names it after the zip), and updating by hand is
# unzipping over the install folder.
function New-FlatZip([string]$from, [string]$zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        Get-ChildItem $from -Recurse -File | ForEach-Object {
            $name = $_.FullName.Substring($from.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

# --- Linux file modes ------------------------------------------------------------------------------------
# A zip made on Windows says "made on MS-DOS" and carries no Unix permissions, so unzip on Linux extracts
# nocatFarm without its executable bit and it won't start until somebody runs chmod +x. There's no API for
# this in System.IO.Compression, so the central directory is patched after the fact: every entry is marked
# "made on Unix" (so unzip honours the mode), the program, createdump and the native .so libraries get 0755,
# everything else 0644. The file data isn't touched, only the two header fields.
function Set-UnixModes([string]$zip) {
    $bytes = [System.IO.File]::ReadAllBytes($zip)

    # End of central directory record: 22 bytes plus an optional comment, so look back from the end.
    $eocd = -1
    for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 22 - 65535); $i--) {
        if ([BitConverter]::ToUInt32($bytes, $i) -eq 0x06054b50) { $eocd = $i; break }
    }
    if ($eocd -lt 0) { throw "not a zip file: $zip" }

    $count  = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $offset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    if (($count -eq 0xFFFF) -or ($offset -eq 0xFFFFFFFF)) { throw "zip64 archives aren't handled: $zip" }

    $p = [int]$offset
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($bytes, $p) -ne 0x02014b50) { throw "damaged central directory in $zip" }

        $nameLen    = [BitConverter]::ToUInt16($bytes, $p + 28)
        $extraLen   = [BitConverter]::ToUInt16($bytes, $p + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $p + 32)
        $name = [System.Text.Encoding]::UTF8.GetString($bytes, $p + 46, $nameLen)

        $exec = ($name -ceq 'nocatFarm') -or ($name -ceq 'createdump') -or ($name -like '*.so') -or ($name -like '*.so.*')
        $mode = if ($exec) { 0x81ED } else { 0x81A4 }    # regular file, 0755 / 0644

        $bytes[$p + 5] = 3                                  # "version made by" host: 3 = Unix
        $attr = [BitConverter]::GetBytes([uint32]([int64]$mode * 65536))
        [Array]::Copy($attr, 0, $bytes, $p + 38, 4)          # external attributes: the mode in the high 16 bits

        $p += 46 + $nameLen + $extraLen + $commentLen
    }

    [System.IO.File]::WriteAllBytes($zip, $bytes)
}

# ==== Windows ============================================================================================
if (-not $OnlyRid) {

# --- fresh staging folder ------------------------------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# --- build ---------------------------------------------------------------------------------------------
# Self-contained win-x64: the release zip runs on a clean Windows box with no .NET install. (Building from
# source, per the README, stays framework-dependent - that path assumes you already have the SDK.)
# docs/COMMANDS.md is made from the command list - regenerate it so a release never ships with a stale one.
python (Join-Path $PSScriptRoot 'gen-commands.py')
if ($LASTEXITCODE -ne 0) { throw 'gen-commands.py failed' }

dotnet publish $proj -c Release -o $stage -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none --nologo -v q @versionArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Assert-Clean $stage

# --- bundle the readme so the zip is self-explanatory --------------------------------------------------
Copy-Item (Join-Path $root 'README.md') $stage -Force

$zip = Join-Path $dist "nocat.farm-v$version-portable.zip"
New-FlatZip $stage $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done  ->  $zip  (${size} MB)" -ForegroundColor Green

# --- the installer, from the very same files ----------------------------------------------------------------
# Inno Setup 7 builds it (tools/installer/nocatfarm.iss). The installed copy updates itself from the zip above,
# exactly like the portable one. Without Inno Setup on this machine the setup is skipped, loudly.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    python (Join-Path $PSScriptRoot 'installer/make-messages.py') | Out-Null
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$stage" (Join-Path $PSScriptRoot 'installer/nocatfarm.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed to build the installer' }
    $setup = Join-Path $dist "nocat.farm-v$version-setup.exe"
    $ssize = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "Done  ->  $setup  (${ssize} MB)" -ForegroundColor Green
} else {
    Write-Host 'Inno Setup 7 not found - the installer was NOT built (the zips still are).' -ForegroundColor Yellow
}

}   # end of Windows

# ==== Linux ==============================================================================================
# Self-contained as well, so it runs on a box with no .NET. The csproj makes these a plain console program
# (no WinExe) - on Linux it's the console and the web dashboard, no window. Start it with ./nocatFarm.
foreach ($rid in $(if ($OnlyRid) { @($OnlyRid) } else { @('linux-x64', 'linux-arm64') })) {
    $lstage = Join-Path $dist "nocat.farm-$rid"
    if (Test-Path $lstage) { Remove-Item $lstage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $lstage | Out-Null

    dotnet publish $proj -c Release -o $lstage -r $rid --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none --nologo -v q @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    Assert-Clean $lstage
    Copy-Item (Join-Path $root 'README.md') $lstage -Force

    $lzip = Join-Path $dist "nocat.farm-v${version}_$rid.zip"
    New-FlatZip $lstage $lzip
    Set-UnixModes $lzip

    $lsize = [math]::Round((Get-Item $lzip).Length / 1MB, 1)
    Write-Host "Done  ->  $lzip  (${lsize} MB)" -ForegroundColor Green
}

Write-Host 'Clean: no accounts, tokens, or logs included.' -ForegroundColor Green
Write-Host 'Upload the setup and all three zips to the release, then add the Mac zips: gh workflow run macos.yml -f tag=v<version>. The Windows zip stays first among the zips by name - see the note at the top.' -ForegroundColor DarkGray
