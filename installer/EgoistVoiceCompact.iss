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
DisableWelcomePage=no
DisableDirPage=yes
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
Source: "..\assets\installer-mark-112.bmp"; Flags: dontcopy
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
; Слог 3.2 вернулся на GigaAM: остатки Whisper (модель и нативные рантаймы) больше не нужны.
Type: filesandordirs; Name: "{app}\Models\Speech\whisper-large-v3-turbo-q5_0-v1"
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\Egoist.Voice.dll.prev311"

[Run]
; shellexec: запуск через оболочку Windows. CreateProcess падает с кодом 740, если на exe стоит
; совместимость «от имени администратора»; оболочка сама запросит повышение и не покажет ошибку.
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: nowait shellexec; Check: ShouldLaunch

[Code]
// Фирменное окно: чёрное, без системной рамки и кнопок, один лаймовый акцент.
// Токены из docs/DESIGN.md (в Inno цвет задаётся как BGR).
const
  BackgroundColor = $000A0808;   // #08080A
  SurfaceColor = $00141010;      // #101014
  BorderColor = $00302A2A;       // #2A2A30
  PrimaryTextColor = $00F8F7F7;  // #F7F7F8
  SecondaryTextColor = $00AAA3A3; // #A3A3AA
  AccentColor = $0000FFA8;       // #A8FF00
  AccentTextColor = $00000F0A;   // #0A0F00

  WindowWidth = 520;
  WindowHeight = 372;
  Margin = 40;

var
  Surface: TPanel;
  MarkImage: TBitmapImage;
  ProductLabel, SubtitleLabel, StateLabel, DetailLabel, FooterLabel, CloseLabel: TNewStaticText;
  PercentLabel: TNewStaticText;
  PrimaryButton: TPanel;
  AutoStartBox, DesktopBox: TPanel;
  AutoStartLabel, DesktopLabel: TNewStaticText;
  ProgressTrack, ProgressFill: TPanel;
  FailureMemo: TNewMemo;
  AutoStartOn, DesktopOn: Boolean;
  IsBusyPage, IsUpdate, AutoAdvanced: Boolean;
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
  Result.Parent := Surface;
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

function ParamIsOne(Name: String): Boolean;
begin
  Result := ExpandConstant('{param:' + Name + '|}') = '1';
end;

function ShouldCreateDesktopIcon: Boolean;
begin
  if ExpandConstant('{param:EGOIST_DESKTOP|}') <> '' then
    Result := ParamIsOne('EGOIST_DESKTOP')
  else
    Result := DesktopOn;
end;

function ShouldAutoStart: Boolean;
begin
  if ExpandConstant('{param:EGOIST_AUTOSTART|}') <> '' then
    Result := ParamIsOne('EGOIST_AUTOSTART')
  else
    Result := AutoStartOn;
end;

function ShouldLaunch: Boolean;
begin
  Result := not ParamIsOne('EGOIST_NOLAUNCH');
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

procedure PaintBox(Box: TPanel; Checked: Boolean);
begin
  if Checked then
  begin
    Box.Color := AccentColor;
    Box.Font.Color := AccentTextColor;
    Box.Caption := '✓';
  end
  else
  begin
    Box.Color := BorderColor;
    Box.Font.Color := BorderColor;
    Box.Caption := '';
  end;
end;

procedure ToggleAutoStart(Sender: TObject);
begin
  AutoStartOn := not AutoStartOn;
  PaintBox(AutoStartBox, AutoStartOn);
end;

procedure ToggleDesktop(Sender: TObject);
begin
  DesktopOn := not DesktopOn;
  PaintBox(DesktopBox, DesktopOn);
end;

// Кнопки и закрытие используют настоящие кнопки мастера, спрятанные за краем окна:
// Enter и Esc работают, проверки Inno (место, занятые файлы) не обходятся.
procedure PressPrimary(Sender: TObject);
begin
  if WizardForm.NextButton.Enabled then
    WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure PressClose(Sender: TObject);
begin
  if WizardForm.CurPageID = wpFinished then
    WizardForm.NextButton.OnClick(WizardForm.NextButton)
  else if WizardForm.CancelButton.Enabled then
    WizardForm.CancelButton.OnClick(WizardForm.CancelButton);
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

procedure ParkNativeControls;
var
  Off: Integer;
begin
  // Родные кнопки остаются живыми, но вне видимой области.
  Off := -ScaleX(400);
  WizardForm.BackButton.Parent := WizardForm;
  WizardForm.NextButton.Parent := WizardForm;
  WizardForm.CancelButton.Parent := WizardForm;
  WizardForm.BackButton.SetBounds(Off, ScaleY(8), ScaleX(80), ScaleY(26));
  WizardForm.NextButton.SetBounds(Off, ScaleY(40), ScaleX(80), ScaleY(26));
  WizardForm.CancelButton.SetBounds(Off, ScaleY(72), ScaleX(80), ScaleY(26));
  WizardForm.NextButton.Default := True;
  WizardForm.CancelButton.Cancel := True;
end;

procedure HidePageContent;
begin
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.InnerNotebook.Visible := False;
  WizardForm.PreparingLabel.Visible := False;
  WizardForm.PreparingMemo.Visible := False;
  WizardForm.PreparingYesRadio.Visible := False;
  WizardForm.PreparingNoRadio.Visible := False;
  WizardForm.RunList.Visible := False;
  WizardForm.YesRadio.Visible := False;
  WizardForm.NoRadio.Visible := False;
  AutoStartBox.Visible := False;
  AutoStartLabel.Visible := False;
  DesktopBox.Visible := False;
  DesktopLabel.Visible := False;
  FailureMemo.Visible := False;
  ProgressTrack.Visible := False;
  PercentLabel.Visible := False;
  PrimaryButton.Visible := True;
end;

procedure StyleMemo(Memo: TNewMemo);
begin
  Memo.Parent := Surface;
  Memo.Color := SurfaceColor;
  Memo.BorderStyle := bsNone;
  Memo.Font.Name := 'Segoe UI';
  Memo.Font.Size := 9;
  Memo.Font.Color := SecondaryTextColor;
  Memo.ReadOnly := True;
  Memo.WordWrap := True;
  Memo.ScrollBars := ssVertical;
  Memo.TabStop := True;
end;

function NewToggle(Y: Integer; Text: String; Handler: TNotifyEvent;
  var Box: TPanel; var Caption: TNewStaticText): Boolean;
begin
  Box := TPanel.Create(WizardForm);
  Box.Parent := Surface;
  Box.BevelOuter := bvNone;
  Box.ParentBackground := False;
  Box.Font.Name := 'Segoe UI Symbol';
  Box.Font.Size := 9;
  Box.Font.Style := [fsBold];
  Box.Cursor := crHand;
  Box.OnClick := Handler;
  SetBounds(Box, Margin, Y, 20, 20);
  Caption := NewLabel(Margin + 32, Y, 400, 22, 10, Text, PrimaryTextColor, False);
  Caption.Cursor := crHand;
  Caption.OnClick := Handler;
  Result := True;
end;

procedure CreateBrandShell;
var
  Round: Integer;
begin
  WizardForm.BorderStyle := bsNone;
  WizardForm.Caption := 'Слог — установка';
  WizardForm.ClientWidth := ScaleX(WindowWidth);
  WizardForm.ClientHeight := ScaleY(WindowHeight);
  WizardForm.Color := BorderColor;
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.Font.Size := 9;
  // Windows 11 скругляет окно без рамки; на Windows 10 вызов безвреден.
  Round := 2;
  DwmSetWindowAttribute(WizardForm.Handle, 33, Round, 4);
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  WizardForm.BeveledLabel.Visible := False;

  // Поверхность на один пиксель меньше окна: оставшаяся кайма и есть тонкая граница.
  Surface := TPanel.Create(WizardForm);
  Surface.Parent := WizardForm;
  Surface.SetBounds(1, 1, WizardForm.ClientWidth - 2, WizardForm.ClientHeight - 2);
  Surface.Color := BackgroundColor;
  Surface.ParentBackground := False;
  Surface.BevelOuter := bvNone;

  ExtractTemporaryFile('installer-mark-112.bmp');
  MarkImage := TBitmapImage.Create(WizardForm);
  MarkImage.Parent := Surface;
  MarkImage.AutoSize := False;
  MarkImage.Stretch := True;
  MarkImage.Bitmap.LoadFromFile(ExpandConstant('{tmp}\installer-mark-112.bmp'));
  SetBounds(MarkImage, Margin, 40, 56, 56);

  ProductLabel := NewLabel(Margin + 72, 44, 300, 32, 20, 'Слог', PrimaryTextColor, True);
  SubtitleLabel := NewLabel(Margin + 73, 78, 340, 20, 10, 'Локальная диктовка на русском', SecondaryTextColor, False);

  CloseLabel := NewLabel(WindowWidth - 44, 14, 28, 26, 14, '×', SecondaryTextColor, False);
  CloseLabel.Cursor := crHand;
  CloseLabel.OnClick := @PressClose;

  StateLabel := NewLabel(Margin, 128, WindowWidth - 2 * Margin, 30, 15, '', PrimaryTextColor, True);
  DetailLabel := NewLabel(Margin, 162, WindowWidth - 2 * Margin, 44, 10, '', SecondaryTextColor, False);
  PercentLabel := NewLabel(WindowWidth - Margin - 80, 128, 80, 28, 11, '', SecondaryTextColor, False);
  FooterLabel := NewLabel(Margin, 338, WindowWidth - 2 * Margin, 20, 8, '', SecondaryTextColor, False);

  AutoStartOn := GetPreviousData('AutoStart', '1') = '1';
  DesktopOn := GetPreviousData('DesktopIcon', '0') = '1';
  NewToggle(206, 'Запускать вместе с Windows', @ToggleAutoStart, AutoStartBox, AutoStartLabel);
  NewToggle(236, 'Ярлык на рабочем столе', @ToggleDesktop, DesktopBox, DesktopLabel);
  PaintBox(AutoStartBox, AutoStartOn);
  PaintBox(DesktopBox, DesktopOn);

  ProgressTrack := TPanel.Create(WizardForm);
  ProgressTrack.Parent := Surface;
  ProgressTrack.Color := BorderColor;
  ProgressTrack.ParentBackground := False;
  ProgressTrack.BevelOuter := bvNone;
  SetBounds(ProgressTrack, Margin, 222, WindowWidth - 2 * Margin, 3);
  ProgressFill := TPanel.Create(WizardForm);
  ProgressFill.Parent := ProgressTrack;
  ProgressFill.Color := AccentColor;
  ProgressFill.ParentBackground := False;
  ProgressFill.BevelOuter := bvNone;
  ProgressFill.SetBounds(0, 0, 0, ProgressTrack.Height);

  StyleMemo(WizardForm.PreparingMemo);
  FailureMemo := TNewMemo.Create(WizardForm);
  StyleMemo(FailureMemo);
  SetBounds(FailureMemo, Margin, 206, WindowWidth - 2 * Margin, 70);

  PrimaryButton := TPanel.Create(WizardForm);
  PrimaryButton.Parent := Surface;
  PrimaryButton.BevelOuter := bvNone;
  PrimaryButton.ParentBackground := False;
  PrimaryButton.Color := AccentColor;
  PrimaryButton.Font.Name := 'Segoe UI';
  PrimaryButton.Font.Size := 10;
  PrimaryButton.Font.Style := [fsBold];
  PrimaryButton.Font.Color := AccentTextColor;
  PrimaryButton.Cursor := crHand;
  PrimaryButton.OnClick := @PressPrimary;
  SetBounds(PrimaryButton, Margin, 284, 180, 40);
  ParkNativeControls;
end;

procedure InitializeWizard;
begin
  LastReportedPercent := -1;
  IsBusyPage := False;
  AutoAdvanced := False;
  IsUpdate := GetPreviousData('AutoStart', 'none') <> 'none';
  CreateBrandShell;
end;

// Закрытие без вопроса «Прервать установку?»: никаких системных окон.
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Confirm := False;
end;

function IsAppRunning: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\cmd.exe'),
    '/c tasklist /FI "IMAGENAME eq {#AppExe}" /NH | find /I "{#AppExe}" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

// Просим Слог завершиться штатно (--shutdown). Если на exe стоит «запуск от имени администратора»,
// приложение работает с повышением, обычный установщик его не закроет: тогда запрос идёт через оболочку
// Windows, и система сама спросит подтверждение. Диалогов «файл занят» после этого не будет.
function StopRunningApp: Boolean;
var
  Code, Attempt: Integer;
  AppPath: String;
begin
  Result := True;
  if not IsAppRunning then Exit;
  AppPath := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(AppPath) then Exit;
  Log('App is running, requesting shutdown');
  Exec(AppPath, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for Attempt := 1 to 6 do
  begin
    if not IsAppRunning then Exit;
    Sleep(500);
  end;
  ShellExec('', AppPath, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for Attempt := 1 to 10 do
  begin
    if not IsAppRunning then Exit;
    Sleep(500);
  end;
  Result := not IsAppRunning;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunningApp then
    Result := 'Не удалось закрыть Слог. Закройте его в области уведомлений и запустите установку снова.';
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Busy, HasFailure: Boolean;
  FailureText: String;
begin
  // Состояние движка читаем до смены видимости: занятые файлы и настоящая ошибка различаются.
  Log('Page ' + IntToStr(CurPageID));
  Busy := (CurPageID = wpPreparing) and WizardForm.PreparingMemo.Visible;
  HasFailure := (CurPageID = wpPreparing) and WizardForm.PreparingLabel.Visible and not Busy;
  FailureText := WizardForm.PreparingLabel.Caption;
  IsBusyPage := Busy;
  HidePageContent;
  ParkNativeControls;
  CloseLabel.Visible := CurPageID <> wpInstalling;
  FooterLabel.Caption := '';

  if CurPageID = wpWelcome then
  begin
    if IsUpdate then
    begin
      StateLabel.Caption := 'Обновление до {#AppVersion}';
      PrimaryButton.Caption := 'Обновить';
    end
    else
    begin
      StateLabel.Caption := 'Готово к установке';
      PrimaryButton.Caption := 'Установить';
    end;
    DetailLabel.Caption := 'Модели уже внутри, интернет не нужен. Настройки сохранятся.';
    AutoStartBox.Visible := True;
    AutoStartLabel.Visible := True;
    DesktopBox.Visible := True;
    DesktopLabel.Visible := True;
    FooterLabel.Caption := 'Версия {#AppVersion} · Windows 10/11 x64 · без аккаунта и телеметрии';
    if ParamIsOne('EGOIST_AUTO') and not AutoAdvanced then
    begin
      AutoAdvanced := True;
      WizardForm.NextButton.OnClick(WizardForm.NextButton);
    end;
  end
  else if CurPageID = wpPreparing then
  begin
    if Busy then
    begin
      StateLabel.Caption := 'Слог сейчас запущен';
      DetailLabel.Caption := 'Нужно закрыть его, чтобы обновить файлы. Завершите диктовку и продолжайте.';
      WizardForm.PreparingYesRadio.Checked := True;
      SetBounds(WizardForm.PreparingMemo, Margin, 206, WindowWidth - 2 * Margin, 70);
      WizardForm.PreparingMemo.Visible := True;
      PrimaryButton.Caption := 'Закрыть и продолжить';
    end
    else if HasFailure then
    begin
      StateLabel.Caption := 'Установка приостановлена';
      DetailLabel.Caption := 'Устраните причину ниже и запустите установщик снова.';
      FailureMemo.Text := FailureText;
      FailureMemo.Visible := True;
      PrimaryButton.Caption := 'Закрыть';
    end
    else
    begin
      StateLabel.Caption := 'Проверяю готовность';
      DetailLabel.Caption := 'Папка, свободное место, открытые приложения…';
      PrimaryButton.Visible := False;
    end;
  end
  else if CurPageID = wpInstalling then
  begin
    StateLabel.Caption := 'Устанавливаю';
    DetailLabel.Caption := 'Копирую приложение и локальные модели. Это займёт около минуты.';
    ProgressTrack.Visible := True;
    PercentLabel.Visible := True;
    PrimaryButton.Visible := False;
    SetProgress(0, 100);
  end
  else if CurPageID = wpFinished then
  begin
    StateLabel.Caption := 'Готово';
    DetailLabel.Caption := 'Слог в трее. Удерживайте Mouse 5 и говорите.';
    PrimaryButton.Caption := 'Закрыть';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Log('Next on page ' + IntToStr(CurPageID));
  if CurPageID = wpWelcome then
  begin
    StateLabel.Caption := 'Закрываю Слог';
    StopRunningApp;
  end;
  // Понятная кнопка «Закрыть и продолжить» разрешает Restart Manager закрыть только файлы этого пакета.
  if (CurPageID = wpPreparing) and IsBusyPage then
    WizardForm.PreparingYesRadio.Checked := True;
  Result := True;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  SetProgress(CurProgress, MaxProgress);
end;
