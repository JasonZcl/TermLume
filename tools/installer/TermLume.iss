#ifndef AppVersion
  #define AppVersion "0.7.0"
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
chinesesimplified.DataInfo=升级或重新安装只更新程序文件，不覆盖已有主机、分组、信任指纹、日志和设置。%n%n数据目录：%1%n%n软件目录中包含 unins000.exe，可随时卸载并选择保留或删除应用数据。私钥原文件、SFTP 本地文件和远程文件不属于清理范围。
english.DataInfo=Upgrades and reinstalls only update program files. Existing hosts, groups, trusted fingerprints, logs and settings are preserved.%n%nData folder: %1%n%nRun unins000.exe in the installation folder to uninstall and choose whether to keep application data. Original private keys, local SFTP files and remote files are not removed.
chinesesimplified.DataPageTitle=安装与数据
english.DataPageTitle=Installation and data
chinesesimplified.DataPageDescription=程序与个人数据分开存放，升级无需先卸载。
english.DataPageDescription=Program files and personal data are stored separately. No uninstall is needed to upgrade.
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

var
  RemovePersonalData: Boolean;

function NativeGetFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion: String;
  CurrentVersion, PreviousVersion: Int64;
  ExitCode: Integer;
begin
  Result := '';
  if ContainsPath(DataDirectory, ExpandConstant('{app}')) or
    ContainsPath(ExpandConstant('{app}'), DataDirectory) or
    PathHasReparsePoint(ExpandConstant('{app}')) then begin
    Result := FmtMessage(CustomMessage('UnsafeInstallPath'), [DataDirectory]);
    Exit;
  end;
  InstalledVersion := GetPreviousData('Version', '');
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
    if Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'),
      '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      if HasWebViewRuntime then Exit;
    end;
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ExitCode)]);
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Version', '{#AppVersion}');
end;

function RequestedSilentDataDeletion: Boolean;
var
  Index: Integer;
begin
  Result := False;
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
