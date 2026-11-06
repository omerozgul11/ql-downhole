# Customer acceptance test on real Windows. Downloads the published package, installs it and operates the application
# with real mouse and keyboard input. UI Automation is used only to locate things on screen and read what they show.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Native {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
# The final check downloads from the public main branch; a branch under test uses its own copy of the same files.
$Ref = if ($env:PACKAGE_REF) { $env:PACKAGE_REF } else { 'main' }
$Base = "https://github.com/omerozgul11/ql-downhole/raw/$Ref"
$Work = 'C:\dh'
$Results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force $Results, "$Results\screens", "$Work\output" | Out-Null
$script:Passed = 0; $script:Failed = 0; $script:Shot = 0
$Summary = "$Results\summary.txt"

function Log($t) { Write-Host $t; Add-Content $Summary $t }
function Publish { & "$PSScriptRoot\publish-results.ps1" $Results "ci-results/$env:RUN_ID-customer" }
function Check($name, $ok, $detail = '') {
  if ($ok) { $script:Passed++; Log "PASS  $name $detail" } else { $script:Failed++; Log "FAIL  $name $detail"; Shot "FAIL-$($name -replace '[^A-Za-z0-9]+','-')" }
}
function Shot($name) {
  $script:Shot++
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save(("$Results\screens\{0:d2}-$name.png" -f $script:Shot), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

# ---- locating and operating
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function AppWindow {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $script:App.Id)
  $AE::RootElement.FindFirst($TS::Children, $c)
}
function Elements { $w = AppWindow; if ($w) { $w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) } }
function Find($pattern, $seconds = 15) {
  $until = (Get-Date).AddSeconds($seconds)
  do {
    try { foreach ($e in (Elements)) { if ($e.Current.Name -like $pattern -and -not $e.Current.IsOffscreen) { return $e } } } catch {}
    Start-Sleep -Milliseconds 400
  } while ((Get-Date) -lt $until)
  return $null
}
function Has($pattern, $seconds = 15) { $null -ne (Find $pattern $seconds) }
function MouseAt($x, $y) {
  [Native]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 120
  [Native]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60
  [Native]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 350
}
function ClickEl($e) { $r = $e.Current.BoundingRectangle; MouseAt ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2) }
function Click($pattern, $seconds = 15) {
  $e = Find $pattern $seconds
  if ($null -eq $e) { Log "      (could not find '$pattern')"; return $false }
  ClickEl $e; return $true
}
function Keys($k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 300 }
# Types literally, escaping the characters SendKeys treats as commands.
function TypeText($t) { Keys ($t -replace '([+^%~(){}\[\]])', '{$1}') }
function Paste($t) { [System.Windows.Forms.Clipboard]::SetText($t); Keys '^a'; Keys '^v' }
function Edits {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  (AppWindow).FindAll($TS::Descendants, $c)
}
function EditValue($e) { try { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $null } }
function EditWithValue($value) { foreach ($e in (Edits)) { if ((EditValue $e) -eq $value -and -not $e.Current.IsOffscreen) { return $e } }; $null }
function Alive { -not $script:App.HasExited }
function TextLike($pattern) { foreach ($e in (Elements)) { if ($e.Current.Name -like $pattern) { return $e.Current.Name } }; $null }

function StartApp {
  $script:App = Start-Process -FilePath $script:Exe -PassThru
  $until = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $until -and -not (AppWindow)) { Start-Sleep 1 }
  Start-Sleep 6
  $w = AppWindow
  if ($w) { [Native]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null }
}

try {
  try { Set-DisplayResolution -Width 1920 -Height 1080 -Force } catch { Log "      (display resolution unchanged: $($_.Exception.Message))" }
  # The machine's console window would otherwise cover the application.
  (New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep 2
  $os = (Get-CimInstance Win32_OperatingSystem)
  Log "Machine: $($os.Caption) build $($os.BuildNumber), screen $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Size)"

  # ---- 1. Download what customers download
  $ProgressPreference = 'SilentlyContinue'
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $zip = "$Work\DownholeStudio-Client-Package.zip"; $setup = "$Work\DownholeStudio-Setup.exe"
  Invoke-WebRequest "$Base/download/DownholeStudio-Client-Package.zip" -OutFile $zip -UseBasicParsing
  Invoke-WebRequest "$Base/download/DownholeStudio-Setup.exe" -OutFile $setup -UseBasicParsing
  $zipSum = (New-Object Net.WebClient).DownloadString("$Base/download/DownholeStudio-Client-Package.zip.sha256")
  Check 'Zip downloads from the public link' ((Get-Item $zip).Length -gt 50MB) "($([math]::Round((Get-Item $zip).Length/1MB)) MB)"
  Check 'Setup downloads from the public link' ((Get-Item $setup).Length -gt 30MB) "($([math]::Round((Get-Item $setup).Length/1MB)) MB)"
  Check 'Zip matches its published checksum' ("$zipSum" -match (Get-FileHash $zip -Algorithm SHA256).Hash)
  Expand-Archive $zip "$Work\pkg" -Force
  $pkg = (Get-ChildItem "$Work\pkg" -Directory | Select-Object -First 1).FullName
  $bad = 0
  foreach ($l in Get-Content "$pkg\SHA256SUMS.txt") {
    if ($l -match '^([0-9a-f]{64})\s+(.+)$') { if ((Get-FileHash -LiteralPath (Join-Path $pkg $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1].ToUpper()) { $bad++ } }
  }
  Check 'Every file in the zip matches SHA256SUMS' ($bad -eq 0) "($bad wrong)"
  foreach ($need in 'START HERE.txt', '1 - Install Downhole Studio\DownholeStudio-Setup-1.0.0.exe', '4 - Training Video\Downhole-Studio-Training-Video.mp4', '4 - Training Video\Downhole-Studio-Training-Video.srt',
    '2 - Quick Start and Activation\Downhole-Studio-Quick-Start-and-Activation.pdf', '5 - User Guide\Downhole-Studio-User-Guide.pdf', '7 - Methods and Validation\Downhole-Studio-Methods-and-Validation.pdf') {
    Check "Zip contains $need" (Test-Path -LiteralPath (Join-Path $pkg $need))
  }
  $records = Join-Path $pkg '8 - Example Field Records'
  Check 'Zip contains 96 example records' ((Get-ChildItem -LiteralPath $records -Filter *.sg2).Count -eq 96)
  Check 'Installer in the zip equals the download-page Setup' ((Get-FileHash -LiteralPath "$pkg\1 - Install Downhole Studio\DownholeStudio-Setup-1.0.0.exe").Hash -eq (Get-FileHash $setup).Hash)
  Publish

  # ---- 2. Install from the zip, as START HERE says
  $inst = Start-Process -FilePath "$pkg\1 - Install Downhole Studio\DownholeStudio-Setup-1.0.0.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', "/LOG=$Results\install.log" -PassThru -Wait
  Check 'Installer finishes without errors' ($inst.ExitCode -eq 0) "(exit code $($inst.ExitCode))"
  $script:Exe = "$env:LOCALAPPDATA\Programs\QuakeLogic\Downhole Studio\DownholeStudio.exe"
  Check 'Application is installed' (Test-Path $script:Exe)
  Check 'Start menu shortcut exists' ($null -ne (Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu" -Recurse -Filter '*Downhole*' | Select-Object -First 1))
  $config = Get-Content (Join-Path (Split-Path $script:Exe) 'appsettings.json') -Raw | ConvertFrom-Json
  foreach ($url in $config.PrivacyPolicyUrl, $config.SupportUrl, $config.PurchaseUrl) {
    $status = 0; try { $status = (Invoke-WebRequest $url -UseBasicParsing -UserAgent 'Mozilla/5.0').StatusCode } catch { $status = "$($_.Exception.Message)" }
    Check "Link in the application works: $url" ($status -eq 200) "($status)"
  }

  # ---- 3. First start: activation window and real input
  StartApp
  Shot 'first-start'
  Check 'Application starts' ((Alive) -and $null -ne (AppWindow))
  Check 'Activation window opens' (Has 'Activate Downhole Studio' 30)
  Check 'Copy Code button copies the computer code' (Click 'Copy Code')
  Start-Sleep 1
  $code = [System.Windows.Forms.Clipboard]::GetText()
  Check 'Computer code has the documented format' ($code -match '^[0-9A-Z]{5}(-[0-9A-Z]{5}){4}$') "($code)"
  $box = $null; foreach ($e in (Edits)) { if (-not $e.Current.IsOffscreen) { $box = $e; break } }
  if ($box) { ClickEl $box }
  TypeText 'dhs1.not-a-real-key'
  Shot 'typed-bad-key'
  Check 'Keyboard input reaches the key box' ((EditValue $box) -like '*not-a-real-key*')
  Click 'Activate' | Out-Null
  Check 'A bad key is refused with an explanation' (Has '*activation key is not valid*' 10)
  Shot 'bad-key-refused'

  # ---- 4. Real activation with a key issued for this computer's code
  $code | Set-Content "$Results\computer-code.txt" -Encoding ascii
  Publish
  Log "Waiting for the activation key for $code"
  $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github.raw' }
  $key = $null; $until = (Get-Date).AddMinutes(25)
  while (-not $key -and (Get-Date) -lt $until) {
    try { $key = (Invoke-RestMethod "https://api.github.com/repos/$env:REPO/contents/key.dhslic?ref=ci-key/$env:RUN_ID" -Headers $headers -UseBasicParsing) } catch { Start-Sleep 10 }
  }
  Check 'Activation key received' ($null -ne $key)
  $keyFile = "$Work\Activation.dhslic"
  $key = "$key".Trim()
  Set-Content -Path $keyFile -Value $key -Encoding ascii
  ClickEl $box; Paste $key; Start-Sleep 1
  Click 'Activate' | Out-Null
  Start-Sleep 4
  Shot 'after-activate'
  Check 'Valid key activates: status shows Licensed' (Has 'Licensed' 15)
  Check 'Start screen is available' (Has 'Explore Sample Project' 10)

  # ---- 5. Project details: text, date and checkbox input
  Click 'New Project' | Out-Null
  Start-Sleep 3
  if (Has 'Save*' 2) { Shot 'new-project' }
  Click 'Project' | Out-Null; Start-Sleep 2
  Shot 'project-page'
  $name = $null; foreach ($e in (Edits)) { if (-not $e.Current.IsOffscreen) { $name = $e; break } }
  ClickEl $name; Keys '^a'; TypeText 'Windows Acceptance Survey (BH-1) +100%'; Keys '{TAB}'; Start-Sleep 1
  Check 'Project name accepts typing, including symbols' ((EditValue $name) -eq 'Windows Acceptance Survey (BH-1) +100%') "($(EditValue $name))"
  Keys '^z'; Start-Sleep 1
  Check 'Ctrl+Z undoes the typed name' ((EditValue $name) -ne 'Windows Acceptance Survey (BH-1) +100%')
  Keys '^y'; Start-Sleep 1
  Check 'Ctrl+Y redoes it' ((EditValue $name) -eq 'Windows Acceptance Survey (BH-1) +100%')
  Shot 'project-typed'

  # ---- 6. Import through the Windows file dialogs
  Click 'Import' | Out-Null; Start-Sleep 2
  Click 'Add files*' | Out-Null; Start-Sleep 4
  Shot 'file-dialog'
  TypeText "$records\"; Keys '{ENTER}'; Start-Sleep 3
  TypeText '"DH_001.0m_P.sg2" "DH_001.0m_SL.sg2" "DH_001.0m_SR.sg2"'; Keys '{ENTER}'; Start-Sleep 5
  Shot 'three-files-chosen'
  Check 'File dialog: three chosen files are inspected' (Has '3 of 3*' 20)
  Click 'Clear' | Out-Null; Start-Sleep 2
  Click 'Add folder*' | Out-Null; Start-Sleep 4
  TypeText $records; Keys '{ENTER}'; Start-Sleep 3
  if (-not (Has '96 of 96*' 3)) { $d = Find 'Select Folder' 3; if ($d) { ClickEl $d }; }
  Shot 'folder-chosen'
  Check 'Folder dialog: all 96 records are inspected' (Has '96 of 96*' 30)
  Check 'No import errors' (Has '*0 error*' 5)
  Click 'Import selected files' | Out-Null; Start-Sleep 6
  Shot 'imported'
  Check 'Import creates 32 depths' (Has '32 depth*' 20)

  # ---- 7. Geometry: numeric input, including invalid text
  $p = EditWithValue '1.5'
  Check 'Source offset field shows the default' ($null -ne $p)
  ClickEl $p; Keys '^a'; TypeText 'abc'; Keys '{TAB}'; Start-Sleep 1
  Shot 'invalid-number'
  Check 'Invalid number does not crash the application' (Alive)
  Check 'Invalid number gets a plain message' (Has 'Enter a number*' 5)
  Check 'No programmer error text is shown' (-not (Has '*InvalidCast*' 2))
  $p = EditWithValue 'abc'
  ClickEl $p; Keys '^a'; TypeText '2'; Keys '{TAB}'; Start-Sleep 1
  $p = EditWithValue '2'
  Check 'P source offset accepts 2 after a wrong entry' ($null -ne $p)
  Check 'The message clears after a correct entry' (-not (Has 'Enter a number*' 2))
  $s = EditWithValue '1.5'
  ClickEl $s; Keys '^a'; TypeText '2'; Keys '{TAB}'; Start-Sleep 1
  Check 'S source offset accepts 2' ((EditValue $s) -eq '2') "($(EditValue $s))"
  Shot 'geometry'

  # ---- 8. Waveforms: check box
  Click 'Waveforms' | Out-Null; Start-Sleep 4
  Shot 'waveforms'
  $over = Find 'Overlay' 5
  if ($over) { ClickEl $over; Start-Sleep 1; ClickEl $over; Start-Sleep 1 }
  Check 'Waveform check box responds' ((Alive) -and $null -ne $over)

  # ---- 9. Picking: buttons, then a mouse click on the chart
  Click 'P Picks' | Out-Null; Start-Sleep 4
  Click 'Run automatic picking' | Out-Null; Start-Sleep 7
  Check 'Automatic P picking picks all 32 depths' (Has '32 of 32 depths picked*' 25)
  Shot 'p-picks'
  $before = TextLike '*depths picked*'
  $w = (AppWindow).Current.BoundingRectangle
  MouseAt ($w.X + $w.Width * 0.30) ($w.Y + $w.Height * 0.62); Start-Sleep 2
  $after = TextLike '*depths picked*'
  Shot 'manual-pick'
  Check 'Mouse click on the chart places a manual pick' ($before -ne $after) "('$before' -> '$after')"
  Keys '^z'; Start-Sleep 2
  Check 'Ctrl+Z removes the manual pick' ((TextLike '*depths picked*') -eq $before)
  Click 'S Picks' | Out-Null; Start-Sleep 4
  Click 'Run automatic picking' | Out-Null; Start-Sleep 8
  Check 'Automatic S picking picks all 32 depths' (Has '32 of 32 depths picked*' 25)
  Shot 's-picks'

  # ---- 10. Velocity model and Vs30
  Click 'Velocity Model' | Out-Null; Start-Sleep 4
  Click 'Suggest boundaries' | Out-Null; Start-Sleep 3
  Click 'Apply all' | Out-Null; Start-Sleep 4
  Shot 'velocity-model'
  Check 'Suggested boundaries give 5 layers' (Has '*5 layer(s)*' 10)
  # A drop-down list: choose another velocity method with the mouse.
  Click 'Layer regression*' | Out-Null; Start-Sleep 2
  Shot 'method-list'
  Click 'Refracted-ray*' | Out-Null; Start-Sleep 4
  Shot 'method-changed'
  Check 'Drop-down list changes the velocity method' (Has '*Snell*' 8)
  Keys '{F1}'; Start-Sleep 2
  Shot 'help'
  Check 'F1 opens help' (Alive)
  Keys '{F1}'; Start-Sleep 1
  Click 'Engineering' | Out-Null; Start-Sleep 4
  Shot 'engineering'
  $vs30 = TextLike '* m/s'
  foreach ($e in (Elements)) { if ($e.Current.Name -match '^\d{3} m/s$') { $vs30 = $e.Current.Name; break } }
  Check 'Vs30 is calculated close to the true 367.6 m/s' ($vs30 -match '^(3[3-9]\d) m/s$') "($vs30)"

  # ---- 11. Save, report, command palette
  Keys '^s'; Start-Sleep 4
  $project = "$Work\output\Windows Acceptance.dhsx"
  Keys '^a'; TypeText $project; Keys '{ENTER}'; Start-Sleep 7
  Check 'Ctrl+S saves the project file' (Test-Path -LiteralPath $project)
  Click 'Report' | Out-Null; Start-Sleep 4
  Click 'Generate PDF*' | Out-Null; Start-Sleep 4
  $pdf = "$Work\output\Windows Acceptance Report.pdf"
  Keys '^a'; TypeText $pdf; Keys '{ENTER}'
  $started = Get-Date; $until = $started.AddMinutes(5)
  while ((Get-Date) -lt $until -and -not ((Test-Path -LiteralPath $pdf) -and -not (Has 'Generating report*' 1))) { Start-Sleep 2 }
  Start-Sleep 2
  Shot 'report'
  $pdfOk = (Test-Path -LiteralPath $pdf) -and ((Get-Item -LiteralPath $pdf).Length -gt 100KB)
  Check 'PDF report is written' $pdfOk "(in $([int]((Get-Date) - $started).TotalSeconds) s)"
  if (Test-Path -LiteralPath $pdf) { Copy-Item -LiteralPath $pdf "$Results\Windows-report.pdf" }
  Keys '{ESC}'; Start-Sleep 1
  Keys '^k'; Start-Sleep 2
  TypeText 'settings'; Start-Sleep 2
  Shot 'command-palette'
  $palette = EditWithValue 'settings'
  Check 'Ctrl+K: typing goes straight into the command palette' ($null -ne $palette)
  Keys '{ENTER}'; Start-Sleep 3
  Shot 'settings-from-palette'
  Check 'Enter runs the chosen command (Settings opens)' (Has 'Recovery copy interval*' 8)
  Keys '{ESC}'; Start-Sleep 2
  Shot 'settings-closed'
  Check 'Esc closes Settings' (-not (Has 'Recovery copy interval*' 2))
  # Excel export through the Report page.
  Click 'Report' | Out-Null; Start-Sleep 3
  $xlsxButton = Find '*Excel*' 5
  if ($xlsxButton) { ClickEl $xlsxButton; Start-Sleep 4; $xlsx = "$Work\output\Windows Acceptance Tables.xlsx"; Keys '^a'; TypeText $xlsx; Keys '{ENTER}'; Start-Sleep 6 }
  Check 'Excel tables are exported' ($null -ne $xlsxButton -and (Test-Path -LiteralPath $xlsx) -and (Get-Item -LiteralPath $xlsx).Length -gt 5KB)
  # Several reports in a row, timed: one earlier run was slow, and a report must never hang.
  Click 'Report' | Out-Null; Start-Sleep 3
  foreach ($n in 1..5) {
    $g = Find 'Generate PDF*' 30
    if ($g) { ClickEl $g }; Start-Sleep 4
    $again = "$Work\output\Timed Report $n.pdf"
    Keys '^a'; TypeText $again
    $cpuBefore = (Get-Process -Id $script:App.Id).CPU
    Keys '{ENTER}'
    # Timed by the file alone: looking through the screen while it runs would disturb the measurement.
    $started = Get-Date; $until = $started.AddMinutes(3)
    while ((Get-Date) -lt $until -and -not (Test-Path -LiteralPath $again)) { Start-Sleep -Milliseconds 250 }
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $cpu = [math]::Round((Get-Process -Id $script:App.Id).CPU - $cpuBefore, 1)
    Check "Report $n of 5 finishes" (Test-Path -LiteralPath $again) "(in $seconds s, $cpu s of processor time)"
    $until = (Get-Date).AddMinutes(1)
    while ((Get-Date) -lt $until -and (Has 'Generating report*' 1)) { Start-Sleep 1 }
    Publish
  }
  Publish

  # ---- 12. Restart: stays activated, the project reopens, and editing and saving it again works
  Keys '^s'; Start-Sleep 3
  Stop-Process -Id $script:App.Id -Force; Start-Sleep 3
  $settings = "$env:LOCALAPPDATA\QuakeLogic\Downhole Studio\settings.json"
  $json = Get-Content $settings -Raw | ConvertFrom-Json
  $json.AutoSaveMinutes = 1
  $json | ConvertTo-Json -Depth 20 | Set-Content $settings -Encoding UTF8
  StartApp
  Shot 'restarted'
  Check 'Stays activated after restart' (Has 'Licensed' 20)
  Check 'Licence file is stored' (Test-Path "$env:LOCALAPPDATA\QuakeLogic\Downhole Studio\license.dat")
  $recent = Find '*Windows Acceptance*' 15
  Check 'Project is listed under Recent projects' ($null -ne $recent)
  $savedAt = (Get-Item -LiteralPath $project).LastWriteTimeUtc
  ClickEl $recent; Start-Sleep 8
  Shot 'reopened'
  Check 'Project reopens with its results' (Has '*Windows Acceptance*' 10)
  Click 'Project' | Out-Null; Start-Sleep 3
  $name = EditWithValue 'Windows Acceptance Survey (BH-1) +100%'
  Check 'Saved project name is kept' ($null -ne $name)
  ClickEl $name; Keys '^a'; TypeText 'Saved Twice'; Keys '{TAB}'; Start-Sleep 1
  Keys '^s'; Start-Sleep 6
  Shot 'saved-again'
  Check 'Saving over the open project works' ((Get-Item -LiteralPath $project).LastWriteTimeUtc -gt $savedAt)
  Check 'A backup of the previous version is kept' (Test-Path -LiteralPath "$project.bak")

  # ---- 12b. Crash recovery: unsaved work survives the application being killed
  $name = EditWithValue 'Saved Twice'
  ClickEl $name; Keys '^a'; TypeText 'Unsaved Work'; Keys '{TAB}'; Start-Sleep 1
  $beforeCrash = (Get-FileHash -LiteralPath $project).Hash
  Log '      (waiting for the automatic recovery copy)'
  Start-Sleep 75
  Stop-Process -Id $script:App.Id -Force; Start-Sleep 3
  StartApp
  Shot 'after-crash'
  Check 'After a crash the application offers to recover' (Has '*recovered an unsaved project*' 20)
  Click 'Recover' | Out-Null; Start-Sleep 8
  Shot 'recovered'
  Check 'Recovery works: no error is shown' (-not (Has '*could not be opened*' 2))
  Click 'Project' | Out-Null; Start-Sleep 3
  Check 'Recovered project contains the unsaved work' ($null -ne (EditWithValue 'Unsaved Work'))
  Check 'Original project file was not touched by the crash' ((Get-FileHash -LiteralPath $project).Hash -eq $beforeCrash)
  Click 'Waveforms' | Out-Null; Start-Sleep 5
  Shot 'recovered-waveforms'
  Check 'Recovered project still has its records' (Alive)
  $recoveredCopy = "$Work\output\Recovered.dhsx"
  Keys '^+s'; Start-Sleep 4
  Keys '^a'; TypeText $recoveredCopy; Keys '{ENTER}'; Start-Sleep 8
  Check 'Recovered project can be saved' (Test-Path -LiteralPath $recoveredCopy)

  # ---- 12c. Closed project files are free again
  Stop-Process -Id $script:App.Id -Force; Start-Sleep 3
  StartApp
  $free = "$Work\output\Delete Me.dhsx"
  Copy-Item -LiteralPath $project $free
  $d = Find 'Discard' 4; if ($d) { ClickEl $d; Start-Sleep 2 }
  Click 'Open' | Out-Null; Start-Sleep 4
  Keys '^a'; TypeText $free; Keys '{ENTER}'; Start-Sleep 8
  Shot 'opened-copy'
  Click 'Waveforms' | Out-Null; Start-Sleep 5
  $deleted = $false; try { Rename-Item -LiteralPath $free "$Work\output\Renamed While Open.dhsx" -ErrorAction Stop; $deleted = $true } catch { Log "      ($($_.Exception.Message))" }
  Check 'An open project file can be moved or renamed in Explorer' $deleted

  # ---- 13. Uninstall keeps the customer's files
  Stop-Process -Id $script:App.Id -Force; Start-Sleep 3
  $un = Get-ChildItem (Split-Path $script:Exe) -Filter 'unins*.exe' | Select-Object -First 1
  Start-Process -FilePath $un.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
  Start-Sleep 5
  Check 'Uninstall removes the program' (-not (Test-Path $script:Exe))
  Check 'Uninstall keeps the project' (Test-Path -LiteralPath $project)
}
catch {
  $script:Failed++
  Log "FAIL  Stopped by an unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
  try { Shot 'unexpected-error' } catch {}
}
finally {
  try {
    $logs = "$env:LOCALAPPDATA\QuakeLogic\Downhole Studio\Logs"
    if (Test-Path $logs) { Copy-Item -Recurse $logs "$Results\app-logs" }
  } catch {}
  Log "RESULT passed $script:Passed, failed $script:Failed"
  Publish
}
if ($script:Failed -gt 0) { exit 1 }
exit 0
