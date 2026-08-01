; Inno Setup script for Junior Torrent Client (JTC)
; Compile with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\JTC.iss
; Output: dist\JTC-v0.7.5-setup.exe

#define MyAppName "Junior Torrent Client"
#define MyAppShortName "JTC"
#define MyAppFolderName "JuniorTorrentClient"
#define MyAppVersion "0.7.5"
#define MyAppPublisher "yalyoha"
#define MyAppURL "https://github.com/yalyoha/JuniorTorrentClient"
#define MyAppExeName "JTC.exe"
#define MyAppSourceDir "..\src\JTC\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
; Unique GUID — do not change once released (identifies the app in "Programs and Features").
; Same GUID as pre-rebrand, so a user upgrading from a TClient install won't get a duplicate entry.
AppId={{4F1B7EDF-9D9D-4C88-9E7A-4A3F1E7B2F91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
; Per-user install — no UAC prompt, works without admin.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppFolderName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Ignore the previous InstallLocation / GroupName recorded in the registry.
; Without these, a machine upgrading from the pre-rebrand "TClient" install would
; keep installing into %LocalAppData%\Programs\TClient because Inno remembers the old path.
UsePreviousAppDir=no
UsePreviousGroup=no
; Where the installer .exe goes when built.
OutputDir=..\dist
OutputBaseFilename={#MyAppShortName}-v{#MyAppVersion}-setup
SetupIconFile=..\src\JTC\Assets\tclient.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
; Windows 10 20H1+ / Windows 11
MinVersion=10.0.19041
; Historically we set AppMutex here so Inno itself would prompt on a running JTC.
; That dialog looped when the running JTC was too old to react to the graceful
; @shutdown marker (pre-v0.3.36): the user clicked Retry, mutex still held, dialog
; came back, retry again, and so on forever. Our PrepareToInstall now does both
; graceful shutdown AND unconditional taskkill /F fallback, so relying on that path
; alone avoids the loop entirely. Keeping AppMutex removed also means the Inno
; built-in dialog can't compete with our own error text.
;
; Inno's built-in CloseApplications sends WM_CLOSE via Restart Manager, which our
; OnAppWindowClosing absorbs (window hides to tray instead of exiting), leaving
; JTC.exe locked. PrepareToInstall in [Code] does the actual shutdown.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Wipe the entire {app} directory before copying the new build. Without this the
; installer only OVERWRITES files with matching names — any DLL / .mui / resources.pri
; from the previous version that isn't shipped in the new build sticks around, and
; the running exe may load stale bits (users reported "old version still comes up
; after reinstall"). All user data lives in %LocalAppData%\JTC (settings.json,
; torrents.json, debug.log, inbox\, cache\), which is a separate directory — this
; sweep is limited to {app} and does not touch downloads or torrent state.
Type: filesandordirs; Name: "{app}\*"

; Legacy pre-rebrand install (v0.3.5 through v0.3.15 shipped as "TClient"): a fresh
; upgrade to JTC lands in {autopf}\JuniorTorrentClient because UsePreviousAppDir=no,
; but Inno leaves the old {autopf}\TClient directory alone since it's a different
; path. Users who clicked the Start-menu shortcut labelled "TClient" (which still
; points into that stale dir) would launch the old exe and think the update didn't
; take. Sweep it here + delete the legacy shortcuts explicitly.
Type: filesandordirs; Name: "{localappdata}\Programs\TClient"
Type: files;         Name: "{autoprograms}\TClient.lnk"
Type: files;         Name: "{autodesktop}\TClient.lnk"

[Files]
; The whole publish folder — 400+ WinAppSDK / MonoTorrent / .NET DLLs.
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; skipifsilent removed in v0.5.4 so /VERYSILENT installs (used by the in-app
; auto-updater's "AutoUpdateEnabled" path) still relaunch JTC after the copy —
; otherwise the silent update would tear down JTC and never bring it back.
; Interactive installs still show the "Launch JTC" checkbox on the final page
; thanks to the `postinstall` flag.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall

[UninstallDelete]
; Leave the app data (%LocalAppData%\JTC, and %LocalAppData%\TClient for old installs) alone
; by default — that's the user's downloads / torrent list. Just remove the install directory.
Type: filesandordirs; Name: "{app}"

[Code]
const
  JtcMutex        = 'Local\JTC-SingleInstance-yalyoha';
  ShutdownTimeout = 10000; // ms
  PollInterval    = 200;
  // AppId + '_is1' — where Inno records per-user install info in the registry.
  // Same GUID as the pre-rebrand TClient install, so an upgrade from TClient
  // resolves through this key too.
  UninstallRegKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F1B7EDF-9D9D-4C88-9E7A-4A3F1E7B2F91}_is1';

// GetTickCount isn't in Inno 6's built-in Pascal namespace, so import directly
// from Win32. Used only to give the shutdown marker file a unique name in case a
// previous install left a stale marker behind.
function GetTickCount: DWord;
  external 'GetTickCount@kernel32.dll stdcall';

// Returns the full path to the previously-installed version's unins*.exe if any
// is registered under the same AppId, or an empty string if this is a fresh install.
function GetPreviousUninstallerPath: String;
var
  UninstallStr: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRegKey, 'UninstallString', UninstallStr) then
    Result := RemoveQuotes(UninstallStr);
end;

// Force-kill by executable name via cmd.exe → taskkill. Used as the fallback path
// in PrepareToInstall for JTC versions before v0.3.36 (which don't understand the
// @shutdown marker) and for the pre-rebrand TClient.exe from v0.3.15 and earlier.
// Torrent state is persisted after every add/remove/pause/resume, so a hard kill
// loses at most current-second rate stats — same cost as the tray "Выход" path.
procedure ForceKillByName(const ExeName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'),
       '/c taskkill /F /IM ' + ExeName + ' /T',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Called by Inno right before the install phase. Two-stage shutdown of any running
// JTC so the {app} directory can be safely wiped and rewritten:
//
//   1) Graceful — drop the "@shutdown" marker into the inbox. v0.3.36+ picks this
//      up via FileSystemWatcher and calls Process.Kill from its own app-side
//      handler, giving TorrentService's dispose logic a chance to run.
//   2) Force — after 5 s, unconditionally taskkill /F both JTC.exe and legacy
//      TClient.exe. Handles pre-v0.3.36 installs that never learned about the
//      marker (their exe would ignore the file and keep running, and the [Files]
//      copy would then fail to overwrite the locked JTC.dll — user sees a "new
//      launcher, old code" hybrid). Also catches any wedged teardown edge case.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  MarkerDir:  String;
  MarkerPath: String;
  Elapsed:    Integer;
begin
  Result := '';
  if not CheckForMutexes(JtcMutex) then
  begin
    // Mutex not held → JTC not running. Still kill legacy TClient.exe in case
    // an even older install is around (its mutex name may differ from JtcMutex).
    ForceKillByName('TClient.exe');
    Sleep(200);
    Exit;
  end;

  MarkerDir := ExpandConstant('{localappdata}\JTC\inbox');
  if not DirExists(MarkerDir) then
    ForceDirectories(MarkerDir);
  MarkerPath := MarkerDir + '\shutdown_' + IntToStr(GetTickCount) + '.txt';
  SaveStringToFile(MarkerPath, '@shutdown', False);

  // Give the graceful path 5 s — down from the 10 s used in v0.3.36 / v0.4.1 so
  // we get to the force-kill fallback faster. Modern JTC exits within ~200 ms
  // of receiving the marker, so 5 s is a comfortable margin.
  Elapsed := 0;
  while (Elapsed < 5000) and CheckForMutexes(JtcMutex) do
  begin
    Sleep(PollInterval);
    Elapsed := Elapsed + PollInterval;
  end;

  // Fallback: force-kill regardless of whether the mutex is still held. The kill
  // is a no-op if the process already exited via the marker path. Also targets
  // legacy TClient.exe in case a pre-rebrand install is what's running.
  ForceKillByName('JTC.exe');
  ForceKillByName('TClient.exe');
  Sleep(500);

  // Something is wedged past both graceful and force paths — very unusual. Ask
  // the user to intervene rather than proceed and corrupt the install.
  if CheckForMutexes(JtcMutex) then
    Result := 'JTC не удалось завершить. Снимите процесс JTC.exe в Диспетчере задач ' +
              'и запустите установку заново.';
end;

// Immediately before the [Files] copy runs, invoke the previous version's uninstaller
// silently. This guarantees a clean state even when the previous install lived at a
// different {app} path (pre-rebrand TClient → JuniorTorrentClient, or a user-chosen
// custom directory that our [InstallDelete] sweep can't know about). Inno records the
// path to unins*.exe in HKCU under the AppId — we look it up, run it with silent
// switches, then let the normal install proceed. Failure here is non-fatal: the
// [InstallDelete] sweep is a fallback for anything the old uninstaller missed.
procedure CurStepChanged(CurStep: TSetupStep);
var
  UninstallExe: String;
  ResultCode:   Integer;
begin
  if CurStep <> ssInstall then
    Exit;
  UninstallExe := GetPreviousUninstallerPath;
  if (UninstallExe = '') or not FileExists(UninstallExe) then
    Exit;
  Exec(UninstallExe, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Give the shell a moment to release file handles from the just-removed files
  // before we start copying the new build over the same tree.
  Sleep(500);
end;
