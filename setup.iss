#define AppName "MultiAI Shell"
#define AppExeName "MultiAI"
#define AppVersion "0.0.4"

[Setup]
AppId={{7A6E2C41-9B3D-4E8A-A1F2-3C5D7E9B0A4C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=davidcanar
DefaultDirName={autopf}\MultiAI Shell
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}.exe
OutputDir=bin\setup
OutputBaseFilename=MultiAI-{#AppVersion}-setup-x64
SetupIconFile=MultiAI.ico
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
LicenseFile=LICENSE

[Files]
Source: "bin\publish\MultiAI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}.exe"

[Run]
Filename: "{app}\{#AppExeName}.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
