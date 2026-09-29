# Everything a release has to pass on this PC, in one go, stopping at the first failure:
#   build (0 warnings), unit tests, translations, the dashboard's script parses, the release files are built,
#   then the setup, moving a running portable copy, and updating itself with a broken and a good version
#   (portable and installed). Linux, Mac and Docker run on GitHub: this starts those runs and waits for them.
#   -From 1.5.4   the previous release, for the move test (its portable zip must be in dist\)
param([Parameter(Mandatory)][string]$From)
$ErrorActionPreference = 'Continue'   # native tools write progress to stderr; failures are read from the output instead
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = (Select-Xml -Path "$Repo\src\NocatFarm\NocatFarm.csproj" -XPath '//Version').Node.InnerText
$failed = @()
function Step($name, [scriptblock]$run) {
  Write-Host "== $name" -ForegroundColor Cyan
  $out = & $run 2>&1 | Out-String
  $bad = ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) -or ($out -match '(?m)^\s*FAIL\b') -or ($out -match '\b[1-9]\d* failed\b') -or ($out -match '[1-9]\d* Error\(s\)') -or ($out -match '[1-9]\d* Warning\(s\)') -or ($out -match '[1-9]\d* problem\(s\)') -or (($name -like '*GitHub*') -and ($out -match '(?m) (failure|cancelled)\s*$'))
  $summary = ($out -split "`n" | Where-Object { $_ -match 'PASS|FAIL|all passed|all clear|Error\(s\)|Warning\(s\)|problem|Done  ->|success|failure' } | Select-Object -Last 12) -join "`n"
  Write-Host $summary
  if ($bad) { $script:failed += $name; Write-Host "!! $name FAILED" -ForegroundColor Red; Write-Host ($out -split "`n" | Select-Object -Last 30 | Out-String) }
}

Push-Location $Repo
try {
  Step 'build' { dotnet build src/NocatFarm -c Release --no-incremental }
  Step 'unit tests' { Push-Location tests/NocatFarm.Tests; dotnet run -c Release; Pop-Location }
  Step 'translations' { $env:PYTHONIOENCODING = 'utf-8'; python tools/check-translations.py }
  Step 'dashboard script parses' { node -e "new Function(require('fs').readFileSync('src/NocatFarm/wwwroot/app.js','utf8')); console.log('all clear')" }
  Step 'commands list is current' { python tools/gen-commands.py }
  if ($failed.Count -eq 0) {
    Step 'release files' { powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 }
    Step 'setup' { powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\install.ps1" }
    Step 'moving a running portable copy in' { powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\move-portable.ps1" -From $From }
    Step 'updating itself - portable' { powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\rollback.ps1" -Mode portable }
    Step 'updating itself - installed' { powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\rollback.ps1" -Mode installed }
    Step 'Linux, Docker and Mac on GitHub' {
      $sha = (git rev-parse HEAD).Trim()
      gh workflow run linux.yml --repo VisaHolder/nocatfarm | Out-Null
      gh workflow run macos.yml --repo VisaHolder/nocatfarm | Out-Null
      Start-Sleep 15
      foreach ($w in 'linux.yml', 'macos.yml') {
        # Filtered here, not with gh's -q: Windows PowerShell mangles the quotes a jq filter needs on the way to gh.
        $id = (gh run list --repo VisaHolder/nocatfarm --workflow $w --limit 5 --json databaseId,headSha | ConvertFrom-Json |
          Where-Object { $_.headSha -eq $sha } | Select-Object -First 1).databaseId
        if (-not $id) { Write-Output "FAIL: no $w run found for $sha"; continue }
        gh run watch $id --repo VisaHolder/nocatfarm --exit-status | Out-Null
        (gh run view $id --repo VisaHolder/nocatfarm --json jobs | ConvertFrom-Json).jobs | ForEach-Object { "$($_.name) $($_.conclusion)" }
        gh run view $id --repo VisaHolder/nocatfarm --log | Select-String 'PASS: |FAIL' | ForEach-Object { $_.Line.Substring([Math]::Max(0, $_.Line.IndexOf('PASS')), [Math]::Min(80, $_.Line.Length - [Math]::Max(0, $_.Line.IndexOf('PASS')))) }
      }
    }
  }
} finally { Pop-Location }

if ($failed.Count -gt 0) { Write-Host "`nNOT READY - failed: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "`nALL PASSED - $Version is ready to release" -ForegroundColor Green
