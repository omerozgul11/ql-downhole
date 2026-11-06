# Runs one test suite and fails on any failed or skipped test, or if no tests ran.
param([string]$Project)
$ErrorActionPreference = 'Continue'
$dir = 'artifacts/suite-results'
New-Item -ItemType Directory -Force $dir | Out-Null
$log = "$dir/$Project.txt"
$project_path = "tests/$Project"
# Through cmd: Windows PowerShell would treat any line on stderr as a failure of this script.
cmd /c "dotnet test $project_path -c Release --no-build > `"$log`" 2>&1"
Get-Content $log
$line = Select-String -Path $log -Pattern '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)' | Select-Object -Last 1
if (-not $line) { Write-Host "No test summary found for $Project"; exit 1 }
$m = $line.Matches[0]
"$Project`: failed $($m.Groups[2].Value), passed $($m.Groups[3].Value), skipped $($m.Groups[4].Value), total $($m.Groups[5].Value)" | Add-Content "$dir/summary.txt"
if ([int]$m.Groups[2].Value -ne 0 -or [int]$m.Groups[4].Value -ne 0 -or [int]$m.Groups[5].Value -eq 0) { exit 1 }
exit 0
