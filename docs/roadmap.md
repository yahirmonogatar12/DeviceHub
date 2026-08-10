# Roadmap y estado

Este archivo es la **fuente de verdad** del avance. Refleja lo que se construyo
de verdad, no el plan original: varias decisiones cambiaron al chocar con la
realidad y estan anotadas donde corresponde.

## Estado

**Vamos en la Fase 10 (Control remoto).** Fases 0 a 9 implementadas, con tests y
verificadas ejecutando contra el MySQL central.

| # | Fase | Estado |
|---|---|---|
| 0 | Esqueleto y documentacion | hecho |
| 1 | Machine Identity + seguridad base | hecho |
| 2 | Heartbeat sobre stream persistente | hecho |
| 3 | Base de datos | hecho |
| 4 | Dashboard WPF | hecho |
| 5 | Inventario de hardware | hecho |
| 6 | Monitoreo | hecho |
| 7 | Comandos | hecho |
| 8 | Procesos | hecho |
| 9 | Servicios | hecho |
| 10 | **Control remoto (RustDesk)** | **siguiente** |
| 11 | Sesiones remotas y permisos | pendiente |
| 12 | Auditoria | pendiente |
| 13 | Roles y hardening | pendiente |
| 14 | File Manager | pendiente |
| 15 | Terminal | pendiente |
| 16 | Auto-update del agente | pendiente |
| 17 | Integracion MES | pendiente |
| 18 | Benchmark de motores remotos | pendiente |

Fuera de la numeracion, tambien hecho: scripts de despliegue self-contained
(`deploy/`), verificacion end-to-end contra MySQL real, bootstrap sin GUI
(`--enrollment-code`) y CI en GitHub Actions (build + tests + regla UTC).

## End-to-end contra MySQL real: hecho

El schema `devicehub` existe en el MySQL central, con un usuario limitado a
`devicehub.*` (sin permisos globales: un error de migracion no puede tocar
`mes_production`). Las 9 migraciones se aplicaron y el flujo completo
**Agent -> Server -> MySQL** quedo verificado con
[`deploy/verify-endtoend.ps1`](../deploy/verify-endtoend.ps1): registro por
codigo de un solo uso, heartbeat, historial de IP, interfaces, inventario de
hardware y metricas por minuto.

**La prueba encontro dos bugs que ninguna prueba unitaria podia encontrar:**

1. **`CHAR(36)` se leia como `Guid`.** MySqlConnector mapea `CHAR(36)` a `Guid`
   por defecto, y `machine_id` se maneja como `string` en todo el sistema:
   Dapper reventaba con *"Object must implement IConvertible"* en cada lectura de
   maquina, y el agente reconectaba en bucle. Se fija `GuidFormat=None` en `Db`,
   no en la cadena de conexion: es una invariante del codigo, no algo que deba
   recordar quien despliega.

2. **La interfaz primaria caia en un fallback ciego.** Cuando la ruta no marcaba
   ninguna, se tomaba la primera de la lista. Contra hardware real eso eligio la
   IP de **Tailscale** (`100.x`); cambiarlo por "la primera con MAC real" eligio
   la de **VirtualBox** (`192.168.56.1`). Toda heuristica acaba escogiendo un
   adaptador virtual, asi que ahora **no se marca ninguna** y `current_ip` queda
   NULL. Una IP equivocada es peor que ninguna en un sistema cuyo proposito es
   saber que PC es cual.

Con el servidor apuntado a la IP LAN real, la seleccion por ruta acierta teniendo
cuatro adaptadores virtuales presentes:

| Interfaz | IP | primaria |
|---|---|---|
| Wi-Fi | 192.168.0.211 | **si** |
| Ethernet 2 (VirtualBox) | 192.168.56.1 | no |
| Tailscale | 100.111.108.116 | no |
| vEthernet (WSL Hyper-V) | 192.168.80.1 | no |
| vEthernet (WSLCore) | 172.27.32.1 | no |

Tambien se vio funcionar la **degradacion aprendida del fingerprint**: tras
registrar la misma PC fisica varias veces durante las pruebas, el servidor bajo
la confianza a `LOW` solo, al detectar el mismo fingerprint en 3 o mas maquinas.

### Lo que sigue faltando

Probarlo en **5 PCs reales de planta**, y sobre todo los escenarios que solo
existen ahi: cambio de IP, reinicio, servidor apagado, red desconectada, Windows
sin sesion iniciada, clonado de imagen y cambio de hostname.

---

## Fases hechas

Detalle tecnico en [architecture.md](architecture.md), [protocol.md](protocol.md)
y [security.md](security.md). Aqui solo lo que cambio respecto al plan.

**0 · Esqueleto** — 5 proyectos, no los 8 del plan. El agente es un solo proyecto
en vez de cuatro (`Service`/`Core`/`Windows`/`Tests`): un `classlib` sin un
segundo consumidor es andamiaje. Se dividira si aparece un agente Linux.

**1 · Machine Identity** — GUID permanente, deteccion de clonacion por dos vias,
codigos de enrolamiento de un solo uso y pin de clave publica. Todo en Fase 1 y
no en la 13, porque la regla propia decia "seguridad desde el comienzo".

**2 · Heartbeat** — la seleccion de NIC primaria no usa lista negra de
Hyper-V/VMware/Tailscale: le pregunta a Windows que interfaz tiene la ruta al
servidor. Tres lineas y cero mantenimiento cuando aparezca el proximo adaptador
virtual.

**3 · Base de datos** — `status` **no** es columna: se deriva de `last_seen`. Eso
elimina el servicio de fondo que tendria que refrescarla. `sites` existe desde el
principio y `machine_code` es unico **por sitio**, no global.

**4 · Dashboard** — `[Authorize(Roles=...)]` en vez del interceptor gRPC propio
que decia el plan. Es el mismo "un solo lugar por metodo", verificado por el
pipeline, con cero codigo nuestro. `StatusCalculator` acabo en `Contracts`
porque el dashboard tiene que recalcular el estado: una PC que se apaga no emite
ningun mensaje, y la UI se quedaria mostrando ONLINE para siempre.

**5 · Inventario** — sin SQLite, contra lo que decia el plan: lo unico a
persistir son dos campos y caben en `machine.json`. Se excluyen discos USB
(entran y salen todo el dia) y espacio libre (es metrica, no inventario) para que
el hash no cambie por ruido.

**6 · Monitoreo** — aqui si entra SQLite, que es donde se gana el puesto. CPU y
memoria por P/Invoke, no por `PerformanceCounter`: esto corre cada 5 s durante
meses, y `PerformanceCounter` falla en PCs con los contadores corruptos.

---

**7 · Comandos** — el TTL es lo que evita que una PC apagada dos horas ejecute al
reconectar un reinicio pedido hace dos horas: `RestartMachine` y
`ShutdownMachine` caducan a los 30 s. La idempotencia tiene dos mitades: el
servidor guarda cada comando por `commandId` y el agente lleva un journal en
SQLite, porque el caso peligroso incluye que el servicio se reiniciara entre
ejecutar y reportar. `CommandPolicy` concentra rol, si es destructivo, si admite
reintento, TTL y timeout: sin esa tabla, las diferencias entre "hacer ping" y
"apagar la maquina" acabarian repartidas en `if` por servidor, agente y UI.

**8 · Procesos** — el % de CPU por proceso se calcula con dos muestras separadas
500 ms: es un delta, no un valor que se pueda leer de golpe. El agente se niega a
matar el PID <= 4 y a matarse a si mismo, que dejaria la PC invisible y sin
rescate remoto.

**9 · Servicios** — `ServiceController` con Start/Stop/Restart. Reservado a
Engineer+: un tecnico no reinicia `MySQL80`.

---

## Fases pendientes

### 10 · Control remoto (RustDesk) — siguiente

```csharp
public interface IRemoteProvider {
    Task<RemoteInfo> GetConnectionInfoAsync(Guid machineId);
    Task<RemoteSession> LaunchAsync(Guid machineId, string userId);
}
```

Dos metodos, no quince. **Nada fuera del provider sabe que existe RustDesk**: el
contrato, la BD y el dashboard solo manejan `RemoteProvider` y `RemoteDeviceId`
como strings opacos. La deteccion de la ruta del `RustDesk2.toml` vive dentro de
`RustDeskDetector` y resuelve por descarte, porque cambia entre versiones.

hbbs/hbbr self-hosted en la LAN: ningun agente expuesto a Internet.

> Ojo con la Fase 5: si RustDesk instala su display virtual, aparecera como GPU
> nueva y disparara un `HARDWARE_CHANGED` en todas las PCs. Ver *Deuda deliberada*.

### 11 · Sesiones remotas y permisos

`machine_sessions` con inicio, fin y origen. Sesiones huerfanas se cierran por
timeout. Control remoto exige Technician+.

### 12 · Auditoria

Antes de terminal y de archivos, a proposito.

`machine_audit`: `timestamp`, `user_id`, `machine_id`, `action`, `source_ip`,
`details`. La escritura de auditoria va en la **misma transaccion** que la accion:
si no se audita, no se ejecuta.

### 13 · Roles y hardening

| Funcion | Viewer | Technician | Engineer | Admin |
|---|---|---|---|---|
| Ver equipo | si | si | si | si |
| Control remoto | no | si | si | si |
| Procesos | no | si | si | si |
| Reiniciar servicio | no | no | si | si |
| Terminal | no | no | si | si |
| Apagar equipo | no | no | no | si |

Mas: mTLS con certificado por agente, rate limiting, rotacion del certificado
disparada desde el dashboard (el procedimiento de 4 pasos ya esta en
[security.md](security.md)).

### 14 · File Manager

List / Download / Upload / Rename / Move / Delete, por streams gRPC con chunks.
Rutas protegidas normalizando con `Path.GetFullPath` **antes** de comparar, para
cerrar el paso a `..\`.

### 15 · Terminal

Ahora si, con auditoria y roles en pie. Nunca un `POST /execute` suelto: sesion
con `session_id`, `user_id`, `machine_id`, cada comando registrado con su salida,
y timeout de inactividad.

### 16 · Auto-update del agente

```
Server anuncia v1.4.0 -> descarga -> valida SHA256 + Authenticode
  -> updater desacoplado -> stop service -> reemplaza -> start -> reporta
```

**Un servicio no puede reemplazar su propio binario en ejecucion.** El updater
tiene que ser un proceso separado que sobreviva a la detencion del servicio. Si
eso no se disena asi, el auto-update no funciona y hay que ir PC por PC.

Rollback: conservar el binario anterior y restaurarlo si el nuevo no reporta
heartbeat en 5 minutos.

### 17 · Integracion MES

**MES no consulta tablas internas de DeviceHub.** Una VIEW de solo lectura
`devicehub.v_mes_machines` y un usuario con `GRANT SELECT` solo sobre ella. El
MES conoce unicamente `machine_id`. Asi el esquema interno puede cambiar y
DeviceHub puede mudarse de servidor sin tocar una query de produccion.

### 18 · Benchmark de motores remotos

Medir, no opinar. Mismo escenario para todos: misma LAN, mismo host, 1080p,
10 min por motor.

| Motor | Latencia | FPS | CPU host | CPU cliente | Conexion | Estable 1 h | Estable 8 h | Multi-monitor |
|---|---|---|---|---|---|---|---|---|
| RustDesk | | | | | | | | |
| MeshCentral | | | | | | | | |
| Sunshine/Moonlight | | | | | | | | |

Solo si los numeros lo justifican se evalua un motor propio. `IRemoteProvider` ya
permite enchufar al ganador sin tocar el resto.

---

## Deuda deliberada

Simplificaciones tomadas a conciencia, con el disparador para revisarlas.

| Simplificacion | Cuando revisarla |
|---|---|
| Adaptadores de video virtuales cuentan como hardware y disparan `HARDWARE_CHANGED` al cargarse | si la Fase 10 instala un display virtual en todas las PCs. No se filtran por nombre porque seria la misma lista negra que se evito para las NIC |
| Las metricas se borran del buffer tras escribir, sin esperar confirmacion | si perder un lote al caerse la conexion llega a importar. El servidor ya hace upsert por `(machine_id, minute)`, asi que pasar a at-least-once no duplicaria nada |
| `status` derivado de `last_seen`, sin columna ni sweeper | si hacen falta eventos "se cayo" para alertas |
| Pin de clave publica en vez de PKI | mTLS en Fase 13; la rotacion A->B ya esta definida |
| El conflicto de identidad lo resuelve un humano | si el volumen de conflictos genera ruido real |
| `sites` con una sola fila | la UI multi-planta se agrega cuando haya una segunda |
| Un solo proyecto para el agente | si aparece un agente Linux |
| `SQLitePCLRaw.bundle_e_sqlite3` fijado a mano en el `.csproj` | cuando `Microsoft.Data.Sqlite` actualice su transitiva (hoy trae una con CVE) |

## Reglas que siguen aplicando

1. Todo en **UTC**; la conversion a local solo en WPF.
2. El `machineId` es inmutable: renombrar o mover conserva identidad e historial.
3. Ningun secreto en claro: tokens y codigos hasheados, passwords con PBKDF2.
4. Cada cambio de esquema es una migracion en `database/migrations/`.
5. Ningun comando arbitrario sin autorizacion; todo comando administrativo se audita.
6. El agente nunca depende de la UI y funciona sin sesion iniciada.
7. No romper el contrato sin versionar el `package` del `.proto`.
8. Tests antes de las funciones criticas, y solo sobre lo que puede romperse.
