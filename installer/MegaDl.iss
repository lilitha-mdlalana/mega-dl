; Inno Setup script for MegaDl. Built by .github/workflows/release.yml (needs a Windows
; runner — ISCC.exe itself is Windows-only, unlike the plain dotnet publish zip).
;
; Expects the self-contained win-x64 publish output at ..\publish\win-x64\ (see the
; "Publish win-x64" step in the workflow) and AppVersion passed on the command line:
;   iscc /DAppVersion=1.2.3 installer\MegaDl.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName "MegaDl"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{FD44D29B-C230-4393-B7EC-8FD239BAFDA8}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MegaDl
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\MegaDl.exe
OutputDir=..\installer-output
OutputBaseFilename=MegaDl-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\MegaDl.Desktop\Assets\app.ico
; GPL-3.0 — this is a derivative work of MegaBasterd (also GPL-3.0); the license text
; travels with the install, same as it does in the repo's LICENSE file.
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add {#AppName} to PATH (lets you run ""MegaDl"" from a terminal)"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\MegaDl.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MegaDl.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MegaDl.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Adds/removes {app} from the current user's PATH (HKCU\Environment), gated by the
// "addtopath" task above — opt-in, since not everyone running a GUI app wants a new
// terminal command. Per-user rather than machine-wide, so it never needs elevation.

function SendMessageTimeout(hWnd: Integer; Msg: Integer; wParam: Integer; lParam: string;
  fuFlags: Integer; uTimeout: Integer; var lpdwResult: Integer): Integer;
  external 'SendMessageTimeoutA@user32.dll stdcall';

procedure RefreshEnvironment;
var
  ResultCode: Integer;
begin
  // Broadcasts WM_SETTINGCHANGE so Explorer (and anything launched from it afterwards,
  // including new terminal windows) picks up the PATH change without a reboot. Terminals
  // already open when the installer ran still need to be reopened — that part's unavoidable.
  SendMessageTimeout($FFFF, $1A, 0, 'Environment', 2, 5000, ResultCode);
end;

procedure EnvAddPath(Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Paths) then
    Paths := '';

  if Paths = '' then
    Paths := Dir
  else if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    exit // already there
  else
    Paths := Paths + ';' + Dir;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Paths);
  RefreshEnvironment;
end;

procedure EnvRemovePath(Dir: string);
var
  Paths: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Paths) then
    exit;

  // Pos() is computed against ';'+Paths+';' (so a match at the very start or end of
  // Paths still has its separator either side) — that padding shifts every index up by
  // one versus Paths itself, hence "P - 1" below, except right at the start (P = 1),
  // where the matched leading ';' is the padding, not a real character in Paths.
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then
    exit;

  if P = 1 then
    Delete(Paths, 1, Length(Dir) + 1)
  else
    Delete(Paths, P - 1, Length(Dir) + 1);

  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Paths);
  RefreshEnvironment;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsTaskSelected('addtopath') then
    EnvAddPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    EnvRemovePath(ExpandConstant('{app}'));
end;
