# The setup moving a RUNNING portable copy (the previous release) into the install: closed first, account and port
# kept, its startup entry taken over, the old folder left alone.
# Needs dist\nocat.farm-v<version>-setup.exe and dist\nocat.farm-v<from>-portable.zip:  -From 1.5.3
param([Parameter(Mandatory)][string]$From)
# The repo, the version being released (from the csproj), and a working folder outside the repo for the test copies.
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$SP = Join-Path $env:TEMP 'nocatfarm-tests'
New-Item -ItemType Directory -Force $SP | Out-Null
$ErrorActionPreference = 'Continue'
$old = "$SP\old147"; $inst = "$SP\inst148"
$setup = "$Repo\dist\nocat.farm-v$Version-setup.exe"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
function Check($name, $ok, $detail = '') { "{0}  {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }) }
$savedRun = (Get-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm
try {
  Remove-Item $old, $inst -Recurse -Force -ErrorAction SilentlyContinue
  Expand-Archive "$Repo\dist\nocat.farm-v$From-portable.zip" $old
  New-Item -ItemType Directory -Force "$old\config" | Out-Null
  '{ "WebPort": 7288, "WebHost": "127.0.0.1", "StartWithWindows": true, "StartMinimized": true, "OpenBrowserOnStart": false, "CheckForUpdates": false, "Language": "en", "TutorialDone": true }' | Set-Content "$old\config\nocatFarm.json" -Encoding utf8
  '{ "Enabled": false, "SteamLogin": "nf_dummy_moved", "IdleGames": [730, 440], "CustomGameNameEnabled": true, "CustomGameName": "moved ok" }' | Set-Content "$old\config\moved.json" -Encoding utf8
  $p = Start-Process "$old\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $old -WindowStyle Hidden -PassThru
  Start-Sleep 6
  Set-ItemProperty $runKey -Name nocatFarm -Value "`"$old\nocatFarm.exe`" --minimized"
  $v = try { (Invoke-RestMethod http://127.0.0.1:7288/api/status).Version } catch { 'down' }
  Check 'the previous release, running on its own port' ($v -eq $From) $v
  $pointsOld = (Get-ItemProperty $runKey).nocatFarm -like "*old147*"
  Check 'its start-with-Windows entry points at it (so the setup finds it)' $pointsOld
  if (-not $pointsOld) { throw 'Run entry not pointing at the test copy - stopping before the setup could find the real app' }

  $s = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$inst`"", '/MOVE=yes', '/STARTUP=no', "/LOG=`"$SP\move.log`"" -PassThru -Wait
  Check 'setup finished' ($s.ExitCode -eq 0) "exit $($s.ExitCode)"
  Start-Sleep 2
  Check 'the running copy was closed first' ($p.HasExited)
  Check 'the account came across' (Test-Path "$inst\config\moved.json")
  $cfg = Get-Content "$inst\config\nocatFarm.json" -Raw | ConvertFrom-Json
  Check 'its own port was kept (not reset to 7242)' ($cfg.WebPort -eq 7288) "port $($cfg.WebPort)"
  Check 'start with Windows off, as chosen' (-not $cfg.StartWithWindows)
  $r = (Get-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm
  Check 'the old copy no longer starts with Windows' (-not ($r -like "*old147*")) "$r"
  Check 'the old folder is left alone' (Test-Path "$old\config\moved.json")

  $n = Start-Process "$inst\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $inst -WindowStyle Hidden -PassThru
  Start-Sleep 7
  $st = try { Invoke-RestMethod http://127.0.0.1:7288/api/status } catch { $null }
  Check 'the installed copy opens on the same port with the account' (($st.Version -eq $Version) -and ($st.Bots.Name -contains 'moved')) "$($st.Version) $($st.Bots.Name -join ',')"
  Start-Process "$inst\nocatFarm.exe" -ArgumentList '--quit' -WorkingDirectory $inst -WindowStyle Hidden -Wait
  Start-Sleep 2
  Check '--quit closed it' ($n.HasExited)
  $u = Start-Process "$inst\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
  Check 'silent uninstall finished without stopping on anything' ($u.ExitCode -eq 0) "exit $($u.ExitCode)"
} finally {
  Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*old147*" -or $_.Path -like "*inst148*" } | Stop-Process -Force
  if ($savedRun) { Set-ItemProperty $runKey -Name nocatFarm -Value $savedRun } else { Remove-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue }
  "restored reap's startup entry: $((Get-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm)"
}
