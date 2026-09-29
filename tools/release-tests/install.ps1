# The setup, end to end in a scratch folder: fresh install, start, a second launch, --quit, --setup choices,
# reinstall over it, uninstall. Your own start-with-Windows entry and desktop shortcut are saved and put back.
# Needs dist\nocat.farm-v<version>-setup.exe (tools\package-release.ps1).
# The repo, the version being released (from the csproj), and a working folder outside the repo for the test copies.
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$SP = Join-Path $env:TEMP 'nocatfarm-tests'
New-Item -ItemType Directory -Force $SP | Out-Null
$ErrorActionPreference = 'Continue'
$inst = "$SP\nocat.farm"
$setup = "$Repo\dist\nocat.farm-v$Version-setup.exe"
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}_is1"
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\nocat.farm.lnk"
$desktop = [Environment]::GetFolderPath('Desktop') + "\nocat.farm.lnk"
# reap's own start-with-Windows entry and desktop shortcut: saved now, put back at the end whatever happens
$savedRun = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm
$savedLnk = "$SP\saved-desktop.lnk"; Remove-Item $savedLnk -ErrorAction SilentlyContinue
if (Test-Path ([Environment]::GetFolderPath('Desktop') + "\nocat.farm.lnk")) { Copy-Item ([Environment]::GetFolderPath('Desktop') + "\nocat.farm.lnk") $savedLnk }
try {
function Check($name, $ok, $detail = '') { "{0}  {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }) }

New-Item -ItemType Directory -Force "$inst\config\state" | Out-Null; Set-Content "$inst\config\state\import-pending.json" '{"From":"asf","Path":"C:\old"}'   # left by an earlier install into the same folder
"--- 1. fresh install: advanced (port 7289, start hidden), start with Windows, desktop shortcut, start fresh"
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$inst`"", '/MOVE=no', '/FROM=fresh', '/STARTUP=yes', '/DESKTOP=yes', '/HIDDEN=yes', '/PORT=7289', "/LOG=`"$SP\install1.log`"" -PassThru -Wait
Check 'setup finished' ($p.ExitCode -eq 0) "exit $($p.ExitCode)"
Check 'program files' ((Test-Path "$inst\nocatFarm.exe") -and (Test-Path "$inst\wwwroot\app.js") -and (Test-Path "$inst\unins000.exe"))
$cfg = Get-Content "$inst\config\nocatFarm.json" -Raw | ConvertFrom-Json
Check 'choices saved' (($cfg.WebPort -eq 7289) -and $cfg.StartWithWindows -and $cfg.StartMinimized -and ($cfg.Language -eq 'en')) "port $($cfg.WebPort), startup $($cfg.StartWithWindows), hidden $($cfg.StartMinimized), lang $($cfg.Language)"
$r = (Get-ItemProperty $run -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm
Check 'starts with Windows right away (no first launch needed)' ($r -like "*$inst\nocatFarm.exe*") $r
$k = Get-ItemProperty $key -ErrorAction SilentlyContinue
Check 'listed in Settings > Apps' (($k.DisplayName -eq 'nocat.farm') -and ($k.DisplayVersion -eq $Version) -and ($k.Publisher -eq 'reap.')) "$($k.DisplayName) $($k.DisplayVersion) by $($k.Publisher)"
Check 'Start menu shortcut' (Test-Path $startMenu)
Check 'desktop shortcut' (Test-Path $desktop)
Check 'no import asked for (fresh)' (-not (Test-Path "$inst\config\state\import-pending.json"))

"--- 2. run it: dashboard up on 7289, a second launch comes forward, --quit closes it"
$cfgRaw = Get-Content "$inst\config\nocatFarm.json" -Raw | ConvertFrom-Json
$cfgRaw | Add-Member -NotePropertyName OpenBrowserOnStart -NotePropertyValue $false -Force
$cfgRaw | Add-Member -NotePropertyName JoinGroup -NotePropertyValue $false -Force
$cfgRaw | Add-Member -NotePropertyName CheckForUpdates -NotePropertyValue $false -Force
[IO.File]::WriteAllText("$inst\config\nocatFarm.json", ($cfgRaw | ConvertTo-Json -Depth 10))
Start-Process "$inst\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $inst; Start-Sleep 7
$st = try { (Invoke-RestMethod http://127.0.0.1:7289/api/status).Version } catch { "down: $($_.Exception.Message)" }
Check 'dashboard answers on the chosen port' ($st -eq $Version) $st
$t0 = Get-Date; $second = Start-Process "$inst\nocatFarm.exe" -PassThru -Wait
Check 'second launch hands over and exits at once' (($second.ExitCode -eq 0) -and (((Get-Date) - $t0).TotalSeconds -lt 3)) "exit $($second.ExitCode) in $([int](((Get-Date) - $t0).TotalMilliseconds))ms"
$t0 = Get-Date; $q = Start-Process "$inst\nocatFarm.exe" -ArgumentList '--quit' -PassThru -Wait
$gone = -not (Get-CimInstance Win32_Process -Filter "Name='nocatFarm.exe'" | Where-Object { $_.ExecutablePath -like "$inst*" })
Check '--quit closes it cleanly' (($q.ExitCode -eq 0) -and $gone) "exit $($q.ExitCode) in $([int](((Get-Date) - $t0).TotalSeconds))s"
Check 'it said goodbye properly' ([bool](Select-String -Path "$inst\logs\*.log" -Pattern 'shutting down' -Quiet))

"--- 3. --setup: ImportFrom is remembered for the dashboard; a bad choice is refused"
$s1 = Start-Process "$inst\nocatFarm.exe" -ArgumentList '--setup', 'ImportFrom=asf', '"ImportPath=C:\Some Folder\arch"' -PassThru -Wait
$pend = Get-Content "$inst\config\state\import-pending.json" -Raw -ErrorAction SilentlyContinue
Check 'coming-from saved' (($s1.ExitCode -eq 0) -and ($pend -match '"asf"') -and ($pend -match 'Some Folder')) $pend
$s2 = Start-Process "$inst\nocatFarm.exe" -ArgumentList '--setup', 'WebPassword=nope' -PassThru -Wait
Check 'a setting the setup may not change is refused' ($s2.ExitCode -eq 2) "exit $($s2.ExitCode)"

"--- 4. run the setup again: it updates in place and keeps the settings"
$before = Get-Content "$inst\config\nocatFarm.json" -Raw
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$SP\install2.log`"" -PassThru -Wait
Check 'reinstall finished into the same folder' (($p.ExitCode -eq 0) -and (Select-String -Path "$SP\install2.log" -Pattern ([regex]::Escape($inst)) -Quiet)) "exit $($p.ExitCode)"
Check 'settings untouched by the reinstall' ((Get-Content "$inst\config\nocatFarm.json" -Raw) -eq $before)

"--- 5. uninstall: everything it added goes; accounts and settings kept (the default)"
$p = Start-Process "$inst\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait; Start-Sleep 2
Check 'uninstall finished' ($p.ExitCode -eq 0) "exit $($p.ExitCode)"
Check 'program files gone' (-not (Test-Path "$inst\nocatFarm.exe"))
Check 'Settings > Apps entry gone' (-not (Test-Path $key))
Check 'Start menu and desktop shortcuts gone' ((-not (Test-Path $startMenu)) -and (-not (Test-Path $desktop)))
Check 'start with Windows entry gone' (-not (Get-ItemProperty $run -Name nocatFarm -ErrorAction SilentlyContinue))
Check 'accounts and settings kept' (Test-Path "$inst\config\nocatFarm.json")
} finally {
  if ($savedRun) { Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name nocatFarm -Value $savedRun }
  if (Test-Path $savedLnk) { Copy-Item $savedLnk ([Environment]::GetFolderPath('Desktop') + "\nocat.farm.lnk") -Force }
  "restored reap's startup entry: $((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm)"
}
