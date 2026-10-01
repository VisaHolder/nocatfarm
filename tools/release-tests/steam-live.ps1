# The live Steam check on Windows: the release's portable zip, unpacked into a folder of its own, connects to Steam over
# WebSocket and TCP, signs in ANONYMOUSLY and sends the games-played message the idler sends (an emoji custom name and all),
# then logs off - nocatFarm --steam-selftest. No account, password or token is involved. Linux, Docker and the Mac run
# the same thing in their workflows.
#   -App <folder>  test a copy that's already unpacked (or a build output folder) instead of the portable zip
#   -TestRoot      where its folder goes (default %TEMP%\nocatfarm-tests)
param([string]$App = '', [string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$W = Join-Path $(if ($TestRoot) { $TestRoot } else { Join-Path $env:TEMP 'nocatfarm-tests' }) 'steam-live'

if (Test-Path $W) { Remove-Item $W -Recurse -Force }
New-Item -ItemType Directory -Force "$W\data" | Out-Null
if (-not $App) {
  Expand-Archive "$Repo\dist\nocat.farm-v$Version-portable.zip" -DestinationPath "$W\app"
  $App = "$W\app"
}

# Its own empty data folder: the settings a fresh install connects with, and nothing of anybody's.
$p = Start-Process (Join-Path $App 'nocatFarm.exe') -ArgumentList '--path', "`"$W\data`"", '--steam-selftest' -PassThru `
  -RedirectStandardOutput "$W\out.txt" -RedirectStandardError "$W\err.txt"
$null = $p.Handle   # Windows PowerShell only fills in ExitCode for a process whose handle was asked for
# It gives up by itself inside two minutes; this is only for one that hangs.
if (-not $p.WaitForExit(150000)) {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Get-Content "$W\out.txt" -Encoding utf8
  Write-Output "FAIL  the Steam self-test didn't finish in 150s"
  exit 1
}

Get-Content "$W\out.txt" -Encoding utf8
Get-Content "$W\err.txt" -Encoding utf8
exit $p.ExitCode
