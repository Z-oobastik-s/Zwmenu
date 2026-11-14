; Установщик Zwmenu: один exe для второго ноутбука.
; Ставится в профиль пользователя, права администратора не нужны.
; Настройки остаются в %LOCALAPPDATA%\Zwmenu и при переустановке не затираются.

#define MyAppName "Zwmenu"
#define MyAppVersion "1.3.40"
#define MyAppExeName "Zwmenu.exe"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8F3C1A2E-6B47-4D91-9E55-2A7C0D4F8B16}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={localappdata}\Programs\Zwmenu
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Zwmenu-Setup
SetupIconFile=..\Zwmenu\Assets\zwmenu.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=ZwmenuSingleInstance
CloseApplications=force
RestartApplications=no
VersionInfoProductName=Zwmenu
VersionInfoDescription=Установщик Zwmenu
VersionInfoVersion=1.3.40.0

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\tessdata\*"; DestDir: "{app}\tessdata"; Flags: ignoreversion
Source: "{#PublishDir}\x64\*"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "{#PublishDir}\ru\*"; DestDir: "{app}\ru"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Data\*"; DestDir: "{app}\Data"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Zwmenu"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Zwmenu"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Zwmenu"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillZwmenu;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Zwmenu.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function InitializeSetup(): Boolean;
begin
  KillZwmenu;
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  KillZwmenu;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Zwmenu');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  { Тихий запуск из автообновления. Обычный мастер сам спрашивает, открывать ли программу. }
  if (CurStep = ssDone) and WizardSilent then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--updated', '', SW_SHOW, ewNoWait, ResultCode);
end;
