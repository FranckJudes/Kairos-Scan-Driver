; ==============================================================================
; OpenScan Engine — Inno Setup Script (Windows Installer)
; ==============================================================================
; Build: ISCC.exe /DAppVersion=1.0.0 /DPublishDir=publish\win-x64 scripts\windows-installer.iss
; ==============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "publish\win-x64"
#endif

[Setup]
AppId={{B8D2F4E1-3A5C-4F9B-9E7D-1C2A3B4D5E6F}
AppName=OpenScan Engine Agent
AppVersion={#AppVersion}
AppVerName=OpenScan Engine Agent {#AppVersion}
AppPublisher=Kairos / FranckJudes
AppPublisherURL=https://github.com/FranckJudes/Kairos-Scan-Driver
DefaultDirName={autopf}\OpenScan\Agent
DefaultGroupName=OpenScan Engine
DisableProgramGroupPage=yes
OutputDir=installers
OutputBaseFilename=OpenScanEngineSetup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\OpenScan.Agent.exe
MinVersion=10.0
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\OpenScan"; Permissions: everyone-modify

[Run]
; --- Création du service Windows (même logique que scripts/install-windows.ps1) ---
; Suppression d'un service existant si upgrade
Filename: "sc.exe"; Parameters: "stop OpenScanAgent"; Flags: runhidden nowait; StatusMsg: "Arrêt du service existant..."; Check: ServiceExists
Filename: "cmd.exe"; Parameters: "/c timeout /t 2 /nobreak >nul & sc.exe delete OpenScanAgent"; Flags: runhidden; StatusMsg: "Suppression de l'ancien service..."; Check: ServiceExists

; Création du service avec démarrage automatique
Filename: "sc.exe"; Parameters: "create OpenScanAgent binPath= ""{app}\OpenScan.Agent.exe"" start= auto DisplayName= ""OpenScan Engine Agent"""; Flags: runhidden; StatusMsg: "Enregistrement du service OpenScanAgent..."

; Description
Filename: "sc.exe"; Parameters: "description OpenScanAgent ""Moteur d'acquisition et agent de numérisation OpenScan Engine (arrière-plan permanent)."""; Flags: runhidden

; Configuration du redémarrage automatique en cas de crash
; Redémarrage après 5 secondes, reset du compteur après 24h (86400s)
Filename: "sc.exe"; Parameters: "failure OpenScanAgent reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden; StatusMsg: "Configuration du redémarrage automatique..."

; Démarrage du service
Filename: "sc.exe"; Parameters: "start OpenScanAgent"; Flags: runhidden; StatusMsg: "Démarrage du service OpenScanAgent..."

[UninstallRun]
; --- Suppression du service (même logique que scripts/uninstall-windows.ps1) ---
Filename: "sc.exe"; Parameters: "stop OpenScanAgent"; Flags: runhidden; RunOnceId: "StopService"
Filename: "cmd.exe"; Parameters: "/c timeout /t 2 /nobreak >nul & sc.exe delete OpenScanAgent"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', 'query OpenScanAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;
