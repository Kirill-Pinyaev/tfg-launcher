#define AppVersion "1.4.0"

[Setup]
AppId={{C9DBD20A-A7A0-43E9-889B-D10962E2CC9A}
AppName=TFG Launcher
AppVersion={#AppVersion}
AppPublisher=TFG Server
DefaultDirName={localappdata}\TFGLauncher\app
DefaultGroupName=TFG Launcher
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=TFG-Launcher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\TFG Launcher.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\bin\publish\TFG Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\TFG Launcher"; Filename: "{app}\TFG Launcher.exe"
Name: "{userdesktop}\TFG Launcher"; Filename: "{app}\TFG Launcher.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Run]
Filename: "{app}\TFG Launcher.exe"; Description: "Запустить TFG Launcher"; Flags: nowait postinstall skipifsilent
Filename: "{app}\TFG Launcher.exe"; Flags: nowait; Check: IsSelfUpdate

[Code]
function IsSelfUpdate: Boolean;
begin
  Result := ExpandConstant('{param:TFGSELFUPDATE|0}') = '1';
end;
