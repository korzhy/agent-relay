#define MyAppName "Agent Relay"
#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\outputs"
#endif

[Setup]
AppId={{CE4E80FC-E915-4930-9202-15EA5FC555A5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Agent Relay contributors
DefaultDirName={localappdata}\Programs\AgentRelay
DefaultGroupName=Agent Relay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=AgentRelaySetup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\AgentRelay.exe
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany=Agent Relay contributors
LicenseFile=..\LICENSE
SetupLogging=yes
CloseApplications=force
CloseApplicationsFilter=AgentRelay.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Agent Relay"; Filename: "{app}\AgentRelay.exe"
Name: "{userdesktop}\Agent Relay"; Filename: "{app}\AgentRelay.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\AgentRelay.exe"; Parameters: "codex install"; StatusMsg: "Installing managed Codex integration..."; Flags: runhidden waituntilterminated; Check: ShouldInstallCodex
Filename: "{app}\AgentRelay.exe"; Flags: nowait runasoriginaluser; Check: IsAutoUpdate
Filename: "{app}\AgentRelay.exe"; Description: "{cm:LaunchProgram,Agent Relay}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\AgentRelay.exe"; Parameters: "codex remove"; RunOnceId: "AgentRelayCodexRemove"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AgentRelay\updates"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := IsWin64;
  if not Result then
    MsgBox('Agent Relay requires Windows 10/11 x64.', mbError, MB_OK);
end;

function ShouldInstallCodex(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:NOCODEXINTEGRATION|0}'), '1') <> 0;
end;

function IsAutoUpdate(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:AUTOUPDATE|0}'), '1') = 0;
end;
