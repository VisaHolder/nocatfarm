# The real updater against the live GitHub release: a copy of the previous release runs 'update accept' and must
# come back up as this version. Run it after the release is published.  -From 1.5.3
param([Parameter(Mandatory)][string]$From)
# The repo, the version being released (from the csproj), and a working folder outside the repo for the test copies.
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$SP = Join-Path $env:TEMP 'nocatfarm-tests'
New-Item -ItemType Directory -Force $SP | Out-Null
$ErrorActionPreference = 'Stop'
$dir = Join-Path $SP 'update-from'
$port = 7295
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
Expand-Archive "$Repo\dist\nocat.farm-v$From-portable.zip" $dir
New-Item -ItemType Directory -Force (Join-Path $dir 'config') | Out-Null
Set-Content (Join-Path $dir 'config\nocatFarm.json') -Encoding utf8 -Value "{`"WebPort`":$port,`"CheckForUpdates`":true,`"JoinGroup`":false,`"StartWithWindows`":false,`"TrayNotifications`":false,`"OpenBrowserOnStart`":false}"

$p = Start-Process (Join-Path $dir 'nocatFarm.exe') -ArgumentList '--minimized' -WorkingDirectory $dir -WindowStyle Hidden -PassThru
$api = "http://127.0.0.1:$port"
$v = $null
for ($i = 0; $i -lt 40; $i++) { try { $v = (Invoke-RestMethod "$api/api/status").Version; break } catch { Start-Sleep 1 } }
"started: $v"
$r = Invoke-RestMethod "$api/api/command" -Method Post -ContentType 'application/json' -Body '{"Line":"update accept"}'
"update accept -> $($r.output)"
$t0 = Get-Date; $now = $v
while (((Get-Date) - $t0).TotalSeconds -lt 300) {
  Start-Sleep 3
  try { $now = (Invoke-RestMethod "$api/api/status" -TimeoutSec 3).Version } catch { continue }
  if ($now -eq $Version) { break }
}
"after $([int]((Get-Date) - $t0).TotalSeconds)s: $now"
if ($now -eq $Version) { "PASS  $From updated itself to $Version" } else { 'FAIL  still ' + $now }
Get-ChildItem $dir -Filter 'added.txt' -ErrorAction SilentlyContinue | ForEach-Object { 'leftover: ' + $_.Name }
Get-Content (Get-ChildItem (Join-Path $dir 'logs') -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Tail 25
Start-Process (Join-Path $dir 'nocatFarm.exe') -ArgumentList '--quit' -WorkingDirectory $dir -WindowStyle Hidden -Wait
Start-Sleep 2
"still running: $((Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" }).Count)"
