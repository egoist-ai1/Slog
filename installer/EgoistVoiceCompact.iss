#ifndef PayloadInclude
  #error PayloadInclude must be a verified explicit file list
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "2.2.0.0"
#endif
#ifdef RussianEdition
  #define AppTitle "Egoist Voice"
  #define PackageName "EgoistVoice-Setup-Russian-" + AppVersion + "-win-x64-inner"
#else
  #define AppTitle "Слог"
  #define PackageName "Slog-Setup-" + AppVersion + "-win-x64"
#endif
#define AppExe "Egoist.Voice.exe"

[Setup]
AppId={{5F84E54F-BE2E-46BA-970C-D1A774D3D239}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher=EGOIST
DefaultDirName={localappdata}\Programs\Egoist Voice Compact
DefaultGroupName=Слог
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.18362
OutputDir={#OutputDir}
OutputBaseFilename={#PackageName}
SetupIconFile=..\assets\EgoistVoice.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppTitle}
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=no
DisableReadyPage=yes
DisableFinishedPage=no
DisableStartupPrompt=yes
WizardResizable=no
WizardSizePercent=100
ShowLanguageDialog=no
Compression=lzma2/normal
SolidCompression=yes
#ifdef RussianEdition
DiskSpanning=yes
DiskSliceSize=2100000000
SlicesPerDisk=1
#else
DiskSpanning=no
#endif
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
UninstallLogMode=append
VersionInfoVersion={#AppFileVersion}
VersionInfoCompany=EGOIST
VersionInfoDescription=Слог — офлайн-диктовка на русском
VersionInfoProductName={#AppTitle}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\assets\installer-microphone-52.bmp"; Flags: dontcopy
#include PayloadInclude

[Icons]
Name: "{autoprograms}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Check: ShouldCreateDesktopIcon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EgoistVoice"; ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Check: ShouldAutoStart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "EgoistVoice"; Flags: deletevalue; Check: not ShouldAutoStart

[InstallDelete]
Type: files; Name: "{autoprograms}\Egoist Voice Compact.lnk"
Type: files; Name: "{userdesktop}\Egoist Voice Compact.lnk"
Type: files; Name: "{userdesktop}\{#AppTitle}.lnk"; Check: not ShouldCreateDesktopIcon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppTitle}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Messages]
SelectDirLabel3=Выберите папку для Слога.
ApplicationsFound=Завершите диктовку. Чтобы обновить файлы, установщик закроет приложения из списка.
ApplicationsFound2=Завершите диктовку. Чтобы обновить файлы, установщик закроет приложения из списка.

[Code]
const
  BackgroundColor = $00000000;
  SurfaceColor = $001A1616;
  PrimaryTextColor = $00FAFAFA;
  SecondaryTextColor = $00ADA5A5;
  AccentColor = $0000FFA8;
  TrackColor = $00332B2B;

var
  BrandSurface: TPanel;
  HeaderIcon: TBitmapImage;
  ProductLabel, VersionLabel, StateLabel, DetailLabel, FolderLabel: TNewStaticText;
  HintLabel, PercentLabel: TNewStaticText;
  AutoStartCheck, DesktopIconCheck: TNewCheckBox;
  ProgressTrack, ProgressFill: TPanel;
  FailureMemo: TNewMemo;
  IsBusyPage: Boolean;
  LastReportedPercent: Integer;

function DwmSetWindowAttribute(Wnd: Integer; Attribute: Integer;
  var Value: Integer; Size: Integer): Integer;
  external 'DwmSetWindowAttribute@dwmapi.dll stdcall';

procedure SetBounds(Control: TControl; X, Y, W, H: Integer);
begin
  Control.SetBounds(ScaleX(X), ScaleY(Y), ScaleX(W), ScaleY(H));
end;

function NewLabel(X, Y, W, H, FontSize: Integer; Text: String;
  FontColor: TColor; Bold: Boolean): TNewStaticText;
begin
  Result := TNewStaticText.Create(WizardForm);
  Result.Parent := BrandSurface;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.ShowAccelChar := False;
  Result.Font.Name := 'Segoe UI';
  Result.Font.Size := FontSize;
  Result.Font.Color := FontColor;
  if Bold then Result.Font.Style := [fsBold];
  SetBounds(Result, X, Y, W, H);
  Result.Caption := Text;
end;

procedure StyleMemo(Memo: TNewMemo);
begin
  Memo.Parent := BrandSurface;
  Memo.Color := SurfaceColor;
  Memo.Font.Name := 'Segoe UI';
  Memo.Font.Size := 10;
  Memo.Font.Color := PrimaryTextColor;
  Memo.ReadOnly := True;
  Memo.WordWrap := True;
  Memo.ScrollBars := ssVertical;
  Memo.TabStop := True;
end;

function ShouldCreateDesktopIcon: Boolean;
begin
  Result := ExpandConstant('{param:EGOIST_DESKTOP|}') = '1';
  if ExpandConstant('{param:EGOIST_DESKTOP|}') = '' then
    Result := DesktopIconCheck.Checked;
end;

function ShouldAutoStart: Boolean;
begin
  Result := ExpandConstant('{param:EGOIST_AUTOSTART|}') = '1';
  if ExpandConstant('{param:EGOIST_AUTOSTART|}') = '' then
    Result := AutoStartCheck.Checked;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if ShouldAutoStart then
    SetPreviousData(PreviousDataKey, 'AutoStart', '1')
  else
    SetPreviousData(PreviousDataKey, 'AutoStart', '0');
  if ShouldCreateDesktopIcon then
    SetPreviousData(PreviousDataKey, 'DesktopIcon', '1')
  else
    SetPreviousData(PreviousDataKey, 'DesktopIcon', '0');
end;

procedure SetProgress(Current, Total: Integer);
var
  Divisor, Value, Maximum, Percent: Integer;
begin
  Percent := 0;
  if Total > 0 then
  begin
    Divisor := (Total div 1000000) + 1;
    Value := Current div Divisor;
    Maximum := Total div Divisor;
    if Maximum > 0 then Percent := (100 * Value) div Maximum;
    if Current >= Total then Percent := 100;
  end;
  if Percent < 0 then Percent := 0;
  if Percent > 100 then Percent := 100;
  ProgressFill.Width := (ProgressTrack.ClientWidth * Percent) div 100;
  PercentLabel.Caption := IntToStr(Percent) + '%';
  if (Percent <> LastReportedPercent) and (ExpandConstant('{param:EGOIST_STATUS|}') <> '') then
  begin
    SaveStringToFile(ExpandConstant('{param:EGOIST_STATUS|}'), IntToStr(Percent), False);
    LastReportedPercent := Percent;
  end;
end;

procedure LayoutFooter;
begin
  WizardForm.BackButton.Parent := BrandSurface;
  WizardForm.CancelButton.Parent := BrandSurface;
  WizardForm.NextButton.Parent := BrandSurface;
  SetBounds(WizardForm.BackButton, 28, 390, 88, 34);
  SetBounds(WizardForm.CancelButton, 240, 390, 96, 34);
  SetBounds(WizardForm.NextButton, 348, 390, 204, 34);
  WizardForm.BackButton.Caption := 'Назад';
  WizardForm.CancelButton.Caption := 'Отмена';
  WizardForm.NextButton.Font.Style := [fsBold];
  WizardForm.NextButton.Default := True;
  WizardForm.CancelButton.Cancel := True;
end;

procedure HidePageContent;
begin
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.InnerNotebook.Visible := False;
  WizardForm.DirEdit.Visible := False;
  WizardForm.DirBrowseButton.Visible := False;
  WizardForm.PreparingLabel.Visible := False;
  WizardForm.PreparingMemo.Visible := False;
  WizardForm.PreparingYesRadio.Visible := False;
  WizardForm.PreparingNoRadio.Visible := False;
  WizardForm.RunList.Visible := False;
  WizardForm.YesRadio.Visible := False;
  WizardForm.NoRadio.Visible := False;
  FolderLabel.Visible := False;
  AutoStartCheck.Visible := False;
  DesktopIconCheck.Visible := False;
  FailureMemo.Visible := False;
  ProgressTrack.Visible := False;
  PercentLabel.Visible := False;
  HintLabel.Visible := False;
  DetailLabel.Visible := True;
end;

procedure CreateBrandShell;
var
  Dark: Integer;
begin
  WizardForm.Caption := 'Слог — установка';
  WizardForm.ClientWidth := ScaleX(580);
  WizardForm.ClientHeight := ScaleY(452);
  WizardForm.Color := BackgroundColor;
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.Font.Size := 9;
  // Keep the native movable window frame. No clipping region survives a DPI change.
  Dark := 1;
  if DwmSetWindowAttribute(WizardForm.Handle, 20, Dark, 4) <> 0 then
    DwmSetWindowAttribute(WizardForm.Handle, 19, Dark, 4);
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  WizardForm.BeveledLabel.Visible := False;

  BrandSurface := TPanel.Create(WizardForm);
  BrandSurface.Parent := WizardForm;
  BrandSurface.SetBounds(0, 0, WizardForm.ClientWidth, WizardForm.ClientHeight);
  BrandSurface.Color := BackgroundColor;
  BrandSurface.ParentBackground := False;
  BrandSurface.BevelOuter := bvNone;

  ExtractTemporaryFile('installer-microphone-52.bmp');
  HeaderIcon := TBitmapImage.Create(WizardForm);
  HeaderIcon.Parent := BrandSurface;
  HeaderIcon.AutoSize := False;
  HeaderIcon.Stretch := True;
  HeaderIcon.Bitmap.LoadFromFile(ExpandConstant('{tmp}\installer-microphone-52.bmp'));
  SetBounds(HeaderIcon, 28, 24, 40, 40);
  ProductLabel := NewLabel(82, 23, 330, 28, 17, 'Слог', PrimaryTextColor, True);
  VersionLabel := NewLabel(83, 53, 420, 20, 9, 'Установка · {#AppVersion}', SecondaryTextColor, False);
  StateLabel := NewLabel(28, 104, 524, 30, 16, '', PrimaryTextColor, True);
  DetailLabel := NewLabel(28, 144, 524, 44, 10, '', SecondaryTextColor, False);
  FolderLabel := NewLabel(28, 184, 524, 20, 9, 'Папка установки', SecondaryTextColor, False);
  HintLabel := NewLabel(28, 316, 524, 48, 9, '', SecondaryTextColor, False);
  PercentLabel := NewLabel(472, 238, 80, 24, 10, '', PrimaryTextColor, True);

  // Reuse Inno's directory edit and browse handler, including its validation and /DIR value.
  WizardForm.DirEdit.Parent := BrandSurface;
  WizardForm.DirEdit.Color := SurfaceColor;
  WizardForm.DirEdit.Font.Color := PrimaryTextColor;
  SetBounds(WizardForm.DirEdit, 28, 208, 416, 28);
  WizardForm.DirBrowseButton.Parent := BrandSurface;
  WizardForm.DirBrowseButton.Caption := 'Выбрать…';
  SetBounds(WizardForm.DirBrowseButton, 456, 206, 96, 32);

  AutoStartCheck := TNewCheckBox.Create(WizardForm);
  AutoStartCheck.Parent := BrandSurface;
  AutoStartCheck.Caption := 'Запускать вместе с Windows';
  AutoStartCheck.Font.Color := PrimaryTextColor;
  AutoStartCheck.Checked := GetPreviousData('AutoStart', '1') = '1';
  SetBounds(AutoStartCheck, 28, 256, 524, 24);
  DesktopIconCheck := TNewCheckBox.Create(WizardForm);
  DesktopIconCheck.Parent := BrandSurface;
  DesktopIconCheck.Caption := 'Создать ярлык на рабочем столе';
  DesktopIconCheck.Font.Color := PrimaryTextColor;
  DesktopIconCheck.Checked := GetPreviousData('DesktopIcon', '1') = '1';
  SetBounds(DesktopIconCheck, 28, 286, 524, 24);

  StyleMemo(WizardForm.PreparingMemo);
  WizardForm.PreparingLabel.Parent := BrandSurface;
  WizardForm.PreparingLabel.Font.Color := SecondaryTextColor;
  WizardForm.PreparingLabel.Font.Size := 10;
  WizardForm.PreparingLabel.WordWrap := True;
  SetBounds(WizardForm.PreparingLabel, 28, 144, 524, 44);
  SetBounds(WizardForm.PreparingMemo, 28, 200, 524, 84);
  WizardForm.PreparingYesRadio.Parent := BrandSurface;
  WizardForm.PreparingNoRadio.Parent := BrandSurface;
  WizardForm.PreparingYesRadio.Font.Color := PrimaryTextColor;
  WizardForm.PreparingNoRadio.Font.Color := PrimaryTextColor;

  FailureMemo := TNewMemo.Create(WizardForm);
  StyleMemo(FailureMemo);
  WizardForm.RunList.Parent := BrandSurface;
  WizardForm.RunList.Color := BackgroundColor;
  WizardForm.RunList.Font.Color := PrimaryTextColor;
  WizardForm.RunList.BorderStyle := bsNone;
  WizardForm.YesRadio.Parent := BrandSurface;
  WizardForm.NoRadio.Parent := BrandSurface;
  WizardForm.YesRadio.Font.Color := PrimaryTextColor;
  WizardForm.NoRadio.Font.Color := PrimaryTextColor;

  ProgressTrack := TPanel.Create(WizardForm);
  ProgressTrack.Parent := BrandSurface;
  ProgressTrack.Color := TrackColor;
  ProgressTrack.ParentBackground := False;
  ProgressTrack.BevelOuter := bvNone;
  SetBounds(ProgressTrack, 28, 274, 524, 4);
  ProgressFill := TPanel.Create(WizardForm);
  ProgressFill.Parent := ProgressTrack;
  ProgressFill.Color := AccentColor;
  ProgressFill.ParentBackground := False;
  ProgressFill.BevelOuter := bvNone;
  ProgressFill.SetBounds(0, 0, 0, ProgressTrack.Height);
  LayoutFooter;
end;

procedure InitializeWizard;
begin
  LastReportedPercent := -1;
  IsBusyPage := False;
  CreateBrandShell;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Busy, HasFailure, PreparingRestart, FinishedRestart: Boolean;
  FailureText: String;
begin
  // Read the engine state before changing visibility; busy files and actual failures differ.
  Busy := (CurPageID = wpPreparing) and WizardForm.PreparingMemo.Visible;
  HasFailure := (CurPageID = wpPreparing) and WizardForm.PreparingLabel.Visible and not Busy;
  PreparingRestart := (CurPageID = wpPreparing) and WizardForm.PreparingYesRadio.Visible and not Busy;
  FinishedRestart := (CurPageID = wpFinished) and WizardForm.YesRadio.Visible;
  FailureText := WizardForm.PreparingLabel.Caption;
  IsBusyPage := Busy;
  HidePageContent;
  LayoutFooter;

  if CurPageID = wpSelectDir then
  begin
    StateLabel.Caption := 'Готово к установке';
#ifdef BundleTextEditor
    DetailLabel.Caption := 'Русская диктовка и редактор текста. Модели уже внутри.';
#else
    DetailLabel.Caption := 'Русская диктовка. Модели уже внутри.';
#endif
    FolderLabel.Visible := True;
    WizardForm.DirEdit.Visible := True;
    WizardForm.DirBrowseButton.Visible := True;
    AutoStartCheck.Visible := True;
    DesktopIconCheck.Visible := True;
    HintLabel.Caption := 'Интернет не нужен. Ваши настройки сохранятся при обновлении.';
    HintLabel.Visible := True;
    WizardForm.NextButton.Caption := 'Установить';
  end
  else if CurPageID = wpPreparing then
  begin
    if Busy then
    begin
      StateLabel.Caption := 'Voice нужно закрыть';
      DetailLabel.Caption := 'Завершите диктовку. Установщик закроет приложения ниже и обновит их файлы.';
      SetBounds(WizardForm.PreparingMemo, 28, 204, 524, 84);
      WizardForm.PreparingMemo.Visible := True;
      WizardForm.PreparingYesRadio.Checked := True;
      HintLabel.Caption := 'Можно закрыть Voice через меню в трее, затем продолжить здесь.';
      HintLabel.Visible := True;
      WizardForm.NextButton.Caption := 'Закрыть и продолжить';
    end
    else if HasFailure then
    begin
      StateLabel.Caption := 'Установка приостановлена';
      DetailLabel.Caption := 'Устраните причину ниже, затем повторите установку.';
      FailureMemo.Text := FailureText;
      SetBounds(FailureMemo, 28, 198, 524, 102);
      FailureMemo.Visible := True;
      if PreparingRestart then
      begin
        SetBounds(WizardForm.PreparingYesRadio, 28, 310, 524, 26);
        SetBounds(WizardForm.PreparingNoRadio, 28, 340, 524, 26);
        WizardForm.PreparingYesRadio.Visible := True;
        WizardForm.PreparingNoRadio.Visible := True;
      end;
    end
    else
    begin
      StateLabel.Caption := 'Проверяем готовность';
      DetailLabel.Caption := 'Проверка папки, свободного места и открытых приложений…';
      SetBounds(WizardForm.PreparingLabel, 28, 144, 524, 44);
      SetBounds(WizardForm.PreparingMemo, 28, 200, 524, 84);
    end;
  end
  else if CurPageID = wpInstalling then
  begin
    StateLabel.Caption := 'Устанавливаем Voice';
    DetailLabel.Caption := 'Копируем приложение и локальные модели. Это займёт несколько минут.';
    ProgressTrack.Visible := True;
    PercentLabel.Visible := True;
    SetProgress(0, 100);
    HintLabel.Caption := 'После установки можно сразу диктовать — скачивать модели не нужно.';
    HintLabel.Visible := True;
  end
  else if CurPageID = wpFinished then
  begin
    StateLabel.Caption := 'Готово';
    DetailLabel.Caption := 'Слог установлен.';
    SetBounds(WizardForm.RunList, 28, 212, 524, 70);
    WizardForm.RunList.Visible := not FinishedRestart;
    if FinishedRestart then
    begin
      DetailLabel.Caption := WizardForm.FinishedLabel.Caption;
      SetBounds(WizardForm.YesRadio, 28, 230, 524, 32);
      SetBounds(WizardForm.NoRadio, 28, 270, 524, 32);
      WizardForm.YesRadio.Visible := True;
      WizardForm.NoRadio.Visible := True;
    end;
    HintLabel.Caption := 'Значок Voice появится в области уведомлений Windows.';
    HintLabel.Visible := not FinishedRestart;
    WizardForm.NextButton.Caption := 'Готово';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  // The clearly labelled action authorizes Restart Manager only for this payload's files.
  if (CurPageID = wpPreparing) and IsBusyPage then
    WizardForm.PreparingYesRadio.Checked := True;
  Result := True;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  SetProgress(CurProgress, MaxProgress);
end;
