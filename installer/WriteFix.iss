; Inno Setup script for WriteFix.
; Build with:  .\build-installer.ps1      (publishes, then compiles this)
;
; Deliberately a PER-USER install (PrivilegesRequired=lowest, installed under
; %LocalAppData%\Programs). WriteFix sends synthetic keystrokes to other windows,
; and an elevated process cannot reach a normal user's windows — so it must never
; end up running as administrator.

#define AppName        "WriteFix"
#define AppVersion     "1.2.0"
; The company publishes it, the author owns it. Windows shows the publisher in
; Settings > Apps and on the SmartScreen prompt.
#define AppPublisher   "iSoutien"
#define AppCopyright   "Copyright (c) 2026 Moad Dahbi"
#define AppExeName     "WriteFix.exe"
; Derived from AppVersion so bumping the version cannot leave a stale icon name.
#define AppIconName    "WriteFix-" + AppVersion + ".ico"
#define SourceDir      "..\publish"

[Setup]
AppId={{7C3F1A64-9E2B-4D58-B0A7-5F1C6E8D2A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppCopyright}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright={#AppCopyright}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; No admin prompt: everything lands in the current user's profile.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\Assets\writefix.ico
UninstallDisplayIcon={app}\{#AppIconName}
WizardStyle=modern

; The payload is a self-contained .NET runtime, so it compresses well but slowly.
Compression=lzma2/max
SolidCompression=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Shut down a running copy instead of failing on locked files. AppMutex matches the
; single-instance mutex created in App.xaml.cs.
CloseApplications=yes
RestartApplications=no
AppMutex=Local\WriteFix.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup";     Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"

[Files]
; The whole self-contained publish folder.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; A versioned path prevents Explorer from reusing a cached icon from an older release.
Source: "..\src\Assets\writefix.ico"; DestDir: "{app}"; DestName: "{#AppIconName}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIconName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIconName}"; Tasks: desktopicon

[Registry]
; Same key/value/format the app's own "Start with Windows" checkbox writes, so the
; two stay in sync instead of fighting each other.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "WriteFix"; ValueData: """{app}\{#AppExeName}"" --background"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent
; In-app updates run this setup silently with /RELAUNCH=1, and /BACKGROUND=1 when
; WriteFix was sitting in the tray rather than showing Settings. A silent run skips
; the entry above, which is why this second one exists.
; runasoriginaluser matters here: if setup was elevated, an elevated WriteFix could
; not send keystrokes to normal windows, which is the whole app (see the note above).
Filename: "{app}\{#AppExeName}"; Parameters: "{code:RelaunchParameters}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

// Set by the in-app updater so the new build starts itself once setup is done.
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function RelaunchParameters(Value: string): string;
begin
  if ExpandConstant('{param:BACKGROUND|0}') = '1' then
    Result := '--background'
  else
    Result := '';
end;

// WriteFix is a tray app with no main window. Restart Manager does not reliably
// close it, and a running copy locks its own files, so kill it outright.
procedure StopWriteFix;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM WriteFix.exe', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Give Windows a moment to release the file handles and the named mutex.
  Sleep(1500);
end;

// Make an install-over-the-top work even if the user is mid-correction.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopWriteFix;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopWriteFix;
  Result := True;
end;

// Uninstall must leave nothing behind: no process, no autostart entry, no
// settings, no encrypted key, no logs.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // In case it was relaunched between the confirmation and this step.
    StopWriteFix;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // The app's own "Start with Windows" checkbox writes this same value, so it
    // can exist even when the installer's startup task was never selected —
    // uninsdeletevalue alone would miss that case.
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'WriteFix');

    DataDir := ExpandConstant('{localappdata}\WriteFix');
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
