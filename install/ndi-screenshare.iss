; NDI Screenshare — Inno Setup script (v6 or later)
; AppVersion is injected at build time via /DMyAppVersion=<VERSION>
;   e.g. ISCC /DMyAppVersion=0.1.0 install\ndi-screenshare.iss
; Falls back to 0.0.0 when built without the define (dev only).
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "NDI Screenshare"
#define MyAppExe "NDI_Screenshare.exe"
#define MyAppPublisher "NDI Screenshare"
#define MyAppURL "https://github.com/Brisbane-ICT/NDI_Screenshare"
#define NdiRuntimeURL "https://ndi.video/tools/ndi-core-suite/"

[Setup]
AppId={{8A3F2C1D-4B5E-4A6F-9C7D-1E2F3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=NDI_Screenshare-{#MyAppVersion}-win64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExe}
LicenseFile=..\install\LICENSE-NOTE.txt
SetupIconFile=
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start {#MyAppName} automatically when I log on"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Framework-dependent single-file publish output (built by build-installer.ps1).
Source: "..\dist\publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\publish\*"; DestDir: "{app}"; Excludes: "{#MyAppExe}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: autostart
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Optional autostart via Run key (alternative to Startup shortcut above).
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NDI Screenshare"; ValueData: """{app}\{#MyAppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function NdiFilePresent(): Boolean;
var
  Pf64, Pf32, AutoPf: String;
begin
  { The app loads this exact DLL — file presence is the strongest signal. }
  Pf64 := ExpandConstant('{pf64}');
  Pf32 := ExpandConstant('{pf32}');
  AutoPf := ExpandConstant('{autopf}');
  Result :=
    FileExists(Pf64 + '\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll') or
    FileExists(Pf32 + '\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll') or
    FileExists(AutoPf + '\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll') or
    FileExists(Pf64 + '\NDI\NDI 6 Runtime\Processing.NDI.Lib.x64.dll') or
    FileExists(Pf32 + '\NDI\NDI 6 Runtime\Processing.NDI.Lib.x64.dll') or
    FileExists(Pf64 + '\NewTek\NDI 5 Runtime\v5\ndi.dll') or
    FileExists(Pf32 + '\NewTek\NDI 5 Runtime\v5\ndi.dll');
end;

function NdiRuntimeMissing(): Boolean;
begin
  { NDI 6 registers HKLM\SOFTWARE\NDI (and no longer uses NewTek\NDI).
    Warn only when neither the registry markers nor the runtime DLL exist. }
  Result := not (
    NdiFilePresent() or
    RegKeyExists(HKLM64, 'SOFTWARE\NDI') or
    RegKeyExists(HKLM64, 'SOFTWARE\NewTek\NDI') or
    RegKeyExists(HKLM32, 'SOFTWARE\NewTek\NDI'));
end;

function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  { Silent installs must never block on a message box. }
  if WizardSilent() then exit;
  if NdiRuntimeMissing() then
  begin
    Answer := MsgBox(
      'NDI Runtime 6 was not detected.' + #13#10 + #13#10 +
      'NDI Screenshare needs the FREE NDI Runtime 6 to send video.' + #13#10 +
      'Download it from:' + #13#10 +
      'https://ndi.video/tools/ndi-core-suite/' + #13#10 + #13#10 +
      'Continue with setup anyway?',
      mbConfirmation, MB_YESNO);
    Result := (Answer = IDYES);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
