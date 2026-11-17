; Installer classico di FrameGrip (Inno Setup 6): benvenuto, scelta della cartella, avanzamento, fine.
; Si compila con build.ps1, che passa versione e cartelle:
;   ISCC /DAppVersion=0.1.0 /DPublishDir=... /DOutDir=... /DIconFile=... FrameGrip.iss
;
; I privilegi di amministratore vengono chiesti una volta sola, all'avvio dell'installazione.
; Con quei privilegi si registra un'attività pianificata che avvia l'app già elevata: i collegamenti
; passano da lì, così Windows non chiede la conferma a ogni avvio.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F3C2B5E-9A41-4D7B-8E2A-51C7F0A9D3B4}
AppName=FrameGrip
AppVersion={#AppVersion}
AppVerName=FrameGrip {#AppVersion}
AppPublisher=Ourze80
AppPublisherURL=https://github.com/Ourze80/FrameGrip
AppSupportURL=https://github.com/Ourze80/FrameGrip
AppUpdatesURL=https://github.com/Ourze80/FrameGrip
DefaultDirName={autopf}\FrameGrip
DefaultGroupName=FrameGrip
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\FrameGrip.exe
UninstallDisplayName=FrameGrip
OutputDir={#OutDir}
OutputBaseFilename=FrameGrip-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Se FrameGrip è aperto, l'installer chiede di chiuderlo prima di continuare.
#ifndef PreviewOnly
AppMutex=FrameGrip.SingleInstance
#endif
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Resti del primo installer (quello senza scelta della cartella).
Type: files; Name: "{app}\Uninstall.exe"

[Icons]
Name: "{autoprograms}\FrameGrip"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn FrameGrip"; WorkingDir: "{app}"; IconFilename: "{app}\FrameGrip.exe"; Comment: "Overlay and frame pacing for games"; Flags: runminimized
Name: "{autodesktop}\FrameGrip"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn FrameGrip"; WorkingDir: "{app}"; IconFilename: "{app}\FrameGrip.exe"; Comment: "Overlay and frame pacing for games"; Flags: runminimized; Tasks: desktopicon

[Run]
; Attività "a richiesta" che avvia l'app con i privilegi più alti dell'utente, senza conferma.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$a = New-ScheduledTaskAction -Execute '{app}\FrameGrip.exe' -WorkingDirectory '{app}'; $p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest; $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel; Register-ScheduledTask -TaskName FrameGrip -Action $a -Principal $p -Settings $s -Force; if (Get-ScheduledTask -TaskName 'FrameGrip Autostart' -ErrorAction SilentlyContinue) {{ Set-ScheduledTask -TaskName 'FrameGrip Autostart' -Action (New-ScheduledTaskAction -Execute '{app}\FrameGrip.exe' -Argument '--tray' -WorkingDirectory '{app}') }"""; \
  StatusMsg: "Setting up launch without confirmation prompts..."; Flags: runhidden
Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn FrameGrip"; Description: "Launch FrameGrip"; Flags: postinstall nowait skipifsilent runhidden

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn FrameGrip /f"; Flags: runhidden; RunOnceId: "RemoveFrameGripTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""FrameGrip Autostart"" /f"; Flags: runhidden; RunOnceId: "RemoveFrameGripAutostart"

[Code]
// La voce di disinstallazione del primo installer va tolta: da ora ce n'è una sola, questa.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RegDeleteKeyIncludingSubkeys(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FrameGrip');
end;
