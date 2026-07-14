; K2Setup.iss - Inno Setup script for K2.
;
; Do NOT run ISCC on this file directly: it expects the self-contained
; publish output already staged under Installer\publish\K2.App by
; build-installer.bat (that script runs the dotnet publish steps first,
; then invokes ISCC on this file). Everything referenced here lives
; inside K2\, except the top-level LICENSE, which build-installer.bat
; leaves in place one level up (see project layout in _PROJECT_MAP.md).

#define MyAppName "K2"
#define MyAppVersion "1.0.0"
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

[Components]
Name: "main"; Description: "K2 (MacroPad + Everest + Makalu + DisplayPad, unified app)"; Types: full compact custom; Flags: fixed
Name: "displaypad"; Description: "K2 DisplayPad standalone (separate x64 app - only needed if you don't want the unified K2 app)"; Types: full

; Staged by build-installer.bat: publish\K2.App\ is the full install tree for
; the "main" component (K2.App.exe + its Satellite\ helper), with the
; standalone DisplayPad publish nested under DisplayPad\ for the optional
; component.
[Files]
Source: "publish\K2.App\*"; DestDir: "{app}"; Excludes: "DisplayPad\*"; Flags: recursesubdirs ignoreversion; Components: main
Source: "publish\K2.App\DisplayPad\*"; DestDir: "{app}\DisplayPad"; Flags: recursesubdirs ignoreversion; Components: displaypad

[Icons]
Name: "{group}\K2"; Filename: "{app}\{#MyAppExeName}"; Components: main
Name: "{group}\K2 DisplayPad (standalone)"; Filename: "{app}\DisplayPad\K2.DisplayPad.exe"; Components: displaypad
Name: "{group}\Uninstall K2"; Filename: "{uninstallexe}"
Name: "{autodesktop}\K2"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Components: main

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,K2}"; Flags: nowait postinstall skipifsilent; Components: main
