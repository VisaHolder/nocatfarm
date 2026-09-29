# Updating itself against releases served from this machine (NOCATFARM_UPDATE_FEED): a download that's gone and a
# damaged one change nothing, a new version that crashes is put back, a good one goes in and stays.
#   -Mode portable | installed   (installed needs dist\nocat.farm-v<version>-setup.exe)
param([string]$Mode = 'portable')   # portable | installed
# The repo, the version being released (from the csproj), and a working folder outside the repo for the test copies.
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$SP = Join-Path $env:TEMP 'nocatfarm-tests'
New-Item -ItemType Directory -Force $SP | Out-Null
#   broken download link -> nothing changed; damaged zip -> nothing changed; a crashing new version -> put back; a good one -> in.
$ErrorActionPreference = 'Stop'
$W = Join-Path $SP 'winupd'
$port = 7298; $feedPort = 8766
function Check($name, $ok, $detail = '') { "{0}  [{1}] {2}{3}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $Mode, $name, $(if ($detail) { "  ($detail)" } else { '' }) }

Remove-Item $W -Recurse -Force -ErrorAction SilentlyContinue   # this build, every time
if ($true) {
  New-Item -ItemType Directory -Force $W | Out-Null
  & dotnet publish "$Repo\src\NocatFarm" -c Release -o "$W\base" -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none --nologo -v q
  & dotnet publish "$Repo\src\NocatFarm" -c Release -o "$W\good" -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -p:Version=9.9.9 --nologo -v q
  Copy-Item "$W\good" "$W\broken" -Recurse
  Copy-Item "$env:WINDIR\System32\hostname.exe" "$W\broken\nocatFarm.exe" -Force   # starts and exits at once
  Compress-Archive "$W\good\*" "$W\good.zip"; Compress-Archive "$W\broken\*" "$W\broken.zip"
  Set-Content "$W\damaged.zip" 'this is not a zip' -Encoding ascii
}
function Feed($name, $tag, $zip) {
  $size = if (Test-Path "$W\$zip") { (Get-Item "$W\$zip").Length } else { 12345 }
  Set-Content "$W\$name.json" -Encoding ascii -Value ('{"tag_name":"' + $tag + '","body":"- a test release","assets":[{"name":"nocat.farm-' + $tag + '-portable.zip","browser_download_url":"http://127.0.0.1:' + $feedPort + '/' + $zip + '","size":' + $size + '}]}')
}
Feed 'broken' 'v9.9.8' 'broken.zip'; Feed 'good' 'v9.9.9' 'good.zip'; Feed 'nolink' 'v9.9.7' 'missing.zip'; Feed 'damaged' 'v9.9.6' 'damaged.zip'
$srv = Start-Process python -ArgumentList '-m', 'http.server', $feedPort, '--bind', '127.0.0.1' -WorkingDirectory $W -WindowStyle Hidden -PassThru
Start-Sleep 2

# the copy under test
$here = Join-Path $SP "winupd-$Mode"
if (Test-Path $here) { Remove-Item $here -Recurse -Force }
if ($Mode -eq 'installed') {
  $p = Start-Process "$Repo\dist\nocat.farm-v$Version-setup.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$here`"", '/MOVE=no', '/FROM=fresh', '/STARTUP=no', '/DESKTOP=no', '/HIDDEN=yes', "/PORT=$port" -PassThru -Wait
  Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" } | Stop-Process -Force
  Copy-Item "$W\base\*" $here -Recurse -Force   # this build's updater, in the installed folder
} else {
  Copy-Item "$W\base" $here -Recurse
}
New-Item -ItemType Directory -Force "$here\config" | Out-Null
Set-Content "$here\config\nocatFarm.json" -Encoding utf8 -Value ('{"WebPort":' + $port + ',"CheckForUpdates":true,"JoinGroup":false,"StartWithWindows":false,"TrayNotifications":false,"OpenBrowserOnStart":false,"TutorialDone":true}')
$api = "http://127.0.0.1:$port"
function Ver { try { (Invoke-RestMethod "$api/api/status" -TimeoutSec 3).Version } catch { '' } }
function Cmd($l) { (Invoke-RestMethod "$api/api/command" -Method Post -ContentType 'application/json' -Body (@{ Line = $l } | ConvertTo-Json)).output }
function Start-It($feed) {
  $env:NOCATFARM_UPDATE_FEED = "http://127.0.0.1:$feedPort/$feed.json"
  Start-Process "$here\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $here -WindowStyle Hidden | Out-Null
  Remove-Item Env:NOCATFARM_UPDATE_FEED
  for ($i = 0; $i -lt 40; $i++) { if (Ver) { return }; Start-Sleep 1 }
}
function Stop-It { try { Start-Process "$here\nocatFarm.exe" -ArgumentList '--quit' -WorkingDirectory $here -WindowStyle Hidden -Wait } catch {}; Start-Sleep 2
  Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" } | Stop-Process -Force }
function LogHas($pattern) { (Get-ChildItem "$here\logs" -Filter *.log | Get-Content | Select-String $pattern) -ne $null }

$base = $null
Start-It 'nolink'; $base = Ver
$o = Cmd 'update now'; Start-Sleep 4
Check 'a download link that is gone: nothing changed' (((Ver) -eq $base) -and (LogHas 'update failed')) "$base, $o"
Stop-It

Start-It 'damaged'
$o = Cmd 'update now'; Start-Sleep 5
Check 'a damaged zip: nothing changed' (((Ver) -eq $base) -and (LogHas "couldn't unpack")) $o
Stop-It

Start-It 'broken'
$t0 = Get-Date
$null = Cmd 'update now'
$back = $false
while (((Get-Date) - $t0).TotalSeconds -lt 150) { Start-Sleep 3; if (((Ver) -eq $base) -and (LogHas 'update undone')) { $back = $true; break } }
Check 'a new version that crashes is put back' $back "$([int]((Get-Date) - $t0).TotalSeconds)s, now $(Ver)"
Check '...and the put-back copy is the real one, with the reason said' ((LogHas 'update undone: 9.9.8') -and ((Get-Item "$here\nocatFarm.exe").Length -gt 100000))
Stop-It

Start-It 'good'
$t0 = Get-Date
$null = Cmd 'update now'
while (((Get-Date) - $t0).TotalSeconds -lt 120) { Start-Sleep 3; if ((Ver) -eq '9.9.9') { break } }
Check 'a good new version goes in' ((Ver) -eq '9.9.9') "$([int]((Get-Date) - $t0).TotalSeconds)s"
$cfg = Get-Content "$here\config\nocatFarm.json" -Raw | ConvertFrom-Json
Check '...with the settings kept' ($cfg.WebPort -eq $port)
Start-Sleep 35   # past the half minute it waits before saying "ok"
Check '...and it stays (confirmed, not put back)' ((Ver) -eq '9.9.9')
Stop-It

if ($Mode -eq 'installed') {
  Start-Process "$here\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction SilentlyContinue
}
Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
