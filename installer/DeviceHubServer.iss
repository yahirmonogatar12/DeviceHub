; Instalador del servidor y el dashboard de ILSAN DeviceHub.
;
; Van juntos porque se instalan pocas veces y a mano; el agente tiene su propio
; instalador porque va a decenas de PCs y pesa la cuarta parte.
;
; La cadena de conexion NO se guarda en appsettings.json. Se escribe en el bloque
; de entorno DEL SERVICIO (HKLM\...\Services\DeviceHubServer\Environment), que
; ademas evita el problema de que una variable de maquina recien creada no la vea
; un servicio hasta reiniciar Windows.

#define AppName "ILSAN DeviceHub"
; Guardado con ifndef: un #define plano pisa el /DAppVersion de la linea de
; comandos, y el instalador salia siempre marcado como 1.0.0 aunque se pidiera
; otra version -- con el numero equivocado en Programas y caracteristicas.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define ServiceName "DeviceHubServer"
#define Publisher "ILSAN"

[Setup]
AppId={{2B7C4E19-8A3F-4D62-B10C-DEVICEHUBSERVER}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={commonpf}\ILSAN\DeviceHub
DefaultGroupName=ILSAN DeviceHub
OutputDir=..\artifacts\installers
OutputBaseFilename=DeviceHubServer-setup-{#AppVersion}
; El icono del propio instalador y el que queda en Programas y caracteristicas.
SetupIconFile=..\assets\devicehub.ico
UninstallDisplayIcon={app}\Dashboard\DeviceHub.Dashboard.exe
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Types]
Name: "completo"; Description: "Servidor y dashboard"
Name: "servidor"; Description: "Solo el servidor"
Name: "dashboard"; Description: "Solo el dashboard (PC del tecnico)"
Name: "custom"; Description: "Personalizada"; Flags: iscustom

[Components]
Name: "servidor"; Description: "Servidor DeviceHub (servicio de Windows)"; Types: completo servidor
Name: "dashboard"; Description: "Dashboard de administracion"; Types: completo dashboard

[Files]
Source: "..\artifacts\server\*"; DestDir: "{app}\Server"; Components: servidor; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\dashboard\*"; DestDir: "{app}\Dashboard"; Components: dashboard; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DeviceHub Dashboard"; Filename: "{app}\Dashboard\DeviceHub.Dashboard.exe"; Components: dashboard
Name: "{commondesktop}\DeviceHub Dashboard"; Filename: "{app}\Dashboard\DeviceHub.Dashboard.exe"; Components: dashboard

; El servicio NO se crea aqui.
;
; Las entradas [Run] se procesan en un momento que no esta garantizado respecto a
; CurStepChanged(ssPostInstall), que es donde se escribe la configuracion. El
; servicio arrancaba ANTES de que existiera la cadena de conexion y moria con
; "Falta la cadena de conexion", dejando un servicio en estado Stopped y ningun
; indicio de que el instalador hubiera hecho algo mal.
;
; Ahora se hace todo en [Code], en orden explicito: crear, configurar, arrancar.
[Run]
Filename: "{app}\Dashboard\DeviceHub.Dashboard.exe"; Description: "Abrir el dashboard"; Components: dashboard; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
var
  PaginaServidor: TInputQueryWizardPage;
  PaginaDashboard: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  PaginaServidor := CreateInputQueryPage(wpSelectComponents,
    'Base de datos',
    'Conexion al MySQL central',
    'Crea antes el schema y un usuario limitado a el:' + #13#10 +
    '  CREATE DATABASE devicehub CHARACTER SET utf8mb4;' + #13#10 +
    '  GRANT ALL PRIVILEGES ON `devicehub`.* TO ''devicehub''@''%'';');

  PaginaServidor.Add('Servidor MySQL:', False);
  PaginaServidor.Add('Base de datos:', False);
  PaginaServidor.Add('Usuario:', False);
  PaginaServidor.Add('Contrasena:', True);
  PaginaServidor.Add('Puerto gRPC de DeviceHub:', False);

  PaginaServidor.Values[0] := ExpandConstant('{param:DBHOST|192.168.1.10}');
  PaginaServidor.Values[1] := ExpandConstant('{param:DBNAME|devicehub}');
  PaginaServidor.Values[2] := ExpandConstant('{param:DBUSER|devicehub}');
  PaginaServidor.Values[3] := ExpandConstant('{param:DBPASS|}');
  PaginaServidor.Values[4] := ExpandConstant('{param:PORT|5443}');

  PaginaDashboard := CreateInputQueryPage(PaginaServidor.ID,
    'Dashboard',
    'A que servidor DeviceHub se conecta',
    'El pin SPKI lo imprime el servidor en su log al arrancar. Si lo dejas vacio,' + #13#10 +
    'el dashboard confiara en el primer certificado que vea.');

  PaginaDashboard.Add('Servidor DeviceHub:', False);
  PaginaDashboard.Add('Puerto:', False);
  PaginaDashboard.Add('Pin SPKI (opcional):', False);

  PaginaDashboard.Values[0] := ExpandConstant('{param:SERVER|localhost}');
  PaginaDashboard.Values[1] := ExpandConstant('{param:PORT|5443}');
  PaginaDashboard.Values[2] := ExpandConstant('{param:PIN|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if (PaginaServidor <> nil) and (PageID = PaginaServidor.ID) then
    Result := not WizardIsComponentSelected('servidor');

  if (PaginaDashboard <> nil) and (PageID = PaginaDashboard.ID) then
    Result := not WizardIsComponentSelected('dashboard');
end;

function EsNumero(const Texto: String): Boolean;
var
  i: Integer;
begin
  Result := Length(Trim(Texto)) > 0;

  for i := 1 to Length(Trim(Texto)) do
    if (Trim(Texto)[i] < '0') or (Trim(Texto)[i] > '9') then
      Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (PaginaServidor <> nil) and (CurPageID = PaginaServidor.ID) then
  begin
    if Trim(PaginaServidor.Values[3]) = '' then
    begin
      MsgBox('Falta la contrasena de MySQL. Sin ella el servicio no arrancara.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    { Un puerto vacio o con letras generaba un appsettings.json roto, y entonces
      el servicio o el dashboard fallaban al arrancar sin explicar por que. }
    if not EsNumero(PaginaServidor.Values[4]) then
    begin
      MsgBox('El puerto debe ser un numero.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if (PaginaDashboard <> nil) and (CurPageID = PaginaDashboard.ID) then
    if not EsNumero(PaginaDashboard.Values[1]) then
    begin
      MsgBox('El puerto debe ser un numero.', mbError, MB_OK);
      Result := False;
    end;
end;

function EscaparJson(const Texto: String): String;
begin
  Result := Texto;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure ConfigurarServidor;
var
  Cadena: String;
begin
  Cadena := 'Server=' + Trim(PaginaServidor.Values[0]) +
            ';Port=3306' +
            ';Database=' + Trim(PaginaServidor.Values[1]) +
            ';Uid=' + Trim(PaginaServidor.Values[2]) +
            ';Pwd=' + PaginaServidor.Values[3] + ';';

  { En el entorno DEL SERVICIO, no en una variable de maquina: una variable recien
    creada no la ve un servicio hasta que Windows refresca su bloque de entorno, y
    ademas asi el secreto queda acotado a este servicio. }
  RegWriteMultiStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Services\{#ServiceName}',
    'Environment',
    'DEVICEHUB_DB_CONNECTION=' + Cadena);

  SaveStringToFile(ExpandConstant('{app}\Server\appsettings.json'),
    '{' + #13#10 +
    '  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },' + #13#10 +
    '  "DeviceHub": {' + #13#10 +
    '    "Port": ' + Trim(PaginaServidor.Values[4]) + ',' + #13#10 +
    '    "DataDirectory": "C:\\ProgramData\\ILSANSYSTEM\\DeviceHubServer",' + #13#10 +
    '    "ConnectionString": "",' + #13#10 +
    '    "DefaultSiteCode": "ILSAN-MTY",' + #13#10 +
    '    "JwtIssuer": "devicehub",' + #13#10 +
    '    "JwtHours": 12,' + #13#10 +
    '    "MetricsRetentionDays": 30' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10, False);
end;

procedure ConfigurarDashboard;
begin
  SaveStringToFile(ExpandConstant('{app}\Dashboard\appsettings.json'),
    '{' + #13#10 +
    '  "DeviceHub": {' + #13#10 +
    '    "ServerHost": "' + EscaparJson(Trim(PaginaDashboard.Values[0])) + '",' + #13#10 +
    '    "ServerPort": ' + Trim(PaginaDashboard.Values[1]) + ',' + #13#10 +
    '    "ServerPin": "' + EscaparJson(Trim(PaginaDashboard.Values[2])) + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10, False);
end;

procedure AbrirFirewall;
var
  Codigo: Integer;
begin
  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="ILSAN DeviceHub Server"',
    '', SW_HIDE, ewWaitUntilTerminated, Codigo);

  Exec(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="ILSAN DeviceHub Server" dir=in action=allow protocol=TCP localport=' +
      Trim(PaginaServidor.Values[4]),
    '', SW_HIDE, ewWaitUntilTerminated, Codigo);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Codigo: Integer;
begin
  if CurStep = ssInstall then
  begin
    { Soltar el ejecutable antes de sobrescribirlo. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
    Sleep(3000);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
    Sleep(1000);
  end;

  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('servidor') then
    begin
      { ORDEN OBLIGATORIO: crear el servicio, configurarlo y SOLO ENTONCES
        arrancarlo. Al reves arranca sin cadena de conexion y muere con
        "Falta la cadena de conexion", dejando un servicio Stopped y ninguna
        pista de que el instalador hizo algo mal. }
      Exec(ExpandConstant('{sys}\sc.exe'),
        ExpandConstant('create {#ServiceName} binPath= "{app}\Server\DeviceHub.Server.exe" start= auto DisplayName= "ILSAN DeviceHub Server"'),
        '', SW_HIDE, ewWaitUntilTerminated, Codigo);

      Exec(ExpandConstant('{sys}\sc.exe'),
        'description {#ServiceName} "Servidor central de ILSAN DeviceHub (gRPC + MySQL)."',
        '', SW_HIDE, ewWaitUntilTerminated, Codigo);

      Exec(ExpandConstant('{sys}\sc.exe'),
        'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000',
        '', SW_HIDE, ewWaitUntilTerminated, Codigo);

      ConfigurarServidor;
      AbrirFirewall;

      Exec(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Codigo);

      { Migrar y generar el certificado tarda unos segundos. }
      Sleep(8000);

      if not FileExists('C:\ProgramData\ILSANSYSTEM\DeviceHubServer\pin.txt') then
        MsgBox('El servidor quedo instalado pero NO termino de arrancar.' + #13#10#13#10 +
               'Casi siempre es la conexion a MySQL. Revisa el detalle con:' + #13#10#13#10 +
               '  Get-EventLog -LogName Application -Newest 20 |' + #13#10 +
               '    Where-Object Message -match ''DeviceHub'' | Format-List',
               mbError, MB_OK)
      else
        MsgBox('Servidor arrancado correctamente.' + #13#10#13#10 +
               'El pin SPKI que necesitan los agentes y el dashboard esta en:' + #13#10 +
               'C:\ProgramData\ILSANSYSTEM\DeviceHubServer\pin.txt',
               mbInformation, MB_OK);
    end;

    if WizardIsComponentSelected('dashboard') then
      ConfigurarDashboard;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Codigo: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="ILSAN DeviceHub Server"',
      '', SW_HIDE, ewWaitUntilTerminated, Codigo);

    if not UninstallSilent then
      MsgBox('El certificado y la clave JWT se conservaron en' + #13#10 +
             'C:\ProgramData\ILSANSYSTEM\DeviceHubServer' + #13#10#13#10 +
             'Borrarlos cambiaria el pin SPKI y TODOS los agentes dejarian de' + #13#10 +
             'conectar hasta recibir un recovery code.' + #13#10#13#10 +
             'La base de datos devicehub tampoco se toca.',
             mbInformation, MB_OK);
  end;
end;
