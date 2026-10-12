; Compile through Build-Installer.ps1: GeneratedFiles contains only verified files.
#ifndef PackageDirectory
  #error PackageDirectory must be supplied by Build-Installer.ps1
#endif
#ifndef GeneratedFiles
  #error GeneratedFiles must be supplied by Build-Installer.ps1
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by Build-Installer.ps1
#endif
#ifndef InstallerOutputName
  #error InstallerOutputName must be supplied by Build-Installer.ps1
#endif
#ifndef MaintenanceHelperPath
  #error MaintenanceHelperPath must be supplied by Build-Installer.ps1
#endif
#ifndef MaintenanceHelperHash
  #error MaintenanceHelperHash must be supplied by Build-Installer.ps1
#endif
#ifndef PayloadManifestHash
  #error PayloadManifestHash must be supplied by Build-Installer.ps1
#endif
#ifndef TestInstaller
  #define TestInstaller 0
#endif
#ifndef PreviewInstaller
  #define PreviewInstaller 0
#endif
#ifndef PreviewTheme
  #define PreviewTheme "system"
#endif
#define WizardAppearance "dynamic"
#define MainBanner "wizard-banner-light.png"
#define MainBackground "wizard-background-light.png"
#if Int(PreviewInstaller) != 0
  #if PreviewTheme == "light"
    #define WizardAppearance "light"
  #elif PreviewTheme == "dark"
    #define WizardAppearance "dark"
    #define MainBanner "wizard-banner.png"
    #define MainBackground "wizard-background.png"
  #endif
#endif
#if Int(TestInstaller) != 0
  #define AppGuid "40B5D24D-0B16-48E6-85C5-6BE9C471D115"
  #define SetupAppId "{{40B5D24D-0B16-48E6-85C5-6BE9C471D115}"
  #define AppName "MorphocyteOS (тест установщика)"
  #define DefaultInstallDirectory "{autopf}\MorphocyteOS"
#else
  #define AppGuid "8AC863D3-AC74-44C3-90EC-082E8A9AA951"
  #define SetupAppId "{{8AC863D3-AC74-44C3-90EC-082E8A9AA951}"
  #define AppName "MorphocyteOS"
  #define DefaultInstallDirectory "{autopf}\MorphocyteOS"
#endif

[Setup]
AppId={#SetupAppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MorphocyteOS
AppSupportURL=https://github.com/Rabotaet3/MorphocyteOS
AppUpdatesURL=https://github.com/Rabotaet3/MorphocyteOS/releases
DefaultDirName={#DefaultInstallDirectory}
AppendDefaultDirName=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
DirExistsWarning=no
UsePreviousTasks=no
#if Int(PreviewInstaller) != 0
; This viewer cannot copy payload files, register an app, or run installation code.
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
AppMutex=
#else
PrivilegesRequired=admin
Uninstallable=yes
AppMutex=Local\MorphocyteOS.Router.SingleInstance
#endif
ArchitecturesAllowed=x64os
SetupArchitecture=x64
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#OutputDirectory}
OutputBaseFilename={#InstallerOutputName}
SetupIconFile={#SourcePath}\..\Source\MorphocyteRouter\Assets\morphocyte.ico
UninstallDisplayIcon={app}\MorphocyteOS.exe
LicenseFile={#PackageDirectory}\LICENSE.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern {#WizardAppearance} windows11 hidebevels
WizardSizePercent=120
WizardBackColor=window
WizardBackImageFile={#SourcePath}\Assets\{#MainBackground}
WizardBackColorDynamicDark=window
WizardBackImageFileDynamicDark={#SourcePath}\Assets\wizard-background.png
WizardImageFile={#SourcePath}\Assets\{#MainBanner}
WizardImageFileDynamicDark={#SourcePath}\Assets\wizard-banner.png
WizardImageBackColor=window
WizardImageBackColorDynamicDark=window
WizardSmallImageFile={#SourcePath}\Assets\wizard-mark.png
WizardSmallImageFileDynamicDark={#SourcePath}\Assets\wizard-mark.png
WizardSmallImageBackColor=none
WizardSmallImageBackColorDynamicDark=none
SetupLogging=yes
SetupMutex=MorphocyteOS.Setup.{#AppGuid}
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no
ChangesAssociations=no
AlwaysRestart=no
RestartIfNeededByRun=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[LangOptions]
DialogFontName=Segoe UI
DialogFontSize=10
WelcomeFontName=Segoe UI
WelcomeFontSize=24

[Messages]
WelcomeLabel1=MorphocyteOS
ButtonNext=&Продолжить
ButtonBack=&Назад
SelectDirDesc=Папка приложения
SelectDirLabel3=Выберите отдельную папку для MorphocyteOS. Личные настройки и профили останутся на месте.
WizardSelectTasks=Дополнительные задачи
SelectTasksDesc=Необязательные действия после установки
SelectTasksLabel2=Выберите, что сделать после установки. Все дополнительные действия выключены по умолчанию.
ReadyLabel1=Всё готово. Проверьте выбранные параметры перед установкой.
FinishedHeadingLabel=MorphocyteOS установлен

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
Name: "launchapp"; Description: "Запустить MorphocyteOS после установки"; Flags: unchecked

[Files]
#if Int(PreviewInstaller) == 0
; The small frozen maintenance helper is pinned by a compile-time SHA, independent
; of the mutable app/update manifest. No duplicate self-contained WPF EXE is kept.
Source: "{#MaintenanceHelperPath}"; DestName: "MorphocyteOS-installer-helper.exe"; Hash: "{#MaintenanceHelperHash}"; Flags: dontcopy
Source: "{#MaintenanceHelperPath}"; DestDir: "{app}\.installer"; DestName: "MorphocyteOS-maintenance.exe"; Hash: "{#MaintenanceHelperHash}"; Flags: ignoreversion
Source: "{#PackageDirectory}\release-manifest.json"; DestName: "installer-payload-manifest.json"; Hash: "{#PayloadManifestHash}"; Flags: dontcopy
#include GeneratedFiles
#endif

[Icons]
#if Int(PreviewInstaller) == 0
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\MorphocyteOS.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MorphocyteOS.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
#if Int(PreviewInstaller) == 0
; The app requires elevation: let the original user's shell request UAC instead
; of CreateProcess failing with ERROR_ELEVATION_REQUIRED or inheriting Setup's user.
Filename: "{app}\MorphocyteOS.exe"; WorkingDir: "{app}"; Verb: "runas"; Tasks: launchapp; Flags: nowait skipifsilent shellexec runasoriginaluser
#endif

[CustomMessages]
russian.TargetRejected=Выберите пустую папку. Повторная установка в существующую папку допустима только для той же версии, установленной этим мастером. Для перехода на новую версию используйте обновление в MorphocyteOS. Переносные сборки, профили и другие файлы перезаписывать нельзя.
russian.CloseBeforeInstall=Перед установкой закройте MorphocyteOS через «Выход» в трее. Если VPN работает через приложение, подключение остановится. Установщик не закрывает приложение и не останавливает другие VPN автоматически.
russian.UninstallRefused=Не удалось безопасно удалить файлы приложения. Закройте MorphocyteOS через «Выход» в трее и попробуйте ещё раз. Профили и настройки сохраняются.
russian.FinishedDescription=Установка завершена. Запустить приложение можно через меню «Пуск». Автозапуск не включён. При удалении приложения ваши профили и настройки будут сохранены.

[Code]
#include "InstallDirectory.iss"

var
  SetupHelperPath: String;

function QuotedArgument(const Value: String): String;
begin
  if Pos('"', Value) <> 0 then RaiseException('Invalid quoted argument.');
  Result := '"' + Value + '"';
end;

function RunSetupValidation(const TargetDirectory: String): Boolean;
var
  ResultCode: Integer;
begin
#if Int(PreviewInstaller) != 0
  Result := True;
#else
  Result := False;
  try
    if SetupHelperPath = '' then begin
      ExtractTemporaryFile('MorphocyteOS-installer-helper.exe');
      ExtractTemporaryFile('installer-payload-manifest.json');
      SetupHelperPath := ExpandConstant('{tmp}\MorphocyteOS-installer-helper.exe');
    end;
    Result := Exec(SetupHelperPath,
      '--installer-validate-target ' + QuotedArgument(TargetDirectory) + ' {#AppGuid}',
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Result := Result and (ResultCode = 0);
  except
    Log('Installer target validation failed.');
  end;
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpSelectDir then
    WizardForm.DirEdit.Text := MorphocyteInstallDirectory(WizardDirValue);
#if Int(PreviewInstaller) != 0
  // Even a direct programmatic Next click cannot start installation in this viewer.
  Result := CurPageID <> wpReady;
#else
  Result := True;
  if CurPageID = wpSelectDir then begin
    Result := RunSetupValidation(WizardDirValue);
    if not Result then MsgBox(CustomMessage('TargetRejected'), mbError, MB_OK);
  end;
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
#if Int(PreviewInstaller) != 0
  Result := 'Это только просмотр оформления. Установка отключена.';
#else
  Result := '';
  if CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))),
    'MorphocyteOS') <> 0 then begin
    Result := 'Конечная папка установки должна называться MorphocyteOS.';
    Exit;
  end;
  // Recheck before payload copying, including silent installation.
  if not RunSetupValidation(ExpandConstant('{app}')) then
    Result := CustomMessage('TargetRejected') + #13#10 + CustomMessage('CloseBeforeInstall');
#endif
end;

#include "Branding.iss"

procedure InitializeWizard;
begin
  // Includes a previous installation path or an explicit /DIR in silent mode.
  WizardForm.DirEdit.Text := MorphocyteInstallDirectory(WizardDirValue);
  InitializeMorphocyteBranding;
  WizardForm.FinishedLabel.Caption := CustomMessage('FinishedDescription');
end;

// Payload entries use uninsneveruninstall. A hash-verified helper deletes only
// currently managed, unmodified files before Inno removes shortcuts/registration.
#if Int(PreviewInstaller) == 0
#include "UninstallBootstrap.iss"
#endif
