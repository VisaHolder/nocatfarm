# Signing in from outside on Windows: the release's portable zip, unpacked and started hidden on its own port, then the
# same end-to-end check Linux and the Mac run (tests/security-check.sh, through Git's sh).
#   -Port and -TestRoot move it off its usual port and folder, to run beside another test.
param([int]$Port = 7356, [string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$W = Join-Path $(if ($TestRoot) { $TestRoot } else { Join-Path $env:TEMP 'nocatfarm-tests' }) 'security'

if (Test-Path $W) { Remove-Item $W -Recurse -Force }
New-Item -ItemType Directory -Force "$W\config" | Out-Null
Expand-Archive "$Repo\dist\nocat.farm-v$Version-portable.zip" -DestinationPath $W
Set-Content "$W\config\nocatFarm.json" -Encoding utf8 -Value "{`"WebPort`":$port,`"WebHost`":`"127.0.0.1`",`"CheckForUpdates`":false,`"AutoUpdate`":0,`"JoinGroup`":false,`"StartWithWindows`":false,`"TrayNotifications`":false,`"OpenBrowserOnStart`":false,`"CountMeAsUser`":false}"
Set-Content "$W\config\demo.json" -Encoding utf8 -Value '{"Enabled":false,"SteamLogin":"not_a_real_account"}'
$p = Start-Process "$W\nocatFarm.exe" -ArgumentList '--minimized' -WorkingDirectory $W -WindowStyle Hidden -PassThru

try {
  $up = $false
  for ($i = 0; $i -lt 60 -and -not $up; $i++) {
    try { Invoke-WebRequest "http://127.0.0.1:$port/api/status" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep 1 }
  }
  if (-not $up) { Write-Output "FAIL  the copy didn't start on port $port"; exit 1 }

  $sh = @("$env:ProgramFiles\Git\bin\sh.exe", "$env:ProgramFiles\Git\usr\bin\sh.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $sh) { Write-Output "FAIL  Git's sh.exe wasn't found"; exit 1 }

  $config = ($W -replace '\\', '/') + '/config'
  Push-Location $W
  & $sh "$Repo\tests\security-check.sh" "http://127.0.0.1:$port" $config
  $code = $LASTEXITCODE
  Pop-Location
} finally {
  Start-Process "$W\nocatFarm.exe" -ArgumentList '--quit' -WorkingDirectory $W -WindowStyle Hidden -Wait
  Start-Sleep 1
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}

exit $code
