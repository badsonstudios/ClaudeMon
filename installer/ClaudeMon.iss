; ClaudeMon Inno Setup Installer Script
; Requires Inno Setup 6.x

#define MyAppName "ClaudeMon"

; Version is derived from the published assembly so there is a single source of
; truth (src/ClaudeMon/ClaudeMon.csproj <Version>). Run installer/build.sh, which
; publishes to ..\publish before compiling this script. Use .claude/scripts/bump-version
; to change the version.
#define MyAppExeSrc "..\publish\ClaudeMon.exe"
#if !FileExists(MyAppExeSrc)
  #error Published build not found at ..\publish\ClaudeMon.exe. Run installer/build.sh (it publishes before compiling the installer).
#endif
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(MyAppExeSrc, VerMajor, VerMinor, VerRev, VerBuild)
#define MyAppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

#define MyAppPublisher "Badson Studios"
#define MyAppURL "https://github.com/badsonstudios/ClaudeMon"
#define MyAppExeName "ClaudeMon.exe"

[Setup]
AppId={{B7E3F8A1-4D2C-4A1B-9F3E-8C6D5A2B1E0F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ClaudeMon-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
; ClaudeMon is a windowless tray app. The Windows Restart Manager (which
; CloseApplications=yes uses) closes apps by messaging their top-level windows,
; so it cannot close ClaudeMon and stalls the wizard on a non-cancellable
; "closing applications" step (#34). We stop the running instance ourselves with
; taskkill in [Code] instead, so the Restart Manager close/restart paths are off.
CloseApplications=no
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; No 'unchecked'/'checkedonce' flag: the task is checked by default on every
; install (including upgrades), so new users opt into start-with-Windows by
; default. They can still untick it to opt out.
Name: "startup"; Description: "Run {#MyAppName} at Windows startup"; GroupDescription: "Additional options:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; Run at startup (only if task selected)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Fresh install (app wasn't already running): offer to launch from the final page.
; If it WAS running, we relaunch automatically in [Code] instead (see CurStepChanged),
; so this entry is suppressed to avoid a redundant second launch.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; Check: not WasAppRunning

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillClaudeMon"

[Code]
var
  ResultCode: Integer;
  AppWasRunning: Boolean;

// True if a ClaudeMon.exe process is currently running.
function IsAppRunning(): Boolean;
var
  RC: Integer;
begin
  Result := Exec(
    ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq ClaudeMon.exe" /NH | findstr /I "ClaudeMon.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0);
end;

// Exposed to the [Run] section's Check parameter.
function WasAppRunning(): Boolean;
begin
  Result := AppWasRunning;
end;

// Stop a running ClaudeMon so its files aren't locked during the copy. Force-kill
// (the app is a tray monitor with no unsaved state) then wait, bounded, for the
// process to actually exit so [Files] can't race a lingering handle.
procedure StopAppIfRunning();
var
  Waited: Integer;
begin
  AppWasRunning := IsAppRunning();
  if not AppWasRunning then
    Exit;

  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM ClaudeMon.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Poll up to ~3s (20 x 150ms) for the process to disappear.
  Waited := 0;
  while IsAppRunning() and (Waited < 20) do
  begin
    Sleep(150);
    Waited := Waited + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    StopAppIfRunning();
  end
  else if CurStep = ssPostInstall then
  begin
    // If it was running before the upgrade, relaunch the freshly-installed version.
    if AppWasRunning then
      Exec(ExpandConstant('{app}\ClaudeMon.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
