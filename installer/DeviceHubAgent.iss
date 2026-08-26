; Instalador del agente de ILSAN DeviceHub.
;
; Va a decenas de PCs, asi que soporta instalacion silenciosa con parametros:
;
;   DeviceHubAgent-setup.exe /VERYSILENT /SERVER=devicehub-host /CODE=ENROLL-XXXX-XXXX /PIN=abc...=
;
; La identidad de la maquina (C:\ProgramData\ILSANSYSTEM\DeviceHub) NO se borra
; al desinstalar. Borrarla daria un machineId nuevo al reinstalar y la PC
; perderia su historial: para retirar un equipo de verdad esta
; deploy\uninstall.ps1 -RemoveIdentity, que pide confirmacion escrita.

#define AppName "ILSAN DeviceHub Agent"
; Guardado con ifndef: un #define plano pisa el /DAppVersion de la linea de
; comandos, y el instalador salia siempre marcado como 1.0.0 aunque se pidiera
; otra version -- con el numero equivocado en Programas y caracteristicas.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define ServiceName "DeviceHubAgent"
#define Publisher "ILSAN"

; Valores horneados en tiempo de compilacion (deploy\build-agent-preconfigured.ps1).
; Con ellos, instalar en una PC de planta no pide teclear nada: ni la IP del
; servidor, ni el pin, ni el codigo. Es lo que evita el error de escribir el
; nombre del equipo o la IP del MySQL donde va el servidor de DeviceHub.
; El servidor de ILSAN. Puesto aqui y no vacio para que hasta un instalador
; compilado a pelo llegue con la casilla rellena: la unica forma de equivocarse
; que quedaba era teclear la IP del MySQL, que es otra maquina.
;
; Que este NO basta para saltarse la pagina -- para eso hace falta ademas el
; codigo, y ese no puede vivir en el repositorio porque es un secreto. Lo hornea
; deploy\build-agent-preconfigured.ps1.
#ifndef DefaultServer
  #define DefaultServer "192.168.1.10"
#endif
#ifndef DefaultPort
  #define DefaultPort "5443"
#endif
#ifndef DefaultCode
  #define DefaultCode ""
#endif
#ifndef DefaultPin
  #define DefaultPin ""
#endif
#ifndef DefaultUpdateShare
  #define DefaultUpdateShare "\\192.168.1.10\updates\Shared\DeviceHub\production"
#endif
#ifndef DefaultThumbprint
  #define DefaultThumbprint ""
#endif

[Setup]
AppId={{8F3D2A61-4C7E-4B19-9E5A-DEVICEHUBAGENT}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={commonpf}\ILSAN\DeviceHub Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installers
OutputBaseFilename=DeviceHubAgent-setup-{#AppVersion}
; El icono del propio instalador y el que queda en Programas y caracteristicas.
SetupIconFile=..\assets\devicehub.ico
UninstallDisplayIcon={app}\DeviceHub.Agent.exe
Compression=lzma2/max
SolidCompression=yes
; El agente corre como LocalSystem y crea un servicio: sin admin no hay nada que hacer.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayName={#AppName}
WizardStyle=modern

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "..\artifacts\agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; El servicio NO se crea aqui: las entradas [Run] se procesan en un momento que
; no esta garantizado respecto a CurStepChanged(ssPostInstall), donde se escribe
; appsettings.json. El agente arrancaba ANTES de tener configuracion y se quedaba
; girando con "sin codigo de enrolamiento" aunque el archivo lo tuviera.
;
; Se hace todo en [Code], en orden explicito: crear, configurar, arrancar.

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
var
  PaginaConfig: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  PaginaConfig := CreateInputQueryPage(wpSelectDir,
    'Conexion con el servidor DeviceHub',
    'A que servidor se conecta este equipo',
    'Es la PC donde instalaste DeviceHub Server (el servicio que escucha en el' + #13#10 +
    'puerto 5443). NO es esta PC, NI el servidor de MySQL.' + #13#10#13#10 +
    'El codigo de enrolamiento se genera en el dashboard y vence en 30 minutos;' + #13#10 +
    'el pin esta en pin.txt, dentro de C:\ProgramData\ILSANSYSTEM\DeviceHubServer' + #13#10 +
    'de esa misma PC.');

  PaginaConfig.Add('PC con DeviceHub Server (host o IP):', False);
  PaginaConfig.Add('Puerto:', False);
  PaginaConfig.Add('Codigo de enrolamiento:', False);
  PaginaConfig.Add('Pin SPKI del servidor (opcional):', False);

  PaginaConfig.Values[0] := ExpandConstant('{param:SERVER|{#DefaultServer}}');
  PaginaConfig.Values[1] := ExpandConstant('{param:PORT|{#DefaultPort}}');
  PaginaConfig.Values[2] := ExpandConstant('{param:CODE|{#DefaultCode}}');
  PaginaConfig.Values[3] := ExpandConstant('{param:PIN|{#DefaultPin}}');
end;

{ Con todo horneado no hay nada que preguntar: se salta la pagina y el instalador
  queda en "siguiente, siguiente" incluso sin /VERYSILENT. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PaginaConfig <> nil) and (PageID = PaginaConfig.ID)
    and ('{#DefaultServer}' <> '') and ('{#DefaultCode}' <> '');
end;

function ObtenerValor(Indice: Integer; Parametro, PorDefecto: String): String;
begin
  { En silencioso, o con la pagina saltada por venir todo horneado, Values esta
    vacio: mandan el parametro de linea de comandos y luego el valor de
    compilacion. }
  if WizardSilent or ShouldSkipPage(PaginaConfig.ID) then
    Result := ExpandConstant('{param:' + Parametro + '|' + PorDefecto + '}')
  else
    Result := PaginaConfig.Values[Indice];

  if Result = '' then
    Result := PorDefecto;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (PaginaConfig <> nil) and (CurPageID = PaginaConfig.ID) then
  begin
    if Trim(PaginaConfig.Values[0]) = '' then
    begin
      MsgBox('Indica el servidor de DeviceHub.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if Trim(PaginaConfig.Values[2]) = '' then
    begin
      { Sin codigo el agente arranca pero no puede registrarse: mejor avisar ahora
        que dejar el servicio girando en vacio en una PC de planta. }
      if MsgBox('Sin codigo de enrolamiento el agente no podra registrarse.' + #13#10 +
                'Puedes anadirlo despues en appsettings.json.' + #13#10#13#10 +
                'Continuar de todos modos?', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

function EscaparJson(const Texto: String): String;
begin
  Result := Texto;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure EscribirConfiguracion;
var
  Json, Pin, PinLinea: String;
begin
  Pin := Trim(ObtenerValor(3, 'PIN', '{#DefaultPin}'));

  if Pin <> '' then
    PinLinea := '"' + EscaparJson(Pin) + '"'
  else
    PinLinea := '';

  Json :=
    '{' + #13#10 +
    '  "Logging": { "LogLevel": { "Default": "Information" } },' + #13#10 +
    '  "DeviceHub": {' + #13#10 +
    '    "ServerHost": "' + EscaparJson(Trim(ObtenerValor(0, 'SERVER', '{#DefaultServer}'))) + '",' + #13#10 +
    '    "ServerPort": ' + ObtenerValor(1, 'PORT', '{#DefaultPort}') + ',' + #13#10 +
    '    "HeartbeatSeconds": 30,' + #13#10 +
    '    "DataDirectory": "C:\\ProgramData\\ILSANSYSTEM\\DeviceHub",' + #13#10 +
    '    "EnrollmentCode": "' + EscaparJson(Trim(ObtenerValor(2, 'CODE', '{#DefaultCode}'))) + '",' + #13#10 +
    '    "PinnedKeys": [' + PinLinea + '],' + #13#10 +
    '    "UpdateShare": "' + EscaparJson(ExpandConstant('{param:UPDATESHARE|\\192.168.1.10\updates\Shared\DeviceHub\production}')) + '",' + #13#10 +
    '    "UpdatePublisherThumbprint": "' + EscaparJson(ExpandConstant('{param:THUMBPRINT|}')) + '",' + #13#10 +
    '    "UpdateCheckHours": 6' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;

  SaveStringToFile(ExpandConstant('{app}\appsettings.json'), Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Codigo: Integer;
begin
  { Antes de copiar archivos hay que soltar el ejecutable: si el servicio sigue
    vivo, la actualizacion falla con "archivo en uso". }
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
    Sleep(3000);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
    Sleep(1000);
  end;

  if CurStep = ssPostInstall then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'),
      ExpandConstant('create {#ServiceName} binPath= "{app}\DeviceHub.Agent.exe" start= auto obj= LocalSystem DisplayName= "{#AppName}"'),
      '', SW_HIDE, ewWaitUntilTerminated, Codigo);

    Exec(ExpandConstant('{sys}\sc.exe'),
      'description {#ServiceName} "Agente de inventario, monitoreo y administracion de ILSAN DeviceHub."',
      '', SW_HIDE, ewWaitUntilTerminated, Codigo);

    { Reinicio automatico: un agente caido deja la PC invisible en el dashboard. }
    Exec(ExpandConstant('{sys}\sc.exe'),
      'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000',
      '', SW_HIDE, ewWaitUntilTerminated, Codigo);

    { La configuracion ANTES de arrancar. }
    EscribirConfiguracion;

    Exec(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if not UninstallSilent then
      MsgBox('La identidad de esta maquina se conservo en' + #13#10 +
             'C:\ProgramData\ILSANSYSTEM\DeviceHub' + #13#10#13#10 +
             'Al reinstalar, la PC mantendra su machineId y su historial.' + #13#10 +
             'Para retirar el equipo definitivamente usa:' + #13#10 +
             '  deploy\uninstall.ps1 -Component agent -RemoveIdentity',
             mbInformation, MB_OK);
end;
