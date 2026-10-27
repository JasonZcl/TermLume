#ifndef AppVersion
  #define AppVersion "0.7.3"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef WebViewBootstrapper
  #error WebViewBootstrapper is required
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif
#ifdef QaMode
  #ifndef QaRoot
    #error QaRoot is required for isolated installer tests
  #endif
  #ifndef QaAppId
    #error QaAppId is required for isolated installer tests
  #endif
  #define ProductId QaAppId
  #define ProductName "TermLume Installer QA"
  #define ProductMutex "Local\TermLume.InstallerQA"
  #define SetupName "TermLume-QA-" + AppVersion
#else
  #define ProductId "{C85D02AE-4D4E-4EAB-89D3-4FCF1E1BC2B3}"
  #define ProductName "TermLume"
  #define ProductMutex "Local\TermLume.Workspace"
  #define SetupName "TermLume-" + AppVersion + "-win-x64-Setup"
#endif

[Setup]
; 固定产品标识用于识别升级；程序目录与个人数据目录不能随版本号混在一起。
AppId={{#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=TermLume
#ifdef QaMode
DefaultDirName={#QaRoot}\App
#else
DefaultDirName={localappdata}\Programs\TermLume
#endif
DefaultGroupName=TermLume
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\..\TermLume\Assets\TermLume.ico
UninstallDisplayIcon={app}\TermLume.exe
UninstallDisplayName={#ProductName} {#AppVersion}
UninstallFilesDir={app}
UninstallLogging=yes
UsePreviousAppDir=yes
UsePreviousLanguage=yes
AppMutex={#ProductMutex}
SetupMutex=Local\{#ProductName}.Setup
CloseApplications=no
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename={#SetupName}
Compression=lzma2
SolidCompression=yes
AllowNoIcons=yes
ChangesAssociations=no
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebViewBootstrapper}"; Flags: dontcopy

#ifndef QaMode
[Icons]
Name: "{userprograms}\TermLume\TermLume"; Filename: "{app}\TermLume.exe"; WorkingDir: "{app}"
Name: "{userprograms}\TermLume\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\TermLume"; Filename: "{app}\TermLume.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TermLume.exe"; Description: "{cm:LaunchProgram,TermLume}"; Flags: nowait postinstall skipifsilent unchecked
#endif

[CustomMessages]
chinesesimplified.UninstallShortcut=卸载 TermLume
english.UninstallShortcut=Uninstall TermLume
chinesesimplified.DataInfo=升级或重新安装时，会自动卸载之前的程序版本，再安装新版本。自动卸载始终保留已有主机、分组、信任指纹、日志、设置和缓存；无需手动卸载。%n%n数据目录：%1%n%n软件目录中包含 unins*.exe，手动卸载时可选择保留或删除应用数据。私钥原文件、SFTP 本地文件和远程文件不属于清理范围。
english.DataInfo=Upgrades and reinstalls automatically uninstall the previous program version before installing this one. Automatic removal always keeps hosts, groups, trusted fingerprints, logs, settings and cache. No manual uninstall is needed.%n%nData folder: %1%n%nRun unins*.exe in the installation folder to manually uninstall and choose whether to keep application data. Original private keys, local SFTP files and remote files are not removed.
chinesesimplified.DataPageTitle=安装与数据
english.DataPageTitle=Installation and data
chinesesimplified.DataPageDescription=自动替换旧版程序，保留已有个人数据。
english.DataPageDescription=Automatically replace the previous program and keep personal data.
chinesesimplified.RuntimeInfo=安装包已内置 .NET，无需另外安装 .NET Desktop Runtime。若未检测到 Microsoft Edge WebView2 Runtime，下一步会联网安装 Microsoft 官方运行时；已有运行时则跳过。无法联网时，请先安装 WebView2 Evergreen Runtime 后重试。
english.RuntimeInfo=The .NET runtime is included; no separate .NET Desktop Runtime installation is needed. If Microsoft Edge WebView2 Runtime is missing, Setup will install it online from Microsoft. If offline, install the WebView2 Evergreen Runtime first and try again.
chinesesimplified.RuntimeInstalling=正在安装 Microsoft Edge WebView2 Runtime，请稍候……
english.RuntimeInstalling=Installing Microsoft Edge WebView2 Runtime. Please wait...
chinesesimplified.RuntimeFailed=未能安装或检测到 WebView2 Runtime。请检查网络，或手动安装 Microsoft Edge WebView2 Evergreen Runtime 后重新运行安装包。程序文件尚未更新。退出代码：%1
english.RuntimeFailed=WebView2 Runtime could not be installed or detected. Check your connection, or install Microsoft Edge WebView2 Evergreen Runtime manually and retry. Program files have not been updated. Exit code: %1
chinesesimplified.UnsafeInstallPath=请将程序安装到独立文件夹，不能使用个人数据目录或与它相互包含的目录：%1
english.UnsafeInstallPath=Choose a separate program folder. It must not be the data folder, inside it, or a parent of it: %1
chinesesimplified.DowngradeBlocked=已安装较新版本（%1）。为避免旧版程序修改新版数据，已停止安装。请使用相同或更新的安装包。
english.DowngradeBlocked=A newer version (%1) is installed. Setup stopped to prevent older software from changing newer data. Use the same or a newer installer.
chinesesimplified.PreviousInstallInvalid=检测到旧版安装记录，但卸载器缺失、安装路径不安全或记录不完整。为保护现有文件，已停止安装。请先修复或手动卸载旧版（选择保留个人数据），再运行此安装包。
english.PreviousInstallInvalid=A previous installation was found, but its uninstaller is missing, its path is unsafe or its registration is incomplete. Setup stopped to protect existing files. Repair or manually uninstall that version while keeping personal data, then retry.
chinesesimplified.PreviousUninstallRunning=正在卸载旧版程序（保留个人数据），请稍候……
english.PreviousUninstallRunning=Uninstalling the previous program (keeping personal data). Please wait...
chinesesimplified.PreviousUninstallFailed=未能完整卸载旧版程序，已停止安装新版本。个人数据不会主动清理。请关闭相关程序后重试，或手动卸载旧版并保留个人数据。退出代码：%1
english.PreviousUninstallFailed=The previous program could not be completely uninstalled. Installation stopped. Personal data is not intentionally removed. Close related programs and retry, or uninstall the previous version manually while keeping personal data. Exit code: %1
chinesesimplified.CloseBeforeUpgrade=TermLume 仍在运行。请先保存远端工作并关闭程序，再安装新版本；安装器不会强制断开连接。
english.CloseBeforeUpgrade=TermLume is still running. Save remote work and close the application before installing. Setup will not forcibly disconnect sessions.
chinesesimplified.UninstallTitle=卸载 TermLume
english.UninstallTitle=Uninstall TermLume
chinesesimplified.UninstallDescription=请选择个人数据的处理方式。两种方式都会移除已安装的程序文件。
english.UninstallDescription=Choose what to do with your personal data. Both options remove installed program files.
chinesesimplified.KeepData=保留个人数据（推荐，默认）
english.KeepData=Keep personal data (recommended, default)
chinesesimplified.KeepDataDetail=保留主机、分组、设置、已知主机和日志；下次安装继续使用。
english.KeepDataDetail=Keep hosts, groups, settings, known hosts and logs for the next installation.
chinesesimplified.DeleteData=删除全部 TermLume 个人数据
english.DeleteData=Delete all TermLume personal data
chinesesimplified.DeleteDataDetail=删除下方应用数据文件夹内的全部内容（含 WebView2 缓存），无法撤销。
english.DeleteDataDetail=Permanently delete the contents of the application data folder below, including the WebView2 cache.
chinesesimplified.DataBoundary=不会清理该文件夹以外的私钥、SFTP 下载文件或任何远程文件。请先备份需要保留的配置。
english.DataBoundary=Private keys and SFTP downloads outside this folder, and all remote files, are not deleted. Back up any configuration you need first.
chinesesimplified.UninstallAction=卸载
english.UninstallAction=Uninstall
chinesesimplified.CancelAction=取消
english.CancelAction=Cancel
chinesesimplified.DeleteConfirmation=确定永久删除以下文件夹中的全部 TermLume 个人数据吗？%n%n%1%n%n主机、分组、设置、已知主机和日志将无法恢复。
english.DeleteConfirmation=Permanently delete all TermLume personal data in this folder?%n%n%1%n%nHosts, groups, settings, known hosts and logs cannot be recovered.
chinesesimplified.CleanupIncomplete=程序已卸载，但部分个人数据未删除（可能仍在使用、权限不足或包含目录链接）。为保护链接目标，不跟随符号链接或目录联接。请关闭相关程序后自行检查：%n%n%1
english.CleanupIncomplete=Program files were uninstalled, but some personal data remains (files in use, permissions, or directory links). Symbolic links and junctions are not followed. Close related programs and review this folder manually:%n%n%1

[Code]
const
  FileAttributeDirectory = $10;
  FileAttributeReparsePoint = $400;
  InvalidFileAttributes = $FFFFFFFF;
  WebViewRegistryKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1';

var
  RemovePersonalData: Boolean;

function NativeGetFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// 正式版只定位当前用户的固定数据目录，测试版仅定位编译时指定的隔离目录。
function DataDirectory: String;
begin
#ifdef QaMode
  Result := ExpandFileName('{#QaRoot}\UserData');
#else
  Result := ExpandConstant('{localappdata}\TermLume');
#endif
end;

function NormalizedPath(Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Value));
end;

function ContainsPath(Parent, Child: String): Boolean;
begin
  Parent := AddBackslash(NormalizedPath(Parent));
  Child := AddBackslash(NormalizedPath(Child));
  Result := CompareText(Copy(Child, 1, Length(Parent)), Parent) = 0;
end;

function PathHasReparsePoint(Path: String): Boolean;
var
  Attributes: LongWord;
  Parent: String;
begin
  Result := False;
  Path := NormalizedPath(Path);
  while Path <> '' do begin
    Attributes := NativeGetFileAttributes(Path);
    if (Attributes <> InvalidFileAttributes) and
      ((Attributes and FileAttributeReparsePoint) <> 0) then begin
      Result := True;
      Exit;
    end;
    Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Path));
    if CompareText(Parent, Path) = 0 then Exit;
    Path := Parent;
  end;
end;

// 不使用笼统的目录树删除：逐项检查重解析点，保留链接及无法删除的项目并报告失败。
function DeleteDataTree(Path: String): Boolean;
var
  Entry: TFindRec;
  Child: String;
  Attributes: LongWord;
begin
  Result := False;
  if PathHasReparsePoint(Path) then Exit;
  Attributes := NativeGetFileAttributes(Path);
  if Attributes = InvalidFileAttributes then begin
    Result := not DirExists(Path) and not FileExists(Path);
    Exit;
  end;
  if (Attributes and FileAttributeDirectory) = 0 then Exit;
  Result := True;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Child := AddBackslash(Path) + Entry.Name;
          if ((Entry.Attributes and FileAttributeReparsePoint) <> 0) or
            PathHasReparsePoint(Child) then
            Result := False
          else if (Entry.Attributes and FileAttributeDirectory) <> 0 then begin
            if not DeleteDataTree(Child) then Result := False;
          end
          else if not DeleteFile(Child) then Result := False;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  if not RemoveDir(Path) then Result := False;
end;

function HasWebViewRuntime: Boolean;
var
  Version: String;
begin
#ifdef QaMode
  Result := True;
#else
  Result := (RegQueryStringValue(HKLM32, WebViewRegistryKey, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU32, WebViewRegistryKey, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0'));
#endif
end;

procedure InitializeWizard;
var
  Page: TOutputMsgWizardPage;
begin
  Page := CreateOutputMsgPage(wpSelectDir, CustomMessage('DataPageTitle'),
    CustomMessage('DataPageDescription'),
    FmtMessage(CustomMessage('DataInfo'), [DataDirectory]) + #13#10#13#10 +
    CustomMessage('RuntimeInfo'));
end;

function ReadPreviousInstallation(var Folder, Uninstaller, Version: String): Boolean;
var
  RegisteredCommand, Arguments, Name: String;
  QuoteEnd, Index: Integer;
begin
  Result := RegQueryStringValue(HKCU64, ProductUninstallKey, 'InstallLocation', Folder) and
    RegQueryStringValue(HKCU64, ProductUninstallKey, 'UninstallString', RegisteredCommand) and
    RegQueryStringValue(HKCU64, ProductUninstallKey, 'DisplayVersion', Version);
  if not Result then Exit;
  Result := (Folder <> '') and (Version <> '');
  if not Result then Exit;
  Folder := NormalizedPath(Folder);
  RegisteredCommand := Trim(RegisteredCommand);
  Result := Copy(RegisteredCommand, 1, 1) = '"';
  if not Result then Exit;
  QuoteEnd := Pos('"', Copy(RegisteredCommand, 2, Length(RegisteredCommand)));
  Result := QuoteEnd > 1;
  if not Result then Exit;
  Uninstaller := Copy(RegisteredCommand, 2, QuoteEnd - 1);
  Arguments := Trim(Copy(RegisteredCommand, QuoteEnd + 2, Length(RegisteredCommand)));
  Name := ExtractFileName(Uninstaller);
  Result := (Length(Name) = 12) and (CompareText(Copy(Name, 1, 5), 'unins') = 0) and
    (CompareText(Copy(Name, 9, 4), '.exe') = 0);
  if not Result then Exit;
  for Index := 6 to 8 do
    if (Name[Index] < '0') or (Name[Index] > '9') then begin
      Result := False;
      Exit;
    end;
  Result := ((Arguments = '') or (CompareText(Arguments, '/LOG') = 0)) and
    (CompareText(NormalizedPath(Uninstaller), AddBackslash(Folder) + Name) = 0) and
    (not ContainsPath(DataDirectory, Folder)) and
    (not ContainsPath(Folder, DataDirectory)) and
    (not PathHasReparsePoint(Uninstaller)) and FileExists(Uninstaller);
end;

function UninstallPreviousVersion(Folder, Uninstaller: String): String;
var
  ExitCode: Integer;
begin
  Result := '';
  WizardForm.StatusLabel.Caption := CustomMessage('PreviousUninstallRunning');
  Log('Automatically uninstalling previous TermLume version (keeping personal data).');
  ExitCode := -1;
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /KEEPUSERDATA',
    Folder, SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Result := FmtMessage(CustomMessage('PreviousUninstallFailed'), [IntToStr(ExitCode)]);
    Exit;
  end;
  if (ExitCode <> 0) or RegKeyExists(HKCU64, ProductUninstallKey) or
    FileExists(AddBackslash(Folder) + 'TermLume.exe') then
    Result := FmtMessage(CustomMessage('PreviousUninstallFailed'), [IntToStr(ExitCode)])
  else
    Log('Previous TermLume version uninstalled; personal data retained.');
end;

// 写入程序文件前检查安装边界、降级风险及 WebView2，失败则中止本次安装。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion, PreviousFolder, PreviousUninstaller: String;
  CurrentVersion, PreviousVersion: Int64;
  ExitCode: Integer;
  HasPreviousInstallation: Boolean;
begin
  Result := '';
  if ContainsPath(DataDirectory, ExpandConstant('{app}')) or
    ContainsPath(ExpandConstant('{app}'), DataDirectory) or
    PathHasReparsePoint(ExpandConstant('{app}')) then begin
    Result := FmtMessage(CustomMessage('UnsafeInstallPath'), [DataDirectory]);
    Exit;
  end;
  if CheckForMutexes('{#ProductMutex}') then begin
    Result := CustomMessage('CloseBeforeUpgrade');
    Exit;
  end;
  HasPreviousInstallation := RegKeyExists(HKCU64, ProductUninstallKey);
  if HasPreviousInstallation then begin
    if not ReadPreviousInstallation(PreviousFolder, PreviousUninstaller, InstalledVersion) then begin
      Result := CustomMessage('PreviousInstallInvalid');
      Exit;
    end;
    if not StrToVersion(InstalledVersion, PreviousVersion) then begin
      Result := CustomMessage('PreviousInstallInvalid');
      Exit;
    end;
  end
  else InstalledVersion := GetPreviousData('Version', '');
  if InstalledVersion = '' then
    GetVersionNumbersString(ExpandConstant('{app}\TermLume.exe'), InstalledVersion);
  if StrToVersion('{#AppVersion}', CurrentVersion) and
    StrToVersion(InstalledVersion, PreviousVersion) and
    (ComparePackedVersion(PreviousVersion, CurrentVersion) > 0) then begin
    Result := FmtMessage(CustomMessage('DowngradeBlocked'), [InstalledVersion]);
    Exit;
  end;
  if not HasWebViewRuntime then begin
    WizardForm.StatusLabel.Caption := CustomMessage('RuntimeInstalling');
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    ExitCode := -1;
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'),
      '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or
      (not HasWebViewRuntime) then begin
      Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)]);
      Exit;
    end;
  end;
  if HasPreviousInstallation then
    Result := UninstallPreviousVersion(PreviousFolder, PreviousUninstaller);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Version', '{#AppVersion}');
end;

// 静默卸载默认保留数据，仅显式参数允许清理；交互卸载另有默认保留和二次确认。
function RequestedSilentDataDeletion: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/KEEPUSERDATA') = 0 then Exit;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/DELETEUSERDATA') = 0 then
      Result := True;
end;

function InitializeUninstall: Boolean;
var
  Form: TSetupForm;
  Description, KeepDetail, DeleteDetail, FolderLabel, Boundary: TNewStaticText;
  KeepOption, DeleteOption: TNewRadioButton;
  ConfirmButton, CancelButton: TNewButton;
begin
  RemovePersonalData := False;
  Result := True;
  if UninstallSilent then begin
    RemovePersonalData := RequestedSilentDataDeletion;
    Exit;
  end;
#if VER >= 0x06060000
  Form := CreateCustomForm(ScaleX(560), ScaleY(370), False, False);
#else
  Form := CreateCustomForm;
  Form.ClientWidth := ScaleX(560);
  Form.ClientHeight := ScaleY(370);
#endif
  try
    Form.Caption := CustomMessage('UninstallTitle');
#ifdef QaMode
    Form.Caption := Form.Caption + ' [QA]';
#endif
    Form.Position := poScreenCenter;
    Form.BorderStyle := bsDialog;
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form;
    Description.AutoSize := False;
    Description.SetBounds(ScaleX(24), ScaleY(20), ScaleX(512), ScaleY(40));
    Description.WordWrap := True;
    Description.Caption := CustomMessage('UninstallDescription');
    KeepOption := TNewRadioButton.Create(Form);
    KeepOption.Parent := Form;
    KeepOption.SetBounds(ScaleX(24), ScaleY(70), ScaleX(512), ScaleY(24));
    KeepOption.Caption := CustomMessage('KeepData');
    KeepOption.Checked := True;
    KeepOption.Font.Style := [fsBold];
    KeepDetail := TNewStaticText.Create(Form);
    KeepDetail.Parent := Form;
    KeepDetail.AutoSize := False;
    KeepDetail.SetBounds(ScaleX(46), ScaleY(100), ScaleX(490), ScaleY(36));
    KeepDetail.WordWrap := True;
    KeepDetail.Caption := CustomMessage('KeepDataDetail');
    DeleteOption := TNewRadioButton.Create(Form);
    DeleteOption.Parent := Form;
    DeleteOption.SetBounds(ScaleX(24), ScaleY(144), ScaleX(512), ScaleY(24));
    DeleteOption.Caption := CustomMessage('DeleteData');
    DeleteOption.Font.Style := [fsBold];
    DeleteDetail := TNewStaticText.Create(Form);
    DeleteDetail.Parent := Form;
    DeleteDetail.AutoSize := False;
    DeleteDetail.SetBounds(ScaleX(46), ScaleY(174), ScaleX(490), ScaleY(38));
    DeleteDetail.WordWrap := True;
    DeleteDetail.Caption := CustomMessage('DeleteDataDetail');
    FolderLabel := TNewStaticText.Create(Form);
    FolderLabel.Parent := Form;
    FolderLabel.AutoSize := False;
    FolderLabel.SetBounds(ScaleX(24), ScaleY(220), ScaleX(512), ScaleY(40));
    FolderLabel.WordWrap := True;
    FolderLabel.Caption := DataDirectory;
    Boundary := TNewStaticText.Create(Form);
    Boundary.Parent := Form;
    Boundary.AutoSize := False;
    Boundary.SetBounds(ScaleX(24), ScaleY(266), ScaleX(512), ScaleY(44));
    Boundary.WordWrap := True;
    Boundary.Caption := CustomMessage('DataBoundary');
    ConfirmButton := TNewButton.Create(Form);
    ConfirmButton.Parent := Form;
    ConfirmButton.SetBounds(ScaleX(320), ScaleY(326), ScaleX(104), ScaleY(28));
    ConfirmButton.Caption := CustomMessage('UninstallAction');
    ConfirmButton.ModalResult := mrOK;
    ConfirmButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(436), ScaleY(326), ScaleX(100), ScaleY(28));
    CancelButton.Caption := CustomMessage('CancelAction');
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    Form.ActiveControl := KeepOption;
    Result := Form.ShowModal = mrOK;
    if Result and DeleteOption.Checked then begin
      Result := MsgBox(FmtMessage(CustomMessage('DeleteConfirmation'), [DataDirectory]),
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
      RemovePersonalData := Result;
    end;
  finally
    Form.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CleanupComplete: Boolean;
begin
  if (CurUninstallStep = usPostUninstall) and RemovePersonalData then begin
    // 移除程序后再次检查运行状态；若应用重新启动则保留数据，避免清理活跃工作空间。
    CleanupComplete := False;
    if not CheckForMutexes('{#ProductMutex}') then
      CleanupComplete := DeleteDataTree(DataDirectory);
    if CleanupComplete then
      Log('TermLume personal data removed.')
    else begin
      Log(FmtMessage(CustomMessage('CleanupIncomplete'), [DataDirectory]));
      if not UninstallSilent then
        MsgBox(FmtMessage(CustomMessage('CleanupIncomplete'), [DataDirectory]), mbError, MB_OK);
    end;
  end;
end;
