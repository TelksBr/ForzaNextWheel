#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\release\app"
#endif
#ifndef OutputPath
  #define OutputPath "..\artifacts\release"
#endif
[Setup]
AppId={{6C03E3BF-5C03-46DB-A24D-A53DB2E284F2}
AppName=ForzaNext Wheel
AppVersion={#AppVersion}
AppPublisher=ForzaNext
DefaultDirName={localappdata}\Programs\ForzaNextWheel
DefaultGroupName=ForzaNext Wheel
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputPath}
OutputBaseFilename=ForzaNextWheel-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
UninstallDisplayIcon={app}\ForzaNext.UI.exe
CloseApplications=no
RestartApplications=no
AppMutex=Local\ForzaNextWheel.UI
SetupLogging=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\ForzaNext Wheel"; Filename: "{app}\ForzaNext.UI.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\ForzaNext Wheel"; Filename: "{app}\ForzaNext.UI.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\ForzaNext.UI.exe"; Description: "Abrir ForzaNext Wheel"; Flags: nowait postinstall skipifsilent
; User profiles and shared vJoy drivers survive app uninstall/update.
