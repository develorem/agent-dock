; Agent Dock — Inno Setup Script
; Build with: ISCC.exe /DMyAppVersion=X.Y.Z installer\AgentDock.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Agent Dock"
#define MyAppExeName "AgentDock.exe"
#define MyAppPublisher "Develorem"
#define MyAppURL "https://github.com/develorem/agent-dock"

[Setup]
AppId={{7A4E3B2D-1F8C-4D6A-9E5B-0C2D3F4A5B6E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=AgentDock-{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
SetupIconFile=..\assets\agentdock.ico
UninstallDisplayIcon={app}\agentdock.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Associate .agentdock files with {#MyAppName}"; GroupDescription: "File associations:"; Flags: checkedonce

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\agentdock.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; File association: .agentdock → AgentDock.Workspace
Root: HKA; Subkey: "Software\Classes\.agentdock"; ValueType: string; ValueName: ""; ValueData: "AgentDock.Workspace"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\AgentDock.Workspace"; ValueType: string; ValueName: ""; ValueData: "Agent Dock Workspace"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\AgentDock.Workspace\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\agentdock.ico,0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\AgentDock.Workspace\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
