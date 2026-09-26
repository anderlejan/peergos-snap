; Peergos Snap installer (Inno Setup 6). Built by build\build.ps1:
;   ISCC /DAppVersion=1.0.0 /DStageDir=...\out\stage /DOutDir=...\dist installer\PeergosSnap.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\out\stage"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6C0E8B1E-6E2B-4C8A-9E4F-5F2D7A1B9C3D}
AppName=Peergos Snap
AppVersion={#AppVersion}
AppVerName=Peergos Snap {#AppVersion}
AppPublisher=anderlejan
AppPublisherURL=https://github.com/anderlejan/peergos-snap
AppSupportURL=https://github.com/anderlejan/peergos-snap/issues
AppUpdatesURL=https://github.com/anderlejan/peergos-snap/releases
DefaultDirName={autopf}\Peergos Snap
DefaultGroupName=Peergos Snap
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutDir}
OutputBaseFilename=PeergosSnap-Setup-{#AppVersion}
SetupIconFile=..\src\PeergosSnap\Assets\PeergosSnap.ico
UninstallDisplayIcon={app}\PeergosSnap.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Peergos Snap
VersionInfoDescription=Peergos Snap installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Peergos Snap when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Old runtime/bridge files must not mix with new ones; old builds shipped debug symbols and a user guide file.
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\bridge"
Type: files; Name: "{app}\PeergosSnap.pdb"
Type: files; Name: "{app}\USER-GUIDE.md"

[Icons]
Name: "{autoprograms}\Peergos Snap"; Filename: "{app}\PeergosSnap.exe"
Name: "{autoprograms}\Peergos Snap – Settings"; Filename: "{app}\PeergosSnap.exe"; Parameters: "--settings"
Name: "{autoprograms}\Peergos Snap – Help"; Filename: "{app}\PeergosSnap.exe"; Parameters: "--help"
Name: "{autodesktop}\Peergos Snap"; Filename: "{app}\PeergosSnap.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PeergosSnap"; ValueData: """{app}\PeergosSnap.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PeergosSnap.exe"; Parameters: "--tray"; Description: "Start Peergos Snap now"; Flags: nowait postinstall

[UninstallRun]
Filename: "{app}\PeergosSnap.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure QuitRunningCopy();
var
  Code: Integer;
begin
  { A running copy (tray) is asked to quit through its own command channel. }
  if FileExists(ExpandConstant('{app}\PeergosSnap.exe')) then
    Exec(ExpandConstant('{app}\PeergosSnap.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningCopy();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Unticking the option on a reinstall removes an older autostart entry. }
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('autostart')) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PeergosSnap');
end;
