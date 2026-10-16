<#
.SYNOPSIS
  Builds, tests, publishes and packages Downhole Studio on Windows, optionally Authenticode-signing it.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Version 1.0.0 -Sign
  (the certificate thumbprint or sign command is read from .env; see .env.example)
.NOTES
  Requires the .NET SDK from global.json, Inno Setup 6 (ISCC.exe on PATH or in its default folder) and,
  for -Sign, the Windows SDK signtool.exe with the code-signing certificate in the user's certificate store
  (or a cloud HSM signing tool configured in DHS_SIGN_COMMAND). Private keys are never stored in the repository.
#>
param(
  [string]$Version = "",
  [switch]$SkipTests,
  [switch]$Sign,
  [string]$CertificateThumbprint = "",
  [string]$TimestampUrl = ""
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
# Local, git-ignored settings (see .env.example); variables already set in the environment win.
if (Test-Path .env) {
  foreach ($line in Get-Content .env) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$' -and -not [Environment]::GetEnvironmentVariable($Matches[1])) {
      [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].Trim().Trim('"'))
    }
  }
}
if (-not $CertificateThumbprint) { $CertificateThumbprint = $env:DHS_CERT_THUMBPRINT }
if (-not $TimestampUrl) { $TimestampUrl = if ($env:DHS_TIMESTAMP_URL) { $env:DHS_TIMESTAMP_URL } else { "http://timestamp.digicert.com" } }
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.VersionPrefix }
Write-Host "== Downhole Studio $Version"

dotnet build DownholeStudio.sln -c Release -p:Version=$Version
if ($LASTEXITCODE) { throw "Build failed" }
if (-not $SkipTests) {
  foreach ($t in "SignalProcessing","Formats","Analysis","Core","Licensing") {
    dotnet test "tests/DownholeStudio.$t.Tests" -c Release --no-build
    if ($LASTEXITCODE) { throw "Tests failed: $t" }
  }
  dotnet test tests/DownholeStudio.UiTests -c Release --no-build
  if ($LASTEXITCODE) { throw "UI tests failed" }
}
if (-not (Test-Path "packaging/samples/Downhole Studio Sample.dhsx")) {
  New-Item -ItemType Directory -Force packaging/samples | Out-Null
  dotnet run --project tools/DownholeStudio.Cli -c Release --no-build -- sample --out "packaging/samples/Downhole Studio Sample.dhsx"
}

$Publish = "artifacts/publish/win-x64"
if (Test-Path $Publish) { Remove-Item -Recurse -Force $Publish }
dotnet publish src/DownholeStudio.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o $Publish
if ($LASTEXITCODE) { throw "Publish failed" }
Get-ChildItem $Publish -Filter *.pdb -Recurse | Remove-Item

function Invoke-Sign([string]$File) {
  if ($env:DHS_SIGN_COMMAND) { & cmd /c ($env:DHS_SIGN_COMMAND -f $File); if ($LASTEXITCODE) { throw "Signing failed: $File" }; return }
  if (-not $CertificateThumbprint) { throw "Set -CertificateThumbprint or DHS_SIGN_COMMAND to sign." }
  & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d "Downhole Studio" $File
  if ($LASTEXITCODE) { throw "Signing failed: $File" }
}

if ($Sign) {
  Get-ChildItem $Publish -Include DownholeStudio.exe,DownholeStudio.dll,DownholeStudio.*.dll -Recurse | ForEach-Object { Invoke-Sign $_.FullName }
}

$Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $Iscc) { $Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
$Defines = @("/DAppVersion=$Version")
if ($Sign) {
  $SignCmd = if ($env:DHS_SIGN_COMMAND) { $env:DHS_SIGN_COMMAND } else { "signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d `$qDownhole Studio`$q `$f" }
  $Defines += "/DSign"; $Defines += "/Sdhssign=$SignCmd"
}
& $Iscc @Defines packaging/windows/installer.iss
if ($LASTEXITCODE) { throw "Installer build failed" }
$Setup = "artifacts/installer/DownholeStudio-Setup-$Version.exe"
if ($Sign) { & signtool verify /pa $Setup; if ($LASTEXITCODE) { throw "Signature verification failed" } }
$Hash = (Get-FileHash $Setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$Hash  DownholeStudio-Setup-$Version.exe" | Set-Content "$Setup.sha256"
Write-Host "== Installer: $Setup  SHA-256 $Hash"
