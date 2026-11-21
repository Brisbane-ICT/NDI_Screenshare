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
; Out-of-process never-stop supervisor (per-user scheduled tasks + hang watchdog).
Source: "..\install\setup-supervisor.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\install\check-heartbeat.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\install\launch-hidden.vbs"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: autostart
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Optional autostart via Run key (alternative to Startup shortcut above).
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NDI Screenshare"; ValueData: """{app}\{#MyAppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Never-stop supervisor (per-user, silent-safe): registers the "NDI Screenshare"
; logon task (restart-on-failure every 1 min) and the "NDI Screenshare Heartbeat"
; hang-watchdog task. Runs hidden in all modes (including silent installs — there
; is no skipifsilent here on purpose) and always exits 0, so setup can never fail
; because of it. See install/setup-supervisor.ps1 for the full recovery story.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup-supervisor.ps1"" -ExePath ""{app}\{#MyAppExe}"""; Flags: runhidden waituntilterminated; StatusMsg: "Setting up auto-restart supervisor..."

[UninstallRun]
; Remove the supervisor tasks on uninstall (runs before {app} files are deleted).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\setup-supervisor.ps1"" -Unregister"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSupervisorTasks"

[Code]
function NdiEnvFilePresent(): Boolean;
var
  Dir: String;
begin
  Result := False;
  { Official discovery: NDI_RUNTIME_DIR_V* env vars each hold the dir directly. }
  Dir := GetEnv('NDI_RUNTIME_DIR_V6');
  if (Dir <> '') and FileExists(Dir + '\Processing.NDI.Lib.x64.dll') then begin Result := True; exit; end;
  Dir := GetEnv('NDI_RUNTIME_DIR_V5');
  if (Dir <> '') and FileExists(Dir + '\Processing.NDI.Lib.x64.dll') then begin Result := True; exit; end;
  if (Dir <> '') and FileExists(Dir + '\ndi.dll') then begin Result := True; exit; end;
  Dir := GetEnv('NDI_SDK_DIR');
  if (Dir <> '') and FileExists(Dir + '\Bin\x64\Processing.NDI.Lib.x64.dll') then begin Result := True; exit; end;
  if (Dir <> '') and FileExists(Dir + '\Bin\x64\ndi.dll') then begin Result := True; exit; end;
end;

function NdiUninstallLocationPresent(RootKey: Integer): Boolean;
var
  Subkeys: TArrayOfString;
  Display, Location: String;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(RootKey, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', Subkeys) then exit;
  for I := 0 to GetArrayLength(Subkeys) - 1 do
  begin
    Display := '';
    Location := '';
    RegQueryStringValue(RootKey,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Subkeys[I],
      'DisplayName', Display);
    { Match ANY NDI product so custom-drive / Tools / SDK layouts are found. }
    if (Display = '') or (Pos('NDI', Uppercase(Display)) = 0) then continue;
    RegQueryStringValue(RootKey,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Subkeys[I],
      'InstallLocation', Location);
    if Location = '' then continue;
    if FileExists(Location + '\Processing.NDI.Lib.x64.dll') then begin Result := True; exit; end;
    if FileExists(Location + '\ndi.dll') then begin Result := True; exit; end;
  end;
end;

function NdiFilePresent(): Boolean;
var
  Pf64, Pf32, AutoPf: String;
begin
  { Env vars first (covers custom paths / other drives), then well-known files,
    then the Uninstall InstallLocation scan. }
  if NdiEnvFilePresent() then begin Result := True; exit; end;
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
  if Result then exit;
  Result := NdiUninstallLocationPresent(HKLM64) or NdiUninstallLocationPresent(HKLM32);
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
