; Inno Setup script for Window Invert.
;
; Compiled by build\release.ps1, which passes the version in from the .csproj:
;   ISCC.exe /DAppVersion=0.1.0 installer\WindowInvert.iss
; The version is deliberately not written here, so there is one copy of it.
;
; Per-user install: no elevation prompt, and it lands beside the HKCU Run key
; the app uses for "Start with Windows".

#ifndef AppVersion
  #error Pass the version with /DAppVersion=x.y.z (build\release.ps1 does this)
#endif

#define AppName "Window Invert"
#define AppExe "WindowInvert.App.exe"
#define AppPublisher "Ryan Robson"
#define AppUrl "https://github.com/robworks-code/window-invert"

[Setup]
; Fixed for the life of the product. Changing it would make a later version
; install beside the earlier one instead of replacing it.
AppId={{156ACB7C-F4C6-4E99-81A3-3C0C520923D2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
VersionInfoVersion={#AppVersion}

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

LicenseFile=..\LICENSE
SetupIconFile=..\assets\WindowInvert.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

OutputDir=..\dist
OutputBaseFilename=WindowInvert-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; The app has no main window for Restart Manager to find, so a running copy is
; stopped explicitly in [Code] instead of through the close-applications page.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when you sign in"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Invert the colors of individual windows"

[Registry]
; Written only when the task is ticked. The value name and quoting match what
; the app itself writes from its "Start with Windows" menu item.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WindowInvert"; ValueData: """{app}\{#AppExe}"""; Tasks: startup
; Never created by install (ValueType none), always removed by uninstall, so an
; uninstalled app does not leave logon trying to launch a missing exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WindowInvert"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningApp;
  Result := True;
end;
