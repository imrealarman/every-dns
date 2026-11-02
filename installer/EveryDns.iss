; Inno Setup script for EveryDns. Build with:  ISCC.exe installer\EveryDns.iss
; Expects the self-contained publish output in ..\publish (see build-installer.ps1).

#define AppName "EveryDns"
#define AppVersion "1.0.0"
#define AppExe "EveryDns.exe"

[Setup]
AppId={{C7A41E9B-3D52-4F86-8B0A-6E2D91F5A374}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arman Soltani
AppContact=imrealarman@gmail.com
AppCopyright=Copyright (C) 2026 Arman Soltani
VersionInfoCompany=Arman Soltani
VersionInfoDescription=EveryDns Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; changing DNS asks for permission (UAC) at the moment it is needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=EveryDns-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Settings and the log (%AppData%\EveryDns) are deliberately left in place on uninstall.
