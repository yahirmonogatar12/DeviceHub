# ILSAN DeviceHub

Plataforma interna para inventariar, monitorear y administrar las PCs de planta.
El control remoto es **un modulo mas**, no el producto.

Estado actual: **Fases 0-15** implementadas. Identidad de maquina, heartbeat
sobre stream gRPC persistente, historial de IP y de ubicacion, dashboard WPF,
inventario de hardware, monitoreo, administracion remota (comandos con TTL e
idempotencia, procesos y servicios), control remoto con sesiones, y auditoria
transaccional.

**Avance completo, decisiones y lo que falta: [docs/roadmap.md](docs/roadmap.md).**

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
deploy/                     publicacion e instalacion de servicios
docs/                       roadmap, arquitectura, protocolo, seguridad
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

## Diagnostico en campo

Que ve DeviceHub en esta PC, sin instalar el servicio ni levantar el servidor:

```powershell
DeviceHub.Agent.exe --inventory   # CPU, RAM, discos, GPU, BIOS, Windows
DeviceHub.Agent.exe --metrics     # tres muestras de CPU/RAM/disco/red
```

Desde el servidor, sin abrir el dashboard:

```powershell
DeviceHub.Server.exe --enrollment-code --uses 5 --minutes 30
DeviceHub.Server.exe --command Ping --machine M1-FCT-01
DeviceHub.Server.exe --command RestartService --machine M1-FCT-01 --param service=MySQL80
```

## Administracion remota

Comandos con lista cerrada (`enum`, nunca texto libre) y una politica por tipo:
rol minimo, si es destructivo, si admite reintento, TTL y timeout.

| Comando | Rol minimo | TTL |
|---|---|---|
| `Ping` | Viewer | 2 min |
| `GetProcesses`, `GetServices` | Technician | 2 min |
| `KillProcess` | Technician | 2 min |
| `StartService`, `StopService`, `RestartService` | Engineer | 2 min |
| `RestartMachine` | Engineer | **30 s** |
| `ShutdownMachine` | Administrator | **30 s** |

El TTL corto de los dos ultimos es el punto: una PC apagada dos horas **no** debe
reiniciarse al reconectar porque alguien lo pidio hace dos horas. El agente
comprueba el vencimiento antes de ejecutar; el servidor solo lo refleja.

La idempotencia tiene dos mitades. El servidor guarda cada comando por
`commandId`, y el agente lleva un journal en SQLite: si la conexion cae despues
de ejecutar pero antes de reportar, al reconectar responde el resultado guardado
en vez de ejecutar otra vez. Sin eso, una reconexion provoca dos reinicios.

## Control remoto

El dashboard muestra **`[ CONTROLAR PC ]`** y nada mas: el identificador del
motor remoto no se le enseña al tecnico. El servidor autoriza (Technician+),
registra la sesion en `machine_sessions` y devuelve que ejecutar; el cliente
corre en la PC del tecnico.

Nada fuera de `RustDeskDetector` (agente) y `RustDeskProvider` (servidor) sabe
que hay RustDesk detras: el contrato, la base y la UI manejan `provider` y
`device_id` como texto opaco. Cambiar de motor es cambiar una linea de registro
de dependencias.

El agente obtiene su identificador preguntandole al programa
(`rustdesk.exe --get-id`) en vez de parsear su archivo de configuracion, cuya
ruta y formato cambian entre versiones. Si no esta instalado, la maquina aparece
sin control remoto disponible -- no es un error.

**DeviceHub no guarda la contrasena del motor remoto.** Una tabla con las claves
de acceso remoto de toda la planta no es algo que se resuelva de paso: exigiria
cifrado en reposo, rotacion y auditoria propia. El tecnico la introduce, o se
configura acceso desatendido en el propio RustDesk.

## Auditoria

**Si no se audita, no se ejecuta.** No es un lema: la fila de auditoria se
escribe en la **misma transaccion** que la accion, asi que si ese INSERT falla,
el rollback se lleva tambien el comando. No hay forma de dejar ejecutado algo sin
rastro de quien lo pidio.

`machine_audit` es la unica tabla **sin foreign key** a `machines`. Todas las
demas tienen `ON DELETE CASCADE`, asi que borrar un equipo se lleva sus metricas
e historiales -- correcto para datos operativos. Para la auditoria seria
convertir "borrar la maquina" en "borrar el rastro", asi que guarda
`machine_code` y `site_code` como copia de texto y **no tiene purga**.

Los intentos **denegados** se auditan igual que los permitidos. Que alguien sin
permisos intentara apagar una PC es exactamente lo que hay que poder ver despues.

Cada fila lleva `request_id` (el `TraceIdentifier` de la peticion), que
correlaciona todo lo ocurrido en una misma llamada.

## Usuarios y limites

| Rol | Puede |
|---|---|
| `viewer` | ver equipos, `Ping` |
| `technician` | + procesos, matar procesos, control remoto |
| `engineer` | + servicios, reiniciar la maquina |
| `administrator` | + apagar, mover/renombrar, usuarios, codigos de enrolamiento |

El administrador crea usuarios desde el dashboard. **No se puede degradar ni
desactivar al ultimo administrador activo**: dejaria el sistema sin forma de
crear usuarios ni resolver conflictos de identidad salvo reescribiendo la base a
mano.

Contrasenas: minimo 12 caracteres y sin palabras obvias. Se exige longitud en
vez de un zoo de simbolos porque `Ilsan2026!` cumple cualquier regla de
mayusculas-numeros-simbolos y es adivinable; una frase larga no.

Limites (ventana deslizante, en memoria):

- **5 intentos de login** por usuario y origen cada 5 min. Registrar los fallos
  no los detiene: sin limite, se pueden probar contrasenas tan rapido como
  aguante la red y lo unico que aporta DeviceHub es constancia detallada.
- **30 comandos por minuto y maquina**. Acota un bucle en la UI o un script mal
  escrito; nadie encola mil reinicios sobre el mismo equipo.

## Inventario de hardware

CPU, RAM, discos, GPU, placa, BIOS, serial y version de Windows. **No viaja en el
heartbeat**: se envia al conectar, cuando el hash del contenido cambia, y como
mucho cada 12 horas.

Dos exclusiones deliberadas para que el hash no cambie por ruido:

- **discos USB y removibles**: en planta las memorias entran y salen todo el dia,
  y contarlas generaria un cambio de hardware falso en cada una;
- **espacio libre**: cambia a cada minuto, asi que es una metrica (Fase 6), no
  inventario. Incluirlo convertiria el inventario en un heartbeat caro.

Cuando el hardware si cambia de verdad (mas RAM, disco nuevo, actualizacion de
Windows) queda un evento `HARDWARE_CHANGED` en `machine_events`.

## Monitoreo

```
Agente --> muestra cada 5 s (CPU, RAM, disco, red)
       --> agrega por minuto (promedio Y pico)
       --> buffer SQLite local
       --> envia el lote cada 60 s por el stream que ya existe
Servidor --> machine_metrics, purga a los 30 dias
```

Granularidad de un minuto, nunca de un segundo: a 200 PCs serian 17 millones de
filas diarias. Se guarda promedio **y** maximo porque un promedio del 40% puede
esconder un pico sostenido al 100%, que es justo lo que se busca cuando alguien
reporta que una estacion va lenta. Del disco se reporta el mas apretado, no la
media: un `C:` al 5% no debe quedar escondido tras un `D:` al 90%.

El muestreo corre **al margen de la conexion**. Los minutos en que el servidor
esta caido son precisamente los que hay que conservar, y para eso esta el buffer
SQLite: sobrevive al reinicio del servicio y se drena al reconectar. Esta acotado
a 24 h para que un agente desconectado una semana no llene el disco de la PC.

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
