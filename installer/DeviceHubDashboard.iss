; Instalador del dashboard (y del visor, que va con el) para la PC del tecnico.
;
; NO PREGUNTA NADA. El servidor y el pin vienen horneados, igual que en el
; instalador preconfigurado del agente: el de servidor+dashboard proponia
; "localhost" -- que solo es verdad en el propio servidor -- y dejaba el pin en
; blanco, con lo que el dashboard confiaba en el primer certificado que viera.
;
; Misma carpeta que ya usaban las PCs de los tecnicos, para que
; deploy\update-dashboard.ps1 siga encontrandolo. Y nada de servicios: este
; instalador no toca DeviceHubServer, ni aunque se ejecute en el servidor.
;
;   DeviceHubDashboard-setup.exe /VERYSILENT [/SERVER=host] [/PORT=5443] [/PIN=...]

#define AppName "ILSAN DeviceHub Dashboard"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Publisher "ILSAN"

#ifndef DefaultServer
  #define DefaultServer "192.168.1.10"
#endif
#ifndef DefaultPort
  #define DefaultPort "5443"
#endif
#ifndef DefaultPin
  #define DefaultPin ""
#endif

[Setup]
AppId={{6D1F3A72-9C4B-4E8A-A2D5-DEVICEHUBDASH}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={commonpf}\ILSAN\DeviceHub\Dashboard
DisableDirPage=yes
DefaultGroupName=ILSAN DeviceHub
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installers
OutputBaseFilename=DeviceHubDashboard-setup-{#AppVersion}
SetupIconFile=..\assets\devicehub.ico
UninstallDisplayIcon={app}\DeviceHub.Dashboard.exe
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
; Un dashboard o un visor abiertos tienen los archivos en uso: se cierran, que
; es lo que hace tambien update-dashboard.ps1.
CloseApplications=force

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
; Sin appsettings.json: lo escribe [Code] con el servidor de verdad.
Source: "..\artifacts\dashboard\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DeviceHub Dashboard"; Filename: "{app}\DeviceHub.Dashboard.exe"
Name: "{commondesktop}\DeviceHub Dashboard"; Filename: "{app}\DeviceHub.Dashboard.exe"

[Run]
Filename: "{app}\DeviceHub.Dashboard.exe"; Description: "Abrir el dashboard"; Flags: postinstall nowait skipifsilent

[Code]
function EscaparJson(const Texto: String): String;
begin
  Result := Texto;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\appsettings.json'),
      '{' + #13#10 +
      '  "DeviceHub": {' + #13#10 +
      '    "ServerHost": "' + EscaparJson(ExpandConstant('{param:SERVER|{#DefaultServer}}')) + '",' + #13#10 +
      '    "ServerPort": ' + ExpandConstant('{param:PORT|{#DefaultPort}}') + ',' + #13#10 +
      '    "ServerPin": "' + EscaparJson(ExpandConstant('{param:PIN|{#DefaultPin}}')) + '"' + #13#10 +
      '  }' + #13#10 +
      '}' + #13#10, False);
end;
