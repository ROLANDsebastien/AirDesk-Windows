#define AppName "AirDesk Companion"
#define AppExe "AirDeskCompanion.exe"
#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif

[Setup]
AppId=AirDeskCompanion.Windows
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=AirDesk
AppPublisherURL=https://github.com/ROLANDsebastien/AirDesk-Windows
DefaultDirName={localappdata}\Programs\AirDesk Companion
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=Local\AirDeskCompanion.Windows
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\AirDesk.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=AirDesk-Companion-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

Source: "browser-host.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\extension\*"; DestDir: "{app}\Extension Edge"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AirDeskCompanion"; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.airdesk.companion"; ValueType: string; ValueData: "{app}\browser-host.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.airdesk.companion"; ValueType: string; ValueData: "{app}\browser-host.json"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Ouvrir AirDesk Companion"; Flags: nowait postinstall skipifsilent
