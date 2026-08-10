# ILSAN DeviceHub

Plataforma interna para inventariar, monitorear y administrar las PCs de planta.
El control remoto es **un modulo mas**, no el producto.

Estado actual: **Fases 0-4** implementadas. Identidad de maquina, heartbeat sobre
stream gRPC persistente, historial de IP y de ubicacion, y dashboard WPF.
Las fases 5-18 estan disenadas pero no codificadas.

## Stack

| Capa | Tecnologia |
|---|---|
| Servidor | ASP.NET Core + Kestrel + gRPC, corre como Windows Service |
| Agente | .NET Worker Service (funciona sin sesion iniciada) |
| Dashboard | WPF + MVVM (CommunityToolkit.Mvvm) |
| Transporte | gRPC sobre HTTP/2 + TLS, stream bidireccional |
| BD central | MySQL 8, schema `devicehub` |
| Datos | MySqlConnector + Dapper; migraciones con DbUp |

## Estructura

```
src/DeviceHub.Contracts/    .proto compartido (cliente + servidor) y logica comun
src/DeviceHub.Server/       gRPC, MySQL, migraciones, identidad
src/DeviceHub.Agent/        servicio de Windows por PC
src/DeviceHub.Dashboard/    WPF
tests/DeviceHub.Tests/      xunit sobre la logica que puede romperse
database/migrations/        .sql embebidos en el servidor
docs/                       arquitectura, protocolo, seguridad
```

## Desplegar

```powershell
.\deploy\publish.ps1
```

Publica los tres en `artifacts\`, **self-contained**: las PCs de planta no
necesitan tener instalado el runtime de .NET. Copiar carpeta y listo.

### 1. Servidor (una vez)

```powershell
.\deploy\install-server.ps1 -ConnectionString "Server=mysql-host;Port=3306;Database=devicehub;Uid=devicehub;Pwd=...;"
```

Crea el servicio, la regla de firewall y el reinicio automatico. En el primer
arranque crea el schema `devicehub`, aplica las migraciones y **escribe en el log
dos cosas que no se vuelven a mostrar**:

- el **pin SPKI** del certificado,
- la password inicial de `admin`.

```powershell
Get-EventLog -LogName Application -Source DeviceHubServer -Newest 20
```

### 2. Dashboard (PC del tecnico)

Copiar `artifacts\dashboard\`, poner el pin y el host en `appsettings.json`, y
ejecutar `DeviceHub.Dashboard.exe`.

Entrar como `admin` -> **Generar codigo de enrolamiento**. El codigo
`ENROLL-XXXX-XXXX` vale 30 minutos y un solo uso (o varios usos para una tanda).

### 3. Agente (cada PC)

```powershell
.\deploy\install-agent.ps1 -Server devicehub-host -EnrollmentCode ENROLL-8K2F-A91X -Pin "XPg0rerx92cm..."
```

El agente genera su GUID permanente en
`C:\ProgramData\ILSANSYSTEM\DeviceHub\machine.json`, se enrola y aparece en el
dashboard en menos de 30 segundos.

Sin `-Pin` la instalacion confia en el primer certificado que ve (TOFU); pasarlo
cierra esa ventana.

### Desinstalar

```powershell
.\deploy\uninstall.ps1 -Component agent
```

Conserva la identidad por defecto: reinstalar sobre una PC conocida mantiene su
`machineId` y su historial. `-RemoveIdentity` pide confirmacion escrita.

### Correr desde codigo fuente

```powershell
$env:DEVICEHUB_DB_CONNECTION = "Server=...;Database=devicehub;Uid=...;Pwd=...;"
dotnet run --project src/DeviceHub.Server
dotnet run --project src/DeviceHub.Dashboard
```

## Tests

```powershell
dotnet test
```

Cubren lo que de verdad se rompe: seleccion de NIC primaria, transiciones del
historial de IP, umbrales de estado, deteccion de clonacion con fingerprint poco
fiable, y manejo de secretos.

## Reglas que el codigo respeta

- Todas las fechas se guardan en **UTC**; la conversion a local ocurre solo en WPF.
- El `machineId` es inmutable: renombrar o mover una PC conserva identidad e historial.
- El estado (`ONLINE`/`UNREACHABLE`/`OFFLINE`) se **deriva** de `last_seen`, no se almacena.
- Ningun secreto se guarda en claro: tokens y codigos hasheados, passwords con PBKDF2.
- Cada cambio de esquema es una migracion en `database/migrations/`.
