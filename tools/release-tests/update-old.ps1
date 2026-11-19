# 2.0.1: a copy from before 2.0.1 (nocatFarm.exe, nocatFarm.json, nocatfarm.rep4rep.dll) updates itself to this build with
# its own updater, from releases served on this machine (NOCATFARM_UPDATE_FEED - the only name it reads), listed the way
# GitHub lists them: the zip for updaters from before 2.0.1 first. It must come back as this build, running as
# gamerFarm.exe, with its settings and add-on - and nothing under the old names left, but the one thing kept on purpose.
#   -From 2.0.0 | 1.9.3   the release it starts as: its portable zip (and setup) from dist\, else from GitHub
#   -Mode portable | installed   installed: its own setup puts it in, and its own uninstaller takes it out again at the end
#   -Port, -FeedPort and -TestRoot move it off its usual ports and folder, to run beside another test.
param([Parameter(Mandatory)][string]$From, [string]$Mode = 'portable', [int]$Port = 7293, [int]$FeedPort = 8768, [string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$SP = if ($TestRoot) { $TestRoot } else { Join-Path $env:TEMP 'gamerfarm-tests' }
New-Item -ItemType Directory -Force $SP | Out-Null
$W = Join-Path $SP "oldupd-$Mode"
$here = Join-Path $SP "oldupd-$Mode-app"
$New = '9.9.9'
function Check($name, $ok, $detail = '') { "{0}  [{1} from {2}] {3}{4}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $Mode, $From, $name, $(if ($detail) { "  ($detail)" } else { '' }) }

foreach ($d in $W, $here) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force $W | Out-Null

# --- the release it starts as, and the add-on as it was built for it --------------------------------------------------
$old = @("$Repo\dist\gamer.farm-v$From-portable.zip", "$Repo\dist\nocat.farm-v$From-portable.zip") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $old) {
  gh release download "v$From" --repo VisaHolder/gamerfarm --pattern '*-portable.zip' --dir "$W\old" | Out-Null
  $old = Get-ChildItem "$W\old" -Filter '*-portable.zip' | Select-Object -First 1 -ExpandProperty FullName
}
$setup = $null
if ($Mode -eq 'installed') {
  $setup = @("$Repo\dist\gamer.farm-v$From-setup.exe", "$Repo\dist\nocat.farm-v$From-setup.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $setup) {
    gh release download "v$From" --repo VisaHolder/gamerfarm --pattern '*-setup.exe' --dir "$W\old" | Out-Null
    $setup = Get-ChildItem "$W\old" -Filter '*-setup.exe' | Select-Object -First 1 -ExpandProperty FullName
  }
}
gh release download v2.0.0 --repo VisaHolder/gamerfarm --pattern 'nocatfarm.rep4rep.dll' --dir "$W\addon" | Out-Null
$addon = Join-Path "$W\addon" 'nocatfarm.rep4rep.dll'

# --- this build as 9.9.9: the plain zip, and the one for updaters from before 2.0.1 --------------------------------------
& dotnet publish "$Repo\src\GamerFarm" -c Release -o "$W\new" -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none "-p:Version=$New" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Compress-Archive "$W\new\*" "$W\plain.zip"
Copy-Item "$W\new" "$W\newold" -Recurse
Copy-Item "$W\newold\gamerFarm.exe" "$W\newold\nocatFarm.exe"
Compress-Archive "$W\newold\*" "$W\forold.zip"
$assets = @(
  @{ name = "gamer.farm-old-updater-v$New-portable.zip"; browser_download_url = "http://127.0.0.1:$FeedPort/forold.zip"; size = (Get-Item "$W\forold.zip").Length },
  @{ name = "gamer.farm-v$New-portable.zip"; browser_download_url = "http://127.0.0.1:$FeedPort/plain.zip"; size = (Get-Item "$W\plain.zip").Length },
  @{ name = "gamer.farm-v$New-setup.exe"; browser_download_url = "http://127.0.0.1:$FeedPort/none.exe"; size = 1 },
  @{ name = "gamerfarm.rep4rep.dll"; browser_download_url = "http://127.0.0.1:$FeedPort/none.dll"; size = 1 }
) | Sort-Object { $_.name } -Culture ''   # by name, ordinal, the way GitHub lists them
$feedJson = @{ tag_name = "v$New"; body = '- this build'; assets = @($assets) } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText("$W\latest.json", $feedJson)
Check 'the feed lists the zip for updaters from before 2.0.1 first, as GitHub would' ($assets[0].name -like 'gamer.farm-old-updater-*')
$srv = Start-Process python -ArgumentList '-m', 'http.server', $FeedPort, '--bind', '127.0.0.1' -WorkingDirectory $W -WindowStyle Hidden -PassThru
Start-Sleep 2

# --- the copy from before 2.0.1 -----------------------------------------------------------------------------------------
if ($Mode -eq 'installed') {
  Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$here`"", '/MOVE=no', '/FROM=fresh', '/STARTUP=no', '/DESKTOP=no', '/HIDDEN=yes', "/PORT=$Port" -Wait | Out-Null
  Get-Process nocatFarm, gamerFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" } | Stop-Process -Force
  Start-Sleep 2
} else {
  Expand-Archive $old $here
}
$exe = Join-Path $here 'nocatFarm.exe'
Check "it's a copy from before 2.0.1: nocatFarm.exe" (Test-Path $exe)
New-Item -ItemType Directory -Force "$here\config", "$here\plugins" | Out-Null
Remove-Item "$here\config\nocatFarm.json" -ErrorAction SilentlyContinue
[IO.File]::WriteAllText("$here\config\nocatFarm.json", ('{"WebPort":' + $Port + ',"CheckForUpdates":true,"JoinGroup":false,"StartWithWindows":false,"TrayNotifications":false,"OpenBrowserOnStart":false,"TutorialDone":true,"CountMeAsUser":false,"PluginsEnabled":true,"StatusEveryMinutes":7}'))
Copy-Item $addon "$here\plugins\nocatfarm.rep4rep.dll"

$api = "http://127.0.0.1:$Port"
function Ver { try { (Invoke-RestMethod "$api/api/status" -TimeoutSec 3).Version } catch { '' } }
function Cmd($l) { try { (Invoke-RestMethod "$api/api/command" -Method Post -ContentType 'application/json' -Body (@{ Line = $l } | ConvertTo-Json) -TimeoutSec 30).output } catch { "(no answer: $($_.Exception.Message))" } }
$env:NOCATFARM_UPDATE_FEED = "http://127.0.0.1:$FeedPort/latest.json"
$env:NOCATFARM_PING_URL = 'http://127.0.0.1:9/api/farm/ping'
Start-Process $exe -ArgumentList '--minimized' -WorkingDirectory $here -WindowStyle Hidden | Out-Null
Remove-Item Env:NOCATFARM_UPDATE_FEED
for ($i = 0; ($i -lt 60) -and -not (Ver); $i++) { Start-Sleep 1 }
$was = Ver
Check "it started as $From" ($was -eq $From) $was
for ($i = 0; ($i -lt 30) -and -not ((Cmd 'plugins') -match 'rep4rep'); $i++) { Start-Sleep 1 }   # plugins load a moment after the dashboard
# The add-on as the list hands it out is the 2.0.0 build: a copy older than 2.0.0 may not load it (that's not 2.0.1's doing).
if ([version]$From -ge [version]'2.0.0') { Check '...with the add-on built for it loaded' ((Cmd 'plugins') -match 'rep4rep') }
else { "INFO  [$Mode from $From] the add-on loaded before the update: $((Cmd 'plugins') -match 'rep4rep')" }

# --- its own updater puts this build in ------------------------------------------------------------------------------
$o = Cmd 'update accept'
"update accept -> $o"
$t0 = Get-Date; $now = $was
while (((Get-Date) - $t0).TotalSeconds -lt 300) { Start-Sleep 3; $now = Ver; if ($now -eq $New) { break } }
Check "it came back as this build" ($now -eq $New) "after $([int]((Get-Date) - $t0).TotalSeconds)s: $now"
Start-Sleep 45   # past the half minute before it says it came up fine - then it moves in
Check '...and stayed (not put back)' ((Ver) -eq $New) (Ver)

$running = @(Get-Process gamerFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $here 'gamerFarm.exe') })
$oldRunning = @(Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" })
Check 'it runs as gamerFarm.exe, and nothing as nocatFarm.exe' (($running.Count -eq 1) -and ($oldRunning.Count -eq 0)) "$($running.Count) / $($oldRunning.Count)"
$settings = Get-Content "$here\config\gamerFarm.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
Check 'the settings moved over under their new name, every one kept' (($settings.WebPort -eq $Port) -and ($settings.StatusEveryMinutes -eq 7) -and -not (Test-Path "$here\config\nocatFarm.json"))
Check 'the add-on took its new name and still loads' ((Test-Path "$here\plugins\gamerfarm.rep4rep.dll") -and -not (Test-Path "$here\plugins\nocatfarm.rep4rep.dll") -and ((Cmd 'plugins') -match 'rep4rep') -and -not ((Cmd 'plugins') -match 'nocat')) (Cmd 'plugins')
$left = @(Get-ChildItem $here -Recurse -Force | Where-Object { $_.Name -like '*nocat*' } | ForEach-Object { $_.FullName.Substring($here.Length + 1) })
if ($Mode -eq 'installed') {
  # The one kept on purpose: the starter for the uninstaller from before 2.0.1, hidden.
  $relay = Get-Item "$here\nocatFarm.exe" -Force -ErrorAction SilentlyContinue
  Check 'installed: only the hidden starter for the old uninstaller is left under the old name' (($left.Count -eq 1) -and $relay -and (($relay.Attributes -band [IO.FileAttributes]::Hidden) -ne 0)) ($left -join ', ')
  $entry = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}_is1'
  Check "installed: Windows' list shows gamer.farm $New, with the new program's icon" (($entry.DisplayName -eq 'gamer.farm') -and ($entry.DisplayVersion -eq $New) -and ($entry.DisplayIcon -like '*gamerFarm.exe')) "$($entry.DisplayName) $($entry.DisplayVersion) $($entry.DisplayIcon)"
  $sh = New-Object -ComObject WScript.Shell
  $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'gamer.farm.lnk'
  $target = if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '' }
  Check 'installed: the Start menu shortcut starts gamerFarm.exe' ($target -eq (Join-Path $here 'gamerFarm.exe')) $target
} else {
  Check 'portable: nothing under the old names is left' ($left.Count -eq 0) ($left -join ', ')
}

# --- closing it, and (installed) the uninstaller from before 2.0.1 ---------------------------------------------------
if ($Mode -eq 'installed') {
  Start-Process (Join-Path $here 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DELETEDATA=yes' -Wait | Out-Null
  Start-Sleep 5
  $still = @(Get-Process gamerFarm, nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" })
  Check "installed: the uninstaller from $From closes it first" ($still.Count -eq 0) "$($still.Count) still running"
  Check '...and takes it out of the list' (-not (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}_is1'))
  Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'gamer.farm.lnk') -ErrorAction SilentlyContinue
} else {
  Start-Process (Join-Path $here 'gamerFarm.exe') -ArgumentList '--quit' -WorkingDirectory $here -WindowStyle Hidden -Wait
}
Start-Sleep 2
Get-Process gamerFarm, nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$here*" } | Stop-Process -Force
Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
Remove-Item Env:NOCATFARM_PING_URL -ErrorAction SilentlyContinue
Start-Sleep 2
Remove-Item $here, $W -Recurse -Force -ErrorAction SilentlyContinue
