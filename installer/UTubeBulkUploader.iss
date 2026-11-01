; UTube Bulk Uploader installer (Inno Setup 6). Built automatically by GitHub Actions.
; Installs for the current user only (no admin rights needed), adds a Start menu entry so the app shows in
; the app list and in Windows search, and registers an uninstaller under Settings > Apps.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{A3E1B6F2-58C4-4D7E-9F21-7C0D4B8E2A93}
AppName=UTube Bulk Uploader
AppVersion={#AppVersion}
AppVerName=UTube Bulk Uploader {#AppVersion}
AppPublisher=UTube Bulk Uploader
DefaultDirName={localappdata}\Programs\UTube Bulk Uploader
DefaultGroupName=UTube Bulk Uploader
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=UTubeBulkUploader-Setup
SetupIconFile=..\src\UTubeBulkUploader.App\Assets\UTubeBulkUploader.ico
UninstallDisplayIcon={app}\UTubeBulkUploader.exe
UninstallDisplayName=UTube Bulk Uploader
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\UTube Bulk Uploader"; Filename: "{app}\UTubeBulkUploader.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\UTube Bulk Uploader"; Filename: "{app}\UTubeBulkUploader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UTubeBulkUploader.exe"; Description: "Open UTube Bulk Uploader"; Flags: nowait postinstall skipifsilent
