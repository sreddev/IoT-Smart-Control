; =====================================================================================================
;  IoT Smart Control — Windows installer (Inno Setup 6)
;
;  Build:  pwsh packaging/build-release.ps1        (publishes the app, then runs ISCC on this file)
;     or:  ISCC packaging/installer/IoTSmartControl.iss /DAppVersion=2.5.0 /DSourceDir=<publish folder>
;
;  What it does
;   * Installs the self-contained app (no .NET needed) per user by default; "Install for all users"
;     is offered and then asks for admin rights.
;   * Upgrades in place. When a previous version (or its settings) is found, a page offers a
;     CLEAN INSTALL: the previous program files and all settings/data are deleted first.
;   * Uninstall asks whether to also DELETE SETTINGS AND DATA (default: keep them).
;   * Silent / unattended:  /CLEANINSTALL  (setup)   and   /DELETEDATA  (uninstaller).
;
;  Settings and data live in %APPDATA%\IoTSmartControl (config.json with protected secrets, the Matter
;  controller storage, safety copies) plus the autostart entry HKCU\...\Run\IoTSmartControl.
; =====================================================================================================

#ifndef AppVersion
  #define AppVersion "2.5.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\obj\publish"
#endif

#define AppName      "IoT Smart Control"
#define AppExe       "IoTSmartControl.exe"
#define DataFolder   "IoTSmartControl"
#define LegacyFolder "LedvanceControl"
#define RunKey       "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue     "IoTSmartControl"
; Never change: identifies the product across versions (upgrade detection, uninstall entry).
#define AppGuid      "{8C1F7B42-6E2D-4B8A-9C3E-5A1D2F6B7E90}"

[Setup]
AppId={{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Sergio (sreddev)
AppPublisherURL=https://github.com/sreddev/IoT-Smart-Control
AppSupportURL=https://github.com/sreddev/IoT-Smart-Control/issues
AppUpdatesURL=https://github.com/sreddev/IoT-Smart-Control/releases
AppCopyright=Copyright (c) 2026 Sergio (sreddev). MIT License.
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; per-user by default (no UAC prompt); the user may choose "all users" instead
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\src\LedvanceControl.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\out
OutputBaseFilename=IoTSmartControl-{#AppVersion}-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; close a running copy (incl. its Matter bridge child) before files are replaced
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Control your smart lights and plugs"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';

var
  UpgradePage: TInputOptionWizardPage;
  CleanInstall: Boolean;
  DeleteDataOnUninstall: Boolean;

{ ---------------------------------------------------------------- helpers }

{ True when /NAME was passed on the command line (case-insensitive). }
function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
      Result := True;
end;

function DataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\{#DataFolder}');
end;

function LegacyDataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\{#LegacyFolder}');
end;

{ Version of the copy already installed (per-user or all-users), or '' when there is none. }
function InstalledVersion(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then
    RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result);
end;

{ Stops the app (and the Node.js Matter bridge it started) so nothing holds files open. }
procedure StopRunningApp();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Deletes every setting the app keeps for this user: the data folder (config with protected keys,
  Matter controller storage, safety copies), the pre-rename folder and the autostart entry. }
procedure DeleteUserData();
begin
  DelTree(DataDir(), True, True, True);
  DelTree(LegacyDataDir(), True, True, True);
  RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}');
end;

{ ---------------------------------------------------------------- setup }

procedure InitializeWizard();
var
  Previous, Caption: String;
begin
  CleanInstall := HasSwitch('CLEANINSTALL');
  Previous := InstalledVersion();
  if (Previous = '') and not DirExists(DataDir()) then
    Exit;  { first install, nothing to keep or clean }

  if Previous <> '' then
    Caption := 'Version ' + Previous + ' is installed. Choose how to install version {#AppVersion}.'
  else
    Caption := 'Settings from an earlier installation were found. Choose how to install version {#AppVersion}.';

  UpgradePage := CreateInputOptionPage(wpSelectDir,
    'Previous installation found', Caption,
    'Your devices, rooms, groups, scenes, schedules, keys and Matter pairings are stored in your user profile.',
    True, False);
  UpgradePage.Add('Upgrade and keep my settings (recommended)');
  UpgradePage.Add('Clean install: delete the previous version and all its settings first');
  if CleanInstall then
    UpgradePage.SelectedValueIndex := 1
  else
    UpgradePage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (UpgradePage <> nil) and (CurPageID = UpgradePage.ID) then
  begin
    CleanInstall := UpgradePage.SelectedValueIndex = 1;
    if CleanInstall and not WizardSilent() then
      Result := MsgBox('A clean install deletes all settings of IoT Smart Control on this PC: devices, rooms, ' +
        'groups, scenes, schedules, cloud sign-ins and local keys.' + #13#10#13#10 +
        'Matter bulbs paired with this PC will need pairing again. To keep a copy, cancel and use ' +
        'Settings > Backup > Export settings first.' + #13#10#13#10 + 'Continue with a clean install?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if CleanInstall then
    Result := Result + 'Clean install:' + NewLine + Space + 'The previous version and all settings will be deleted first.'
  else if UpgradePage <> nil then
    Result := Result + 'Upgrade:' + NewLine + Space + 'Your settings are kept.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    StopRunningApp();
    if CleanInstall then
    begin
      DeleteUserData();
      { remove the old program files too, but only where a copy of this app is installed }
      if FileExists(ExpandConstant('{app}\{#AppExe}')) then
        DelTree(ExpandConstant('{app}\*'), False, True, True);
    end;
  end;
end;

{ ---------------------------------------------------------------- uninstall }

function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteDataOnUninstall := HasSwitch('DELETEDATA');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { usUninstall: the user has confirmed the uninstall; nothing is removed yet }
  if CurUninstallStep = usUninstall then
  begin
    if not UninstallSilent() then
      DeleteDataOnUninstall := TaskDialogMsgBox(
        'Also delete your settings?',
        'IoT Smart Control keeps your devices, rooms, groups, scenes, schedules, cloud sign-ins, local keys ' +
        'and Matter pairings in your user profile (%APPDATA%\{#DataFolder}).' + #13#10#13#10 +
        'Keep them to pick up where you left off after reinstalling, or delete them to remove everything.',
        mbConfirmation, MB_YESNO, ['Keep my settings', 'Delete my settings'], 0) = IDNO;
    StopRunningApp();
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { the autostart entry points at the removed exe: always remove it }
    RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}');
    if DeleteDataOnUninstall then
      DeleteUserData();
  end;
end;
