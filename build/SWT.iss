#ifndef AppVersion
  #error AppVersion must be supplied by Package.ps1
#endif

[Setup]
AppId={{49A0B028-683D-44BC-A3BA-9A834F2F9A76}
AppName=SWT
AppVersion={#AppVersion}
AppPublisher=AngelJumbo
AppPublisherURL=https://github.com/AngelJumbo/SWT
AppSupportURL=https://github.com/AngelJumbo/SWT/issues
DefaultDirName={localappdata}\Programs\SWT
DefaultGroupName=SWT
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=SWT-{#AppVersion}-win-x64-setup
VersionInfoVersion={#NumericVersion}.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\SWT.Desktop.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SWT"; Filename: "{app}\SWT.Desktop.exe"
Name: "{userdesktop}\SWT"; Filename: "{app}\SWT.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SWT.Desktop.exe"; Description: "Launch SWT"; Flags: nowait postinstall skipifsilent
