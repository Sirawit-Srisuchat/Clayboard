; Builds dist\Clayboard-Setup.exe from the portable publish output.
;   dotnet publish -p:PublishProfile=Portable
;   "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" installer\Clayboard.iss

#define AppName "Clayboard"
#define AppExe "Clayboard.exe"
#define SourceDir "..\dist\Clayboard"
#define AppVersion GetVersionNumbersString(SourceDir + "\" + AppExe)
; Same Run-key value the app's own "Start with Windows" switch uses, so both stay in sync.
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Never change AppId: it's how a newer installer finds and upgrades an existing install.
AppId={{4E9F613C-9041-4640-9487-5508677538EA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
; Per-user install: no admin prompt, and the app never runs elevated by accident.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\Assets\app.ico
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installing or uninstalling over a running copy asks to close it first.
AppMutex={#AppName}.SingleInstance
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "th"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.StartupTask=Start Clayboard with Windows (quietly, in the tray)
th.StartupTask=เปิด Clayboard พร้อม Windows (ซ่อนอยู่ใน tray)
ja.StartupTask=Windows と同時に Clayboard を起動 (トレイで静かに)
ko.StartupTask=Windows 시작 시 Clayboard 실행 (트레이에서 조용히)
ru.StartupTask=Запускать Clayboard вместе с Windows (тихо, в трее)
en.StartupGroup=Startup:
th.StartupGroup=การเปิดเครื่อง:
ja.StartupGroup=スタートアップ:
ko.StartupGroup=시작:
ru.StartupGroup=Автозапуск:

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Only the exe: a config.json next to it would switch the app into portable mode.
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; --tray starts hidden in the notification area, with no window popping up at sign-in.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The app can turn startup on by itself later, so remove the entry on uninstall whether or not
// the installer created it. Settings in %AppData%\Clayboard are kept for a later reinstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
end;
