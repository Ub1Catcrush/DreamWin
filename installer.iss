[Setup]
AppName=DreamWin
AppVersion={#AppVersion}
DefaultDirName={autopf}\DreamWin
DefaultGroupName=DreamWin
UninstallDisplayIcon={app}\DreamWin.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.\installer\Output
OutputBaseFilename=DreamWinSetup

[Files]
; "ignoreversion" stellt sicher, dass die alte .exe immer überschrieben wird
Source: ".\build\release\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\DreamWin"; Filename: "{app}\DreamWin.exe"
Name: "{autodesktop}\DreamWin"; Filename: "{app}\DreamWin.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\DreamWin.exe"; Description: "{cm:LaunchProgram,DreamWin}"; Flags: nowait postinstall skipifsilent
