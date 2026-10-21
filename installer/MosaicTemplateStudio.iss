#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef InstallerOutput
  #define InstallerOutput "..\artifacts\installer"
#endif

[Setup]
AppId={{7B8E9EF4-E4D4-46F5-907C-A4DC3970D527}
AppName=Mosaic Template Studio
AppVersion={#AppVersion}
AppPublisher=Mosaic Template Studio
DefaultDirName={localappdata}\Programs\Mosaic Template Studio
DefaultGroupName=Mosaic Template Studio
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#InstallerOutput}
OutputBaseFilename=MosaicTemplateStudio-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Mosaic.App\Assets\app.ico
UninstallDisplayIcon={app}\MosaicTemplateStudio.exe
DisableProgramGroupPage=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Mosaic Template Studio offline installer
AppMutex=MosaicTemplateStudio.App

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
polish.DesktopShortcut=Utwórz skrót na pulpicie
english.AssociateProjects=Open Mosaic projects with Mosaic Template Studio
polish.AssociateProjects=Otwieraj projekty Mosaic w programie Mosaic Template Studio
english.LaunchApplication=Open Mosaic Template Studio
polish.LaunchApplication=Otwórz Mosaic Template Studio
english.ProjectDescription=Mosaic Template Studio project
polish.ProjectDescription=Projekt Mosaic Template Studio

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: checkedonce
Name: "association"; Description: "{cm:AssociateProjects}"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mosaic Template Studio"; Filename: "{app}\MosaicTemplateStudio.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mosaic Template Studio"; Filename: "{app}\MosaicTemplateStudio.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\.mosaic\OpenWithProgids"; ValueType: string; ValueName: "MosaicTemplateStudio.Project"; ValueData: ""; Flags: uninsdeletevalue; Tasks: association
Root: HKCU; Subkey: "Software\Classes\MosaicTemplateStudio.Project"; ValueType: string; ValueName: ""; ValueData: "{cm:ProjectDescription}"; Flags: uninsdeletekey; Tasks: association
Root: HKCU; Subkey: "Software\Classes\MosaicTemplateStudio.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\MosaicTemplateStudio.exe,0"; Tasks: association
Root: HKCU; Subkey: "Software\Classes\MosaicTemplateStudio.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\MosaicTemplateStudio.exe"" ""%1"""; Tasks: association

[Run]
Filename: "{app}\MosaicTemplateStudio.exe"; Description: "{cm:LaunchApplication}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] entries: personal projects, recovery and preferences survive uninstall.
