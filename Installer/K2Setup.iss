; K2Setup.iss - Inno Setup script for K2.
;
; Do NOT run ISCC on this file directly: it expects the self-contained
; publish output already staged under Installer\publish\K2.App by
; build-installer.bat (that script runs the dotnet publish steps first,
; then invokes ISCC on this file). Everything referenced here lives
; inside K2\, except the top-level LICENSE, which build-installer.bat
; leaves in place one level up (see project layout in _PROJECT_MAP.md).

#define MyAppName "K2"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "K2 Project (community, non-commercial)"
#define MyAppExeName "K2.App.exe"

[Setup]
AppId={{5C6A9E2A-6C6F-4C7B-9C64-1B7B6C7C7A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\K2
DefaultGroupName=K2
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=K2-Setup-{#MyAppVersion}
SetupIconFile=..\K2.App\Assets\K2_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Staged by build-installer.bat: publish\K2.App\ is the full install tree,
; K2.App.exe + its Satellite\ helper + the standalone DisplayPad publish
; nested under DisplayPad\ - all installed unconditionally.
[Files]
Source: "publish\K2.App\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\K2"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall K2"; Filename: "{uninstallexe}"
Name: "{autodesktop}\K2"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; shellexec (not the default CreateProcess) is required here: K2.App.exe's
; manifest is requireAdministrator, and CreateProcess cannot elevate a child
; process on its own — it fails with "CreateProcess failed; code 740" even
; when the current user is an administrator. ShellExecute knows how to
; trigger the UAC elevation dance for a manifested-elevated target.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,K2}"; Flags: nowait postinstall skipifsilent shellexec
