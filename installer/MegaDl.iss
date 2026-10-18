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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\MegaDl.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MegaDl.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MegaDl.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
