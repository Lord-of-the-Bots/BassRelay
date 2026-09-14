; Build with ..\build-installer.ps1. All payload paths are supplied by the build script.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required
#endif

[Setup]
AppId={{7A901622-63A5-4F29-A817-E338EC090140}
AppName=Bass Relay
AppVersion={#AppVersion}
AppPublisher=Bass Relay
DefaultDirName={localappdata}\Programs\BassRelay
UsePreviousAppDir=yes
DisableDirPage=no
DefaultGroupName=Bass Relay
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\Assets\BassRelay.ico
UninstallDisplayIcon={app}\BassRelay.exe
UninstallDisplayName=Bass Relay
OutputDir={#InstallerOutputDir}
OutputBaseFilename=BassRelay-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
english.SelectDirLabel3=Choose where to install Bass Relay. You can delete the downloaded installer after installation.
russian.SelectDirLabel3=Выберите, где будет храниться Bass Relay. После установки скачанный установщик можно удалить.
brazilianportuguese.SelectDirLabel3=Escolha onde instalar o Bass Relay. Após a instalação, você pode excluir o instalador baixado.
spanish.SelectDirLabel3=Elige dónde instalar Bass Relay. Una vez instalado, puedes borrar el instalador descargado.
english.FinishedLabelNoIcons=Bass Relay is installed.%n%nSelect your shaker's sound card in the app. Use the gear menu to turn off startup with Windows or closing to the system tray.
russian.FinishedLabelNoIcons=Bass Relay установлен.%n%nВыберите звуковую карту шейкера в приложении. Автозапуск и закрытие в трей можно отключить через шестерёнку.
brazilianportuguese.FinishedLabelNoIcons=O Bass Relay foi instalado.%n%nSelecione a placa de som do seu shaker no aplicativo. No menu da engrenagem, você pode desativar a inicialização com o Windows e a opção de fechar para a bandeja do sistema.
spanish.FinishedLabelNoIcons=Bass Relay está instalado.%n%nSelecciona la tarjeta de sonido de tu shaker en la aplicación. En el menú del engranaje puedes desactivar el inicio con Windows y la opción de cerrar a la bandeja del sistema.

[CustomMessages]
english.LaunchBassRelay=Launch Bass Relay
russian.LaunchBassRelay=Запустить Bass Relay
brazilianportuguese.LaunchBassRelay=Abrir Bass Relay
spanish.LaunchBassRelay=Abrir Bass Relay
english.ShutdownFailure=Could not close Bass Relay. Choose Exit from its system tray icon menu or the app settings, then try again.
russian.ShutdownFailure=Не удалось закрыть Bass Relay. Выберите «Выйти» в меню значка в трее или в настройках приложения, затем повторите попытку.
brazilianportuguese.ShutdownFailure=Não foi possível fechar o Bass Relay. Selecione Sair no menu do ícone na bandeja do sistema ou nas configurações do aplicativo e tente novamente.
spanish.ShutdownFailure=No se pudo cerrar Bass Relay. Elige Salir en el menú del icono de la bandeja del sistema o en los ajustes de la aplicación e inténtalo de nuevo.

[Files]
; Keep the executable first: PrepareToInstall extracts the fresh shutdown helper.
Source: "{#PublishDir}\BassRelay.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\Licenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
; Data is deliberately absent: settings are created by the app and survive upgrades/uninstall.

[Icons]
Name: "{userprograms}\Bass Relay"; Filename: "{app}\BassRelay.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\BassRelay.exe"; Description: "{cm:LaunchBassRelay}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValue = 'BassRelay';

function OpenExistingFileForWrite(FileName: String; DesiredAccess, ShareMode: DWORD;
  SecurityAttributes: UINT_PTR; CreationDisposition, FlagsAndAttributes: DWORD;
  TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseFileHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function WaitForExecutableRelease(const ExePath: String): Boolean;
var
  Attempt: Integer;
  FileHandle: THandle;
begin
  Result := False;
  { The app mutex may disappear slightly before Windows unmaps the EXE.
    OPEN_EXISTING + GENERIC_WRITE checks that mapping without changing bytes.
    Permit all sharing so ordinary readers do not delay the update. }
  for Attempt := 0 to 30 do
  begin
    if not FileExists(ExePath) then
    begin
      Result := True;
      Exit;
    end;
    FileHandle := OpenExistingFileForWrite(ExePath, $40000000, $00000007,
      0, 3, $00000080, 0);
    if FileHandle <> THandle(-1) then
    begin
      CloseFileHandle(FileHandle);
      Result := True;
      Exit;
    end;
    if Attempt < 30 then
      Sleep(100);
  end;
  Log('Bass Relay executable remains locked or not writable after 3 seconds.');
end;

function RequestAppExit(const ExePath: String): Boolean;
var
  ResultCode: Integer;
begin
  { --exit never starts audio or changes settings. The app bounds the IPC wait. }
  Result := Exec(ExePath, '--exit', ExtractFileDir(ExePath), SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if Result then
    Result := ResultCode = 0;
  if not Result then
    Log(Format('Bass Relay shutdown failed; result code: %d', [ResultCode]));
end;

function ShutdownFailure: String;
begin
  Result := CustomMessage('ShutdownFailure');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { Old portable releases do not understand --exit. Always use this package's
    executable so they cannot accidentally start and block the installer. }
  ExtractTemporaryFile('BassRelay.exe');
  if not RequestAppExit(ExpandConstant('{tmp}\BassRelay.exe')) then
  begin
    Result := ShutdownFailure;
    Exit;
  end;
  if not WaitForExecutableRelease(ExpandConstant('{app}\BassRelay.exe')) then
    Result := ShutdownFailure;
end;

function InitializeUninstall: Boolean;
var
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\BassRelay.exe');
  Result := True;
  if FileExists(ExePath) then
    Result := RequestAppExit(ExePath);
  if Result then
    Result := WaitForExecutableRelease(ExePath);
  if not Result then
    SuppressibleMsgBox(ShutdownFailure, mbError, MB_OK, IDOK);
end;

function StartupExecutable(CommandLine: String): String;
var
  Delimiter: Integer;
begin
  Result := '';
  CommandLine := Trim(CommandLine);
  if CommandLine = '' then
    Exit;
  if CommandLine[1] = '"' then
  begin
    Delete(CommandLine, 1, 1);
    Delimiter := Pos('"', CommandLine);
    if Delimiter > 0 then
      Result := Copy(CommandLine, 1, Delimiter - 1);
  end
  else
  begin
    { Our own registration is quoted. Recognize unambiguous unquoted paths too. }
    Delimiter := Pos(' ', CommandLine);
    if Delimiter = 0 then
      Result := CommandLine
    else
      Result := Copy(CommandLine, 1, Delimiter - 1);
  end;
end;

procedure RemoveOwnStartupEntry;
var
  CommandLine, RegisteredExe, InstalledExe: String;
begin
  if not RegQueryStringValue(HKCU, StartupKey, StartupValue, CommandLine) then
    Exit;
  RegisteredExe := StartupExecutable(CommandLine);
  InstalledExe := ExpandConstant('{app}\BassRelay.exe');
  { A portable copy may have taken over autostart. Leave that registration alone. }
  if CompareText(RegisteredExe, InstalledExe) = 0 then
  begin
    if RegDeleteValue(HKCU, StartupKey, StartupValue) then
      Log('Removed startup entry for this installation.')
    else
      Log('Could not remove startup entry for this installation.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Runs after the user confirms uninstall, before removing the installed files. }
  if CurUninstallStep = usUninstall then
    RemoveOwnStartupEntry;
end;
