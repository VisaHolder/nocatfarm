# Every button of the dashboard, on Windows: the release's portable zip unpacked into %TEMP%\nocatfarm-tests\e2e, given
# a throwaway config (three switched-off accounts with made-up logins - nothing signs in to Steam), started on its own
# port, then tests\e2e\dashboard.mjs clicks through all of it in a real browser (Chromium, through Playwright).
#   -Port and -TestRoot move it off its usual port and folder. -AppDir tests an already built folder instead of the zip.
#
# Nothing of it can show on this PC's screen. The copy runs on a desktop of its own that is never switched to (so its
# window, if it opens one, is drawn where nobody can see it), with no tray icon (--no-tray, and Tray off in its config),
# and the browser is headless. Not --no-gui: on Windows that opens a console window instead of the app's own.
param([int]$Port = 7377, [string]$TestRoot = '', [string]$AppDir = '')
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$W = Join-Path $(if ($TestRoot) { $TestRoot } else { Join-Path $env:TEMP 'nocatfarm-tests' }) 'e2e'
$E2E = Join-Path $Repo 'tests\e2e'

# Never the real dashboard's port.
if ($Port -eq 7242) { Write-Output 'FAIL  7242 is the real dashboard''s port - pick another'; exit 1 }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class OffScreen {
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

	// Starts a program on a desktop of its own (never switched to), hidden as well. Returns its process id.
	public static int Start(string exe, string args, string dir) {
		if (desktop == IntPtr.Zero) {
			desktop = CreateDesktop("nocatfarm-e2e", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
			if (desktop == IntPtr.Zero) throw new Exception("couldn't make a desktop: Windows error " + Marshal.GetLastWin32Error());
		}

		STARTUPINFO si = new STARTUPINFO();
		si.cb = Marshal.SizeOf(si);
		si.lpDesktop = "nocatfarm-e2e";
		si.dwFlags = 1;          // STARTF_USESHOWWINDOW
		si.wShowWindow = 0;      // SW_HIDE
		PROCESS_INFORMATION pi;
		if (!CreateProcess(exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi)) {
			throw new Exception("couldn't start " + exe + ": Windows error " + Marshal.GetLastWin32Error());
		}
		CloseHandle(pi.hThread);
		CloseHandle(pi.hProcess);
		return pi.dwProcessId;
	}
}
'@

if (Test-Path $W) { Remove-Item $W -Recurse -Force }
New-Item -ItemType Directory -Force $W | Out-Null
if ($AppDir) { Copy-Item "$AppDir\*" $W -Recurse } else { Expand-Archive "$Repo\dist\nocat.farm-v$Version-portable.zip" -DestinationPath $W }
node "$E2E\make-fixture.mjs" $W $Port

# Playwright and its Chromium, once.
Push-Location $E2E
if (-not (Test-Path "$E2E\node_modules\playwright")) { cmd /c "npm ci --no-audit --no-fund 2>&1" }
cmd /c "npx playwright install chromium 2>&1" | Out-Null
Pop-Location

$password = 'e2e-' + [guid]::NewGuid().ToString('N')
$id = [OffScreen]::Start("$W\nocatFarm.exe", '--minimized --no-tray', $W)

try {
  $up = $false
  for ($i = 0; $i -lt 60 -and -not $up; $i++) {
    try { Invoke-WebRequest "http://127.0.0.1:$Port/api/status" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep 1 }
  }
  if (-not $up) { Write-Output "FAIL  the copy didn't start on port $Port"; exit 1 }

  node "$E2E\dashboard.mjs" "http://127.0.0.1:$Port" $password --fixture $W --set-password
  $code = $LASTEXITCODE
} finally {
  $q = [OffScreen]::Start("$W\nocatFarm.exe", '--quit', $W)
  Wait-Process -Id $q -Timeout 30 -ErrorAction SilentlyContinue
  Start-Sleep 1
  # Only if it's still the test copy: the id of one that closed on its own can already belong to something else.
  Get-Process -Id $id -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$W\nocatFarm.exe" } | Stop-Process -Force -ErrorAction SilentlyContinue
}

exit $code
