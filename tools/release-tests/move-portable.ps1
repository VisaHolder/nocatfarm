# The setup moving a RUNNING portable copy (the previous release) into the install: closed first, account and port
# kept, its startup entry taken over, the old folder left alone.
# Then, with the accounts that uninstall kept still in the folder and a file in them held open (Explorer, a virus scan),
# a second move: it can't set them aside, so it stops - and every kept account is still there.
# Needs dist\nocat.farm-v<version>-setup.exe and dist\nocat.farm-v<from>-portable.zip:  -From 1.5.3
# (-Setup another-setup.exe runs the same checks against another build's setup.)
param([Parameter(Mandatory)][string]$From, [string]$Setup = '')
# The repo, the version being released (from the csproj), and a working folder outside the repo for the test copies.
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$SP = Join-Path $env:TEMP 'nocatfarm-tests'
New-Item -ItemType Directory -Force $SP | Out-Null
$ErrorActionPreference = 'Continue'
$old = "$SP\old147"; $inst = "$SP\inst148"
$setup = if ($Setup) { (Resolve-Path $Setup).Path } else { "$Repo\dist\nocat.farm-v$Version-setup.exe" }
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
function Check($name, $ok, $detail = '') { "{0}  {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }) }
$savedRun = (Get-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm

# The real startup entry, remembered on disk. Read from the registry alone, a run that overlapped another (or followed
# one that was killed) saved a TEST copy's entry as the real one and put that back at the end - the owner's nocat.farm
# then stopped starting with Windows. An entry pointing in here is never taken for the real one.
$realRunFile = Join-Path $SP 'real-run-entry.txt'
# No entry at all is the owner's choice (Start with Windows off) - the saved one is forgotten, not put back.
if (-not $savedRun) { Remove-Item $realRunFile -ErrorAction SilentlyContinue }
elseif ($savedRun -notlike "*nocatfarm-tests*") { Set-Content $realRunFile $savedRun -Encoding utf8 }
elseif (Test-Path $realRunFile) { $savedRun = (Get-Content $realRunFile -Raw).Trim() }
else { $savedRun = $null }
try {
  Remove-Item $old, $inst -Recurse -Force -ErrorAction SilentlyContinue
  Expand-Archive "$Repo\dist\nocat.farm-v$From-portable.zip" $old
  New-Item -ItemType Directory -Force "$old\config" | Out-Null
  '{ "WebPort": 7288, "WebHost": "127.0.0.1", "StartWithWindows": true, "StartMinimized": true, "OpenBrowserOnStart": false, "CheckForUpdates": false, "Language": "en", "TutorialDone": true, "CountMeAsUser": false }' | Set-Content "$old\config\nocatFarm.json" -Encoding utf8
  '{ "Enabled": false, "SteamLogin": "nf_dummy_moved", "IdleGames": [730, 440], "CustomGameNameEnabled": true, "CustomGameName": "moved ok" }' | Set-Content "$old\config\moved.json" -Encoding utf8
  # No copy here ever pings nocat.lol (every one started below inherits this): an older release can save its settings
  # without "Count me as a user" off.
  $env:NOCATFARM_PING_URL = 'http://127.0.0.1:9/api/farm/ping'
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

  "--- the kept accounts can't be set aside (a file in them is open): the move stops, and none of them is deleted"
  # The uninstaller finishes from a copy of itself: until it has, the setup would take this for an update, not a move.
  $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}_is1"
  for ($i = 0; ($i -lt 60) -and ((Test-Path $uninstallKey) -or (Test-Path "$inst\nocatFarm.exe")); $i++) { Start-Sleep 1 }
  Check 'the uninstall kept the accounts' (Test-Path "$inst\config\moved.json")
  '{ "Enabled": false, "SteamLogin": "nf_dummy_kept" }' | Set-Content "$inst\config\kept.json" -Encoding utf8
  $p = Start-Process "$old\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $old -WindowStyle Hidden -PassThru
  Start-Sleep 6
  Set-ItemProperty $runKey -Name nocatFarm -Value "`"$old\nocatFarm.exe`" --minimized"
  $held = [IO.File]::Open("$inst\config\nocatFarm.json", 'Open', 'ReadWrite', 'None')
  try {
    $s = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$inst`"", '/MOVE=yes', '/STARTUP=no', "/LOG=`"$SP\move-held.log`"" -PassThru -Wait
  } finally {
    $held.Dispose()
  }
  Check 'setup finished' ($s.ExitCode -eq 0) "exit $($s.ExitCode)"
  $left = @(Get-ChildItem "$inst\config" -File -ErrorAction SilentlyContinue | ForEach-Object Name)
  Check 'every kept account is still there' ((Test-Path "$inst\config\kept.json") -and (Test-Path "$inst\config\moved.json")) ($left -join ', ')
  Check 'the setup log says why it did not move the copy in' ([bool](Select-String -Path "$SP\move-held.log" -Pattern "couldn't set the kept" -SimpleMatch -Quiet))
  Check 'the portable copy is left as it was' (Test-Path "$old\config\moved.json")
  $u = Start-Process "$inst\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DELETEDATA=yes' -PassThru -Wait
  for ($i = 0; ($i -lt 30) -and (Test-Path "$inst\config"); $i++) { Start-Sleep 1 }   # the uninstaller finishes from a copy of itself
  Check 'uninstalled again, data and all' (($u.ExitCode -eq 0) -and -not (Test-Path "$inst\config")) "exit $($u.ExitCode)"
} finally {
  Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*old147*" -or $_.Path -like "*inst148*" } | Stop-Process -Force
  if ($savedRun) { Set-ItemProperty $runKey -Name nocatFarm -Value $savedRun } else { Remove-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue }
  "restored reap's startup entry: $((Get-ItemProperty $runKey -Name nocatFarm -ErrorAction SilentlyContinue).nocatFarm)"
}
