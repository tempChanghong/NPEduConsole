#define ProductName "NPEduConsole"
#define ProductId "NovarkPower.NPEduConsole.CurrentUser"
#define MainExe "NPEduConsole.App.exe"

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppDisplayVersion}
AppVerName={#ProductName} {#AppDisplayVersion}
AppPublisher=Novark Power
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppDisplayVersion}
VersionInfoVersion={#AppFileVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
DefaultDirName={localappdata}\Programs\NPEduConsole
DisableDirPage=yes
UsePreviousAppDir=yes
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
WizardStyle=modern dynamic
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#MainExe}
OutputDir={#OutputRoot}
OutputBaseFilename=NPEduConsole-{#AppDisplayVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{userprograms}\NPEduConsole"; Filename: "{app}\{#MainExe}"; WorkingDir: "{app}"; Comment: "{cm:AppShortcutComment}"
Name: "{userdesktop}\NPEduConsole"; Filename: "{app}\{#MainExe}"; WorkingDir: "{app}"; Comment: "{cm:AppShortcutComment}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MainExe}"; Description: "{cm:LaunchAfterInstall}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked

[CustomMessages]
chinesesimplified.AppShortcutComment=NPEduConsole 学校管理桌面端
chinesesimplified.LaunchAfterInstall=安装完成后打开 NPEduConsole(&L)
chinesesimplified.Busy=无法锁定此 Windows 用户的 NPEduConsole 安装。请关闭安装版和其他安装或卸载窗口，再试一次。
chinesesimplified.UnsafeTarget=安装目录和当前 Windows 用户的安装登记不符，或目录中已有其他文件。请核对原安装或便携文件的位置。
chinesesimplified.UnknownVersion=无法核对当前安装版本，请先从 Windows 设置卸载本应用，再安装。学校连接设置会保留。
chinesesimplified.Downgrade=本机已安装更高版本，本次未更改文件。
chinesesimplified.ProfileUnavailable=无法访问当前 Windows 用户的配置目录。
english.AppShortcutComment=NPEduConsole school management desktop console
english.LaunchAfterInstall=&Launch NPEduConsole after installation
english.Busy=Could not lock this user's NPEduConsole installation. Close the installed app and any other setup or uninstall window, then try again.
english.UnsafeTarget=The directory does not match this Windows user's registration, or it already contains other files. Check the existing installation or portable files.
english.UnknownVersion=The installed version could not be verified. Uninstall it from Windows Settings before installing. School connection settings are retained.
english.Downgrade=A newer version is already installed. No files were changed.
english.ProfileUnavailable=The current Windows user's settings directory could not be opened.

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1';
  InvalidHandle = -1;
  InvalidAttributes = $FFFFFFFF;
  ReparsePoint = $400;

var
  Maintenance: THandle;

function CreateFileW(FileName: string; Access, Share: Cardinal; Security: NativeUInt;
  Creation, Flags: Cardinal; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(FileName: string): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsRedirected(const Path: string): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> InvalidAttributes) and ((Attributes and ReparsePoint) <> 0);
end;

function OwnTarget: Boolean;
var
  Parent, Target: string;
begin
  Parent := ExpandConstant('{localappdata}\Programs');
  Target := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  Result := (CompareText(Target, Parent + '\NPEduConsole') = 0) and
    not IsRedirected(Parent) and not IsRedirected(Target);
end;

function ContainsFiles(const Directory: string): Boolean;
var
  Search: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(Directory) + '*', Search) then
    Exit;
  try
    repeat
      Result := (Search.Name <> '.') and (Search.Name <> '..');
    until Result or not FindNext(Search);
  finally
    FindClose(Search);
  end;
end;

function LockMaintenance: string;
var
  Profile, LockPath: string;
begin
  Result := '';
  if Maintenance <> 0 then
    Exit;
  Profile := ExpandConstant('{localappdata}\NPEduConsole');
  LockPath := AddBackslash(Profile) + 'installation.lock';
  if IsRedirected(Profile) or IsRedirected(LockPath) or not ForceDirectories(Profile) then begin
    Result := CustomMessage('ProfileUnavailable');
    Exit;
  end;
  { GENERIC_READ | GENERIC_WRITE, no sharing, OPEN_ALWAYS. The installed app holds a shared read handle. }
  Maintenance := CreateFileW(LockPath, $C0000000, 0, 0, 4, $80, 0);
  if Maintenance = THandle(InvalidHandle) then begin
    Maintenance := 0;
    Result := CustomMessage('Busy');
  end;
end;

procedure UnlockMaintenance;
begin
  if Maintenance <> 0 then begin
    CloseHandle(Maintenance);
    Maintenance := 0;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  Registered, Target, InstalledExe: string;
  Current, Incoming: Int64;
begin
  if not OwnTarget then begin
    Result := CustomMessage('UnsafeTarget');
    Exit;
  end;
  Result := LockMaintenance;
  if Result <> '' then
    Exit;
  { Read the registration under the same lock that protects the file writes. }
  Target := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if RegKeyExists(HKCU, UninstallKey) then begin
    if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', Registered) or
        (CompareText(RemoveBackslashUnlessRoot(Registered), Target) <> 0) then
      Result := CustomMessage('UnsafeTarget')
    else begin
      InstalledExe := AddBackslash(Target) + '{#MainExe}';
      if IsRedirected(InstalledExe) or not GetPackedVersion(InstalledExe, Current) or
          not StrToVersion('{#AppFileVersion}', Incoming) then
        Result := CustomMessage('UnknownVersion')
      else if ComparePackedVersion(Current, Incoming) > 0 then
        Result := CustomMessage('Downgrade');
    end;
  end else if ContainsFiles(Target) then
    Result := CustomMessage('UnsafeTarget');
  if Result <> '' then
    UnlockMaintenance;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UnlockMaintenance;
end;

procedure DeinitializeSetup;
begin
  UnlockMaintenance;
end;

function InitializeUninstall: Boolean;
var
  Issue: string;
begin
  if OwnTarget then
    Issue := LockMaintenance
  else
    Issue := CustomMessage('UnsafeTarget');
  Result := Issue = '';
  if not Result then begin
    Log(Issue);
    if not UninstallSilent then
      MsgBox(Issue, mbError, MB_OK);
  end;
end;

procedure DeinitializeUninstall;
begin
  UnlockMaintenance;
end;
