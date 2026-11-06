# Publishes a results folder to its own branch so they can be read without a GitHub login.
param([string]$Folder, [string]$Branch)
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $Folder)) { New-Item -ItemType Directory -Force $Folder | Out-Null; 'no results' | Set-Content "$Folder/empty.txt" }
$work = Join-Path $env:RUNNER_TEMP ("publish-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
Copy-Item -Recurse -Force "$Folder/*" $work
Push-Location $work
git init -q -b $Branch 2>&1 | Out-Null
git config user.name 'Windows tests'
git config user.email 'actions@users.noreply.github.com'
git add -A 2>&1 | Out-Null
git commit -q -m "Windows test results" 2>&1 | Out-Null
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { $env:REPO }
git push -q -f "https://x-access-token:$env:GH_TOKEN@github.com/$repo.git" "HEAD:refs/heads/$Branch" 2>&1 | Out-Null
Pop-Location
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
