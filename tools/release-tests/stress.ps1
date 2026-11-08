# Crash-proofing, on Windows: a hidden test copy (no Steam accounts - three switched-off ones with made-up logins, nothing
# signs in) started again and again against things that have crashed it, held it still or stopped part of it until restart:
#   1. every config and state file broken - garbage, cut short, empty, 10000 deep, 100 MB - and plugin DLLs that aren't
#   2. the settings files held open by another program, an account's file read-only
#   3. a plugin that does everything a plugin shouldn't (stress-plugin\), with the dashboard hammered on every route by a few
#      workers in bursts (stress-hammer.mjs) - memory, handles and threads watched for leaks
#   4. killed (as a power cut would) in the middle of settings saves, then started again - every file still whole
#   5. add-on installs and removals waiting for the restart, and a half-written download
#   6. closing while a plugin never lets go of its unload
# Not part of run-all.ps1 (it takes ~10 minutes). Run it before a release that touches plugins, files or the dashboard:
#   powershell -ExecutionPolicy Bypass -File tools\release-tests\stress.ps1 [-Port 7381] [-Minutes 6]
#   -AppDir a built nocat.farm folder (default: src\NocatFarm\bin\Release\net10.0, built by the test build)
#
# Kind to the PC it runs on: the copy and the hammer run at below-normal priority, 4 workers, bursts of a minute with a
# half-minute rest. Nothing reaches the internet: updates, the user count, the shared sets and swaps point at a closed
# port on this PC, and the hammer never asks for an add-on.
param([int]$Port = 7381, [int]$Minutes = 6, [string]$AppDir = '')
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Root = Join-Path $env:TEMP 'nocatfarm-tests\stress'
$fails = 0
function Check($name, $ok, $detail = '') {
  if ($ok) { Write-Output "PASS  $name" } else { $script:fails++; Write-Output "FAIL  $name$(if ($detail) { "  ($detail)" })" }
}

if ($Port -eq 7242) { Write-Output 'FAIL  7242 is the real dashboard''s port - pick another'; exit 1 }
if (-not $AppDir) { $AppDir = Join-Path $Repo 'src\NocatFarm\bin\Release\net10.0' }
if (-not (Test-Path "$AppDir\nocatFarm.exe")) { Write-Output "FAIL  no nocatFarm.exe in $AppDir - build first"; exit 1 }
$PluginDll = Join-Path $PSScriptRoot 'stress-plugin\bin\Release\net10.0\nocatfarm.stress.dll'
if (-not (Test-Path $PluginDll)) {
  dotnet build "$PSScriptRoot\stress-plugin" -c Release -m:1 -nodeReuse:false | Out-Null
  if (-not (Test-Path $PluginDll)) { Write-Output 'FAIL  the stress plugin did not build'; exit 1 }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class StressDesk {
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct STARTUPINFO {
		public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
		public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
		public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr attributes);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern bool CreateProcess(string app, string commandLine, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env,
		string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(IntPtr handle);

	static IntPtr desktop = IntPtr.Zero;

	// A program on a desktop of its own that is never switched to, hidden, at below-normal priority. Its process id.
	public static int Start(string exe, string args, string dir) {
		if (desktop == IntPtr.Zero) {
			desktop = CreateDesktop("nocatfarm-stress", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
			if (desktop == IntPtr.Zero) throw new Exception("couldn't make a desktop: Windows error " + Marshal.GetLastWin32Error());
		}

		STARTUPINFO si = new STARTUPINFO();
		si.cb = Marshal.SizeOf(si);
		si.lpDesktop = "nocatfarm-stress";
		si.dwFlags = 1;
		si.wShowWindow = 0;
		PROCESS_INFORMATION pi;
		const uint BelowNormal = 0x00004000;
		if (!CreateProcess(exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, BelowNormal, IntPtr.Zero, dir, ref si, out pi)) {
			throw new Exception("couldn't start " + exe + ": Windows error " + Marshal.GetLastWin32Error());
		}
		CloseHandle(pi.hThread);
		CloseHandle(pi.hProcess);
		return pi.dwProcessId;
	}
}
'@

# Whatever a stopped run left behind goes first.
Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like "$Root\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$App = Join-Path $Root 'app'
New-Item -ItemType Directory -Force $App | Out-Null
Copy-Item "$AppDir\*" $App -Recurse
$Exe = Join-Path $App 'nocatFarm.exe'
$Data = Join-Path $Root 'data'
$Url = "http://127.0.0.1:$Port"

# Nothing from a test copy reaches nocat.lol or GitHub: every address it could call points at a closed port here.
$env:NOCATFARM_WEB_PORT = "$Port"
$env:NOCATFARM_WEB_HOST = '127.0.0.1'
$env:NOCATFARM_PING_URL = 'http://127.0.0.1:9/ping'
$env:NOCATFARM_UPDATE_FEED = 'http://127.0.0.1:9/feed'
$env:NOCATFARM_SETS_URL = 'http://127.0.0.1:9/sets'
$env:NOCATFARM_SWAP_URL = 'http://127.0.0.1:9/swaps'

function Write-Json($file, $value) {
  New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
  [IO.File]::WriteAllText($file, ($value | ConvertTo-Json -Depth 8))
}

function New-Fixture([switch]$Plugins) {
  if (Test-Path $Data) { Remove-Item $Data -Recurse -Force }
  $c = Join-Path $Data 'config'
  Write-Json "$c\nocatFarm.json" @{ WebPort = $Port; WebHost = '127.0.0.1'; UpdateMode = 3; StartWithWindows = $false; TrayNotifications = $false;
    Tray = $false; StartMinimized = $true; KeepAwake = $false; OpenBrowserOnStart = $false; OpenDashboardAfterAdd = $false; DiscordPresence = $false;
    CountMeAsUser = $false; TutorialDone = $true; PluginsEnabled = [bool]$Plugins; FileLogging = $true; Debug = $true }
  Write-Json "$c\human.json" @{ Enabled = $false; JoinGroup = $false; SteamLogin = 'not_a_real_account'; LegitMode = $true; GameWeights = '730:70, 440:30' }
  Write-Json "$c\robot.json" @{ Enabled = $false; JoinGroup = $false; SteamLogin = 'not_a_real_account_2'; IdleGames = @(440) }
  Write-Json "$c\authbot.json" @{ Enabled = $false; JoinGroup = $false; SteamLogin = 'not_a_real_account_3' }
  if ($Plugins) {
    New-Item -ItemType Directory -Force "$Data\plugins" | Out-Null
    Copy-Item $PluginDll "$Data\plugins\"
  }
}

# --minimized as well: a broken settings file means the defaults, and the default opens a browser at start.
function Start-Copy { [StressDesk]::Start($Exe, "--no-gui --no-tray --minimized --path `"$Data`"", $App) }

function Wait-Up([int]$seconds = 90) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $seconds) {
    try { Invoke-WebRequest "$Url/api/status" -UseBasicParsing -TimeoutSec 5 | Out-Null; return $sw.Elapsed.TotalSeconds } catch { Start-Sleep -Milliseconds 500 }
  }
  return -1
}

function Alive($id) { $null -ne (Get-Process -Id $id -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe }) }

function Stop-Copy($id, [int]$seconds = 60) {
  $q = [StressDesk]::Start($Exe, "--quit --path `"$Data`"", $App)
  Wait-Process -Id $q -Timeout $seconds -ErrorAction SilentlyContinue
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ((Alive $id) -and ($sw.Elapsed.TotalSeconds -lt $seconds)) { Start-Sleep -Milliseconds 500 }
  $closed = -not (Alive $id)
  if (-not $closed) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
  return $closed
}

function Day-Log { Get-ChildItem "$Data\logs\nocatFarm-*.log" -ErrorAction SilentlyContinue | Get-Content -Raw -ErrorAction SilentlyContinue | Out-String }

# Every .json under config reads as JSON - the files nocat.farm wrote never half-written.
function Broken-Json {
  $bad = @()
  foreach ($f in Get-ChildItem "$Data\config" -Recurse -Filter *.json -File -ErrorAction SilentlyContinue) {
    try { [IO.File]::ReadAllText($f.FullName) | ConvertFrom-Json | Out-Null } catch { $bad += $f.FullName.Substring($Data.Length + 1) }
  }
  return $bad
}

function Sample($id) {
  $p = Get-Process -Id $id -ErrorAction SilentlyContinue
  if (-not $p) { return $null }
  [pscustomobject]@{ At = Get-Date; MB = [math]::Round($p.PrivateMemorySize64 / 1MB); Handles = $p.HandleCount; Threads = $p.Threads.Count }
}

try {
  # ── 1. every file broken ──────────────────────────────────────────────────
  New-Fixture -Plugins
  $c = Join-Path $Data 'config'
  $rng = [Random]::new(7)
  $garbage = [byte[]]::new(4096); $rng.NextBytes($garbage)
  [IO.File]::WriteAllBytes("$c\nocatFarm.json", $garbage)
  [IO.File]::WriteAllText("$c\human.json", '{ "Enabled": false, "SteamLogin": "not_a')
  [IO.File]::WriteAllText("$c\robot.json", '')
  [IO.File]::WriteAllText("$c\authbot.json", ('[' * 100000))
  $before = @{}
  foreach ($f in 'nocatFarm.json', 'human.json', 'robot.json', 'authbot.json') { $before[$f] = (Get-FileHash "$c\$f").Hash }

  $id = Start-Copy
  $up = Wait-Up 120
  Start-Sleep 20
  Check 'broken files: starts and answers anyway (garbage settings; cut-short, empty and 10000-deep accounts)' (($up -ge 0) -and (Alive $id)) "up after ${up}s"
  $pages = foreach ($route in 'status', 'plugins', 'config', 'log', 'history', 'library') { try { (Invoke-WebRequest "$Url/api/$route" -UseBasicParsing -TimeoutSec 30).StatusCode } catch { 0 } }
  Check 'broken files: every page of the dashboard still answers' (@($pages | Where-Object { $_ -ne 200 }).Count -eq 0) ($pages -join ' ')
  $kept = $true
  foreach ($f in $before.Keys) {
    $now = if (Test-Path "$c\$f") { (Get-FileHash "$c\$f").Hash } else { '' }
    $aside = if (Test-Path "$c\$f.broken") { (Get-FileHash "$c\$f.broken").Hash } else { '' }
    if (($now -ne $before[$f]) -and ($aside -ne $before[$f])) { $kept = $false; Write-Output "      $f was replaced, and not kept aside" }
  }
  Check 'broken files: nothing wiped - each broken file is still there, or kept aside as .broken' $kept
  $log = Day-Log
  Check 'broken files: said in the log' (($log -match 'nocatFarm.json') -and ($log -match "can't read|couldn't read|didn't load|couldn't load"))
  Check 'broken files: closes cleanly' (Stop-Copy $id)

  # The settings fine (plugins on), everything else broken: state files (one of 100 MB), the plugin's own files, DLLs that aren't.
  New-Fixture -Plugins
  New-Item -ItemType Directory -Force "$c\state\history", "$c\plugins" | Out-Null
  [IO.File]::WriteAllText("$c\state\comments-human.json", '{ "Posts": [ 9223372036854775807, -1')
  [IO.File]::WriteAllText("$c\state\comments-robot.json", '{ "Posts": [ 9223372036854775807, -1, 5 ] }')
  [IO.File]::WriteAllText("$c\state\buy-human.json", '{ "Ok": true, "Entries": [ null ], "Waits": [ null ] }')
  [IO.File]::WriteAllBytes("$c\state\buy-robot.json", $garbage)
  [IO.File]::WriteAllBytes("$c\plugins\stress-flood.settings.json", $garbage)
  [IO.File]::WriteAllText("$c\plugins\stress-flood.json", '{ "nested": ' + ('[' * 50000))
  [IO.File]::WriteAllText("$c\state\history\$(Get-Date -Format yyyy-MM).json", '{ "x": "' + ('a' * 100MB) + '" }')
  [IO.File]::WriteAllBytes("$Data\plugins\broken.dll", $garbage)
  [IO.File]::WriteAllBytes("$Data\plugins\empty.dll", [byte[]]::new(0))
  [GC]::Collect()
  $id = Start-Copy
  $up = Wait-Up 120
  Start-Sleep 45
  $pages = foreach ($route in 'status', 'plugins', 'config', 'log', 'history', 'library') { try { (Invoke-WebRequest "$Url/api/$route" -UseBasicParsing -TimeoutSec 30).StatusCode } catch { 0 } }
  Check 'broken state files, plugin files and DLLs: starts, every page answers' (($up -ge 0) -and (Alive $id) -and (@($pages | Where-Object { $_ -ne 200 }).Count -eq 0)) "up after ${up}s; $($pages -join ' ')"
  $log = Day-Log
  Check 'broken state files, plugin files and DLLs: the plugin still loads, its unreadable settings kept aside as .broken' (($log -match 'plugin loaded: stress-flood') -and (Test-Path "$c\plugins\stress-flood.settings.json.broken"))
  Check 'broken state files, plugin files and DLLs: closes cleanly' (Stop-Copy $id)

  # ── 2. files held open by another program, an account read-only ───────────
  New-Fixture -Plugins
  Set-ItemProperty "$c\robot.json" -Name IsReadOnly -Value $true
  $lockedHash = (Get-FileHash "$c\nocatFarm.json").Hash
  $held = @([IO.File]::Open("$c\nocatFarm.json", 'Open', 'ReadWrite', 'None'), [IO.File]::Open("$c\human.json", 'Open', 'ReadWrite', 'None'))
  try {
    $id = Start-Copy
    $up = Wait-Up 120
    Check 'locked files: starts and answers while its settings are held open by another program' (($up -ge 0) -and (Alive $id)) "up after ${up}s"
    try { Invoke-RestMethod "$Url/api/config" -Method Post -ContentType 'application/json' -Body '{"Theme":"dark"}' -TimeoutSec 30 | Out-Null } catch { }
  } finally { $held | ForEach-Object { $_.Dispose() } }
  Start-Sleep 2
  try { Invoke-RestMethod "$Url/api/config" -Method Post -ContentType 'application/json' -Body '{"Theme":"light"}' -TimeoutSec 30 | Out-Null } catch { }
  Check 'locked files: still running once they''re let go' (Alive $id)
  Check 'locked files: closes cleanly' (Stop-Copy $id)
  Check 'locked files: the settings it couldn''t read were never saved over with defaults' ((Get-FileHash "$c\nocatFarm.json").Hash -eq $lockedHash -or (((Get-Content "$c\nocatFarm.json" -Raw) | ConvertFrom-Json).WebPort -eq $Port))
  Set-ItemProperty "$c\robot.json" -Name IsReadOnly -Value $false

  # ── 3. a hostile plugin, the dashboard hammered in bursts, leaks watched ──
  New-Fixture -Plugins
  $id = Start-Copy
  $up = Wait-Up 120
  Check 'hostile plugin: the dashboard is up while plugins that block or never load are still being waited on' (($up -ge 0) -and ($up -lt 60)) "up after ${up}s"
  Start-Sleep 45   # the 15 seconds each of the two that block or never finish, and the rest of the start
  $log = Day-Log
  Check 'hostile plugin: the ones that throw, block or never finish are left out; the flooding one loads' (($log -match 'plugin loaded: stress-flood') -and ($log -match 'stress-throws failed to load') -and ($log -match 'stress-blocks failed to load') -and ($log -match 'stress-never failed to load'))
  $samples = @()
  $samples += Sample $id
  $bursts = [math]::Max(1, [math]::Floor($Minutes * 60 / 90))
  $hammered = @()
  for ($b = 0; $b -lt $bursts -and (Alive $id); $b++) {
    $h = Start-Process node -ArgumentList "`"$PSScriptRoot\stress-hammer.mjs`" $Url 60 4" -NoNewWindow -PassThru -RedirectStandardOutput "$Root\hammer-$b.json" -RedirectStandardError "$Root\hammer-$b.err"
    try { $h.PriorityClass = 'BelowNormal' } catch { }
    $h.WaitForExit(150000) | Out-Null
    if (-not $h.HasExited) { $h.Kill() }
    $hammered += Get-Content "$Root\hammer-$b.json" -Raw -ErrorAction SilentlyContinue
    Start-Sleep 30
    $samples += Sample $id
  }
  $samples | Format-Table -AutoSize | Out-String | Write-Output
  $hammered | ForEach-Object { if ($_) { Write-Output "      $($_.Trim())" } }
  Check 'hostile plugin + hammering: still running after every burst' (Alive $id)
  $answers = try { (Invoke-WebRequest "$Url/api/status" -UseBasicParsing -TimeoutSec 15).StatusCode } catch { 0 }
  Check 'hostile plugin + hammering: the dashboard still answers straight away' ($answers -eq 200)
  $errs = 0; $reqs = 0
  foreach ($j in $hammered) { if ($j) { $o = $j | ConvertFrom-Json; $reqs += $o.requests; $errs += ($o.status.'500' + 0) } }
  Check 'hostile plugin + hammering: no request ever failed inside the app (no 500s)' ($errs -eq 0) "$errs of $reqs"
  $settled = @($samples | Where-Object { $_ })
  if ($settled.Count -ge 3) {
    $first = $settled[1]; $last = $settled[-1]
    Check 'hostile plugin + hammering: memory doesn''t keep growing (under 250 MB more from the first burst to the last)' (($last.MB - $first.MB) -lt 250) "$($first.MB) MB -> $($last.MB) MB"
    Check 'hostile plugin + hammering: threads and handles don''t keep growing' ((($last.Threads - $first.Threads) -lt 60) -and (($last.Handles - $first.Handles) -lt 3000)) "threads $($first.Threads) -> $($last.Threads), handles $($first.Handles) -> $($last.Handles)"
  }
  Check 'hostile plugin + hammering: every settings file still whole' ((Broken-Json).Count -eq 0) ((Broken-Json) -join ', ')
  $sizeMb = [math]::Round(((Get-ChildItem "$Data\logs" -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
  Check 'hostile plugin + hammering: its flood of log lines is capped - the log stays small' ($sizeMb -lt 200) "$sizeMb MB of logs"

  # ── 4. killed in the middle of settings saves ─────────────────────────────
  $h = Start-Process node -ArgumentList "`"$PSScriptRoot\stress-hammer.mjs`" $Url 20 4" -NoNewWindow -PassThru -RedirectStandardOutput "$Root\kill.json" -RedirectStandardError "$Root\kill.err"
  Start-Sleep 8
  Stop-Process -Id $id -Force
  $h.WaitForExit(60000) | Out-Null
  Start-Sleep 1
  $bad = Broken-Json
  Check 'killed mid-save: every settings and state file is whole (the old one or the new, never half)' ($bad.Count -eq 0) ($bad -join ', ')
  $id = Start-Copy
  $up = Wait-Up 120
  Check 'killed mid-save: starts again straight away' (($up -ge 0) -and (Alive $id)) "up after ${up}s"
  $halves = @(Get-ChildItem "$Data\config" -Recurse -File -Filter *.tmp | Where-Object { $_.Name -match '\.[0-9a-f]{32}\.tmp$' })
  Check 'killed mid-save: the half-written copies the kill left are cleared at the start' ($halves.Count -eq 0) (($halves | ForEach-Object Name) -join ', ')
  Check 'killed mid-save: closes cleanly, with the plugin that never lets go of its unload' (Stop-Copy $id 60)

  # ── 5. installs and removals waiting for the restart, a half-written download ──
  Copy-Item $PluginDll "$Data\plugins\nocatfarm.stress.dll.new"
  [IO.File]::WriteAllBytes("$Data\plugins\old.dll", $garbage)
  [IO.File]::WriteAllText("$Data\plugins\old.dll.remove", '')
  [IO.File]::WriteAllBytes("$Data\plugins\nocatfarm.stress.dll.0123456789abcdef.tmp", $garbage)
  $id = Start-Copy
  $up = Wait-Up 120
  Start-Sleep 5
  Check 'waiting add-on changes: the new file in place, the removed one gone, the half-written download cleared - and it starts' (($up -ge 0) -and
    -not (Test-Path "$Data\plugins\nocatfarm.stress.dll.new") -and -not (Test-Path "$Data\plugins\old.dll") -and -not (Test-Path "$Data\plugins\old.dll.remove") -and
    -not (Test-Path "$Data\plugins\nocatfarm.stress.dll.0123456789abcdef.tmp") -and (Test-Path "$Data\plugins\nocatfarm.stress.dll"))

  # ── 6. closing with a plugin that never lets go ───────────────────────────
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $closed = Stop-Copy $id 60
  Check 'closing: done within a minute although a plugin''s unload never returns' $closed "$([math]::Round($sw.Elapsed.TotalSeconds))s"
  $crash = Test-Path "$Data\logs\crash.log"
  Check 'no crash at any point (no crash.log)' (-not $crash) $(if ($crash) { (Get-Content "$Data\logs\crash.log" -Tail 3) -join ' | ' })
} finally {
  Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like "$Root\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  foreach ($v in 'WEB_PORT', 'WEB_HOST', 'PING_URL', 'UPDATE_FEED', 'SETS_URL', 'SWAP_URL') { Remove-Item "Env:NOCATFARM_$v" -ErrorAction SilentlyContinue }
  Start-Sleep 1
  if (-not $env:STRESS_KEEP) { Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output $(if ($fails -eq 0) { 'all passed' } else { "$fails failed" })
exit $fails
