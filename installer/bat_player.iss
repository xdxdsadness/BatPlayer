; =====================================================================
; Bat Player - Inno Setup Installer Script
; Requires: Inno Setup 6.2+ (https://jrsoftware.org/isinfo.php)
;
; Build steps:
;   1. Publish the app:
;        dotnet publish src/BatPlayer/BatPlayer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
;   2. Generate icon (if missing):
;        python scripts/generate_icon.py
;   3. Compile installer:
;        iscc installer/bat_player.iss
; =====================================================================

#define MyAppName          "Bat Player"
#define MyAppVersion       "1.0.0"
#define MyAppPublisher     "Bat Player"
#define MyAppURL           "https://example.com/bat-player"
#define MyAppExeName       "BatPlayer.exe"

[Setup]
AppId={{A1B2C3D4-1111-2222-3333-444455556666}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=BatPlayer-Setup-{#MyAppVersion}
SetupIconFile=..\src\BatPlayer\Resources\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
MinVersion=10.0.19041
CloseApplications=force

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Запускать вместе с Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\BatPlayer\Resources\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\app.ico"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup; IconFilename: "{app}\app.ico"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
; Keep user data by default — comment out to enable clean uninstall
; Type: filesandordirs; Name: "{localappdata}\BatPlayer"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
