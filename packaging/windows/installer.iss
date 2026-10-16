; Downhole Studio — Windows installer (Inno Setup 6).
; Build with scripts/build.sh (Linux, Inno Setup under Wine in Docker) or scripts/build.ps1 (Windows).
; Defines passed by the build scripts:
;   AppVersion   e.g. 1.0.0
;   PublishDir   folder with the self-contained win-x64 application
;   OutputDir    where DownholeStudio-Setup-<version>.exe is written
;   Sign         (optional) when defined, the installer and uninstaller are Authenticode-signed
;                with the "dhssign" sign tool configured in Inno Setup (see DEPLOYMENT.md).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
; Windows 10 build 19044 (21H2, incl. Enterprise LTSC 2021) or later; 22H2 (19045) and Windows 11 are the target.
#ifndef MinWindows
  #define MinWindows "10.0.19044"
#endif

#define AppName "Downhole Studio"
#define AppExe "DownholeStudio.exe"
#define ProgId "QuakeLogic.DownholeStudio.Project"

[Setup]
AppId={{6B0D5F7C-6F2B-4E3E-9C4B-7E1D2A8D6D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=QuakeLogic Inc.
AppPublisherURL=https://quakelogic.net
AppSupportURL=https://quakelogic.net/contact
AppUpdatesURL=https://quakelogic.net/downhole-studio
AppCopyright=Copyright © 2026 QuakeLogic Inc.
VersionInfoVersion={#AppVersion}
VersionInfoCompany=QuakeLogic Inc.
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
; Per-user installation by default (no administrator rights needed); an administrator can choose all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\QuakeLogic\Downhole Studio
DefaultGroupName=QuakeLogic
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinWindows}
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-150.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-150.bmp,wizard-small-200.bmp
SetupIconFile=DownholeStudio.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=EULA.txt
OutputDir={#OutputDir}
OutputBaseFilename=DownholeStudio-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ChangesAssociations=yes
CloseApplications=yes
; The application holds this mutex while running, so setup and uninstall can detect it.
AppMutex=QuakeLogic.DownholeStudio.Running
RestartApplications=no
ShowLanguageDialog=no
#ifdef Sign
SignTool=dhssign $f
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "association"; Description: "Open .dhsx project files with Downhole Studio"; GroupDescription: "File types:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\samples\Downhole Studio Sample.dhsx"; DestDir: "{app}\Samples"; Flags: ignoreversion
Source: "EULA.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Downhole seismic processing and reporting"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; .dhsx association (per user or per machine, following the installation mode).
Root: HKA; Subkey: "Software\Classes\.dhsx"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: association
Root: HKA; Subkey: "Software\Classes\.dhsx\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: association
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Downhole Studio project"; Flags: uninsdeletekey; Tasks: association
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: association
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: association
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".dhsx"; ValueData: ""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; After a silent update started from the application (which passes /RELAUNCH), start it again.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

[UninstallDelete]
; Only files the application created inside its own installation folder. Projects (Documents\Downhole Studio)
; and user data (%LOCALAPPDATA%\QuakeLogic\Downhole Studio: settings, licence, logs, recovery copies) are never removed.
Type: filesandordirs; Name: "{app}\Samples"

[Code]
function ShouldRelaunch(): Boolean;
begin
  Result := WizardSilent and (Pos('/RELAUNCH', Uppercase(GetCmdTail)) > 0);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent then
    MsgBox('Downhole Studio will be removed. Your projects and settings are kept.', mbInformation, MB_OK);
end;
