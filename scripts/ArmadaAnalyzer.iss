; Build with Inno Setup 6.7.3. Supply /DAppSource=... /DSetupOutput=...
#ifndef AppSource
  #error AppSource must name the self-contained publish directory
#endif
#ifndef SetupOutput
  #error SetupOutput must name the installer output directory
#endif
[Setup]
AppId={{69E53D3E-4B90-486B-ACF3-2BF1BF530312}
AppName=Auto Scope
AppVersion=1.01
AppVerName=Auto Scope 1.01
AppPublisher=Auto Scope Contributors
DefaultDirName={localappdata}\Programs\ArmadaAnalyzer
DefaultGroupName=Auto Scope
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Restart Manager must run before any application-mutex check during updates.
CloseApplications=force
CloseApplicationsFilter=AutoScope.exe,ArmadaScopeFX.exe,ArmadaAnalyzer.exe
RestartApplications=no
UninstallDisplayIcon={app}\Assets\AutoScope-1.01.ico
OutputDir={#SetupOutput}
OutputBaseFilename=AutoScope-1.01-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern light
SetupIconFile=..\App\Assets\scope.ico
DisableWelcomePage=yes
DisableDirPage=yes
ShowLanguageDialog=no
DisableFinishedPage=yes
WizardImageFile=assets\scope-wizard.bmp
WizardSmallImageFile=assets\scope-mark.bmp
WizardImageStretch=yes
WizardImageBackColor=$00F4F2F1
WizardSizePercent=110
; License is shipped in the installation folder.
SetupLogging=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"


[CustomMessages]
japanese.UpdateCloseNotice=更新先で実行中のAuto Scopeを強制終了してインストールします。進行中の読込・計算・未保存の操作は中断されます。保存済みの分析データは削除しません。
japanese.CloseBeforeUninstall=Auto Scopeを終了してからアンインストールしてください。

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Workspaces\*,TdsBridge\python\*,TdsBridge\__pycache__\*,diagnostics.log,Install.ps1,ArmadaAnalyzer.exe,ArmadaAnalyzer.dll,ArmadaAnalyzer.deps.json,ArmadaAnalyzer.runtimeconfig.json,ArmadaAnalyzer.pdb"
; Identical runtime binaries may be used by a separate TDS job. Replace only differing versions/content.
Source: "{#AppSource}\TdsBridge\python\*"; DestDir: "{app}\TdsBridge\python"; Flags: replacesameversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{userdesktop}\Armada Scope FX.lnk"
Type: files; Name: "{userprograms}\Armada Scope FX.lnk"
Type: files; Name: "{app}\ArmadaScopeFX.exe"
Type: files; Name: "{app}\ArmadaScopeFX.dll"
Type: files; Name: "{app}\ArmadaScopeFX.pdb"
Type: files; Name: "{app}\ArmadaScopeFX.deps.json"
Type: files; Name: "{app}\ArmadaScopeFX.runtimeconfig.json"

Type: files; Name: "{app}\ArmadaAnalyzer.exe"
Type: files; Name: "{app}\ArmadaAnalyzer.dll"
Type: files; Name: "{app}\ArmadaAnalyzer.pdb"
Type: files; Name: "{app}\ArmadaAnalyzer.deps.json"
Type: files; Name: "{app}\ArmadaAnalyzer.runtimeconfig.json"
Type: files; Name: "{userprograms}\Armada Analyzer.lnk"
Type: files; Name: "{userdesktop}\Armada Analyzer.lnk"

[Icons]
Name: "{userprograms}\Auto Scope"; Filename: "{app}\AutoScope.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AutoScope-1.01.ico"; IconIndex: 0; Check: not WizardNoIcons
Name: "{userdesktop}\Auto Scope"; Filename: "{app}\AutoScope.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AutoScope-1.01.ico"; IconIndex: 0; Check: not WizardNoIcons

[Run]
Filename: "{app}\AutoScope.exe"; Description: "Auto Scope を起動"; Flags: nowait skipifsilent

; No [UninstallDelete]: user workspaces and user-added files are never recursively removed.

[Code]
procedure SHChangeNotify(wEventId, uFlags: Integer; dwItem1, dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SHChangeNotify($08000000, 0, 0, 0);
end;

function SetWindowTheme(hWnd: HWND; pszSubAppName, pszSubIdList: String): Integer;
  external 'SetWindowTheme@uxtheme.dll stdcall';

procedure InitializeWizard;
var AccentLine: TPanel;
begin
  WizardForm.Color := $00F4F2F1;
  WizardForm.Font.Color := $00292320;
  WizardForm.MainPanel.Color := clWhite;
  WizardForm.InnerPage.Color := clWhite;
  WizardForm.PageNameLabel.Font.Color := $002317DC;
  WizardForm.WelcomeLabel1.Font.Color := $002317DC;
  WizardForm.FinishedHeadingLabel.Font.Color := $002317DC;
  AccentLine := TPanel.Create(WizardForm);
  AccentLine.Parent := WizardForm;
  AccentLine.SetBounds(0, 0, WizardForm.ClientWidth, ScaleY(3));
  AccentLine.Anchors := [akLeft, akTop, akRight];
  AccentLine.BevelOuter := bvNone;
  AccentLine.Color := $002317DC;
  SetWindowTheme(WizardForm.ProgressGauge.Handle, '', '');
  SendMessage(WizardForm.ProgressGauge.Handle, $0409, 0, $002317DC);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := CustomMessage('UpdateCloseNotice') + NewLine + NewLine + MemoUserInfoInfo +
    NewLine + MemoDirInfo + NewLine + MemoTypeInfo + NewLine + MemoComponentsInfo +
    NewLine + MemoGroupInfo + NewLine + MemoTasksInfo;
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\ArmadaAnalyzer.Application');
  if not Result then
    MsgBox(CustomMessage('CloseBeforeUninstall'), mbError, MB_OK);
end;
// Close only executables at the selected installation destination. Other copies survive.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Locator, Service, Processes, Process: Variant; I: Integer; Target, Executable: String;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT * FROM Win32_Process WHERE Name="AutoScope.exe" OR Name="ArmadaScopeFX.exe" OR Name="ArmadaAnalyzer.exe"');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then begin
        Executable := Process.ExecutablePath;
        Target := ExpandConstant('{app}');
        if (CompareText(Executable, Target + '\AutoScope.exe') = 0) or
           (CompareText(Executable, Target + '\ArmadaScopeFX.exe') = 0) or
           (CompareText(Executable, Target + '\ArmadaAnalyzer.exe') = 0) then begin
          if Process.Terminate(0) <> 0 then
            Result := '更新先のアプリを終了できません。アプリを終了して再試行してください。';
        end;
      end;
    end;
  except
    Log('自動終了の事前処理を利用できません。Restart Managerで終了を確認します。');
  end;
end;



