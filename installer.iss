#define AppName "Starlight Launcher"
#ifndef AppVersion
#define AppVersion "1.3.1"
#endif
[Setup]
AppId={{A35CC7A5-C657-4BA7-B0CE-60D0CB7B9114}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Starlight
DefaultDirName={userappdata}\.starlight\app
DefaultGroupName={#AppName}
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=artifacts\installer
OutputBaseFilename=Starlight-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Starlight.exe
SetupIconFile=src\Starlight.Desktop\Assets\starlight.ico
CloseApplications=yes
SetupLogging=yes
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Dirs]
Name: "{userappdata}\.starlight\config"; Flags: uninsneveruninstall
Name: "{userappdata}\.starlight\runtimes"; Flags: uninsneveruninstall
Name: "{userappdata}\.starlight\instances"; Flags: uninsneveruninstall
Name: "{userappdata}\.starlight\local_servers"; Flags: uninsneveruninstall
Name: "{userappdata}\.starlight\cache"; Flags: uninsneveruninstall
Name: "{userappdata}\.starlight\logs"; Flags: uninsneveruninstall
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Starlight.exe"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\Starlight.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Starlight.exe"; Description: "Launch Starlight"; Flags: nowait postinstall skipifsilent
[Code]
var RemoveAllData: Boolean;

function InitializeSetup(): Boolean;
begin
  { Self-contained distribution contains its own .NET 8 runtime. Check the
    published runtime at compile/distribution time; no system install needed. }
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  RemoveAllData := False;
  if not UninstallSilent then
    RemoveAllData := MsgBox('Удалить ВСЕ данные Starlight, включая миры, сборки, серверы и аккаунты?' + #13#10 + #13#10 +
      'Нет — удалить только лаунчер и сохранить данные.' + #13#10 + 'Да — полное удаление без возможности восстановления.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var DataRoot: String;
begin
  if (CurUninstallStep = usPostUninstall) and RemoveAllData then begin
    DataRoot := ExpandConstant('{userappdata}\.starlight');
    { Fixed known data root, never the parent AppData directory. }
    if (ExtractFileName(DataRoot) = '.starlight') then begin
      DelTree(DataRoot + '\config', True, True, True);
      DelTree(DataRoot + '\runtimes', True, True, True);
      DelTree(DataRoot + '\instances', True, True, True);
      DelTree(DataRoot + '\local_servers', True, True, True);
      DelTree(DataRoot + '\cache', True, True, True);
      DelTree(DataRoot + '\logs', True, True, True);
    end;
  end;
end;
