# Instalacion paso a paso

De cero a cinco PCs en el dashboard. Los tiempos son reales, no optimistas.

---

## 0. Antes de empezar

| Necesitas | Donde |
|---|---|
| MySQL 8 accesible | `192.168.1.10:3306` |
| Una PC que haga de servidor | encendida siempre, IP fija |
| Permiso de administrador | en el servidor y en cada PC de planta |
| Puerto 5443 libre | entre las PCs y el servidor |

El servidor **no** necesita salida a internet. Los agentes solo necesitan
alcanzar el puerto 5443 del servidor.

---

## 1. Crear el schema y el usuario (una vez, 2 min)

Conectate al MySQL con una cuenta administradora y ejecuta:

```sql
CREATE DATABASE devicehub CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER 'devicehub'@'%' IDENTIFIED BY 'PON-AQUI-UNA-CLAVE-LARGA';
GRANT ALL PRIVILEGES ON `devicehub`.* TO 'devicehub'@'%';
FLUSH PRIVILEGES;
```

**Usa un usuario dedicado, no `mes_admin`.** `ALL PRIVILEGES` **solo sobre
`devicehub`** significa que este usuario no ve `mes_production` ni existiendo. Si
DeviceHub corriera con una cuenta de permisos globales, un error en una migracion
dejaria de ser "se rompio DeviceHub" para ser un incidente de produccion.

El `CREATE DATABASE` lo hace un administrador porque el usuario limitado, por
diseño, no puede crear bases. Las tablas las crea DeviceHub solo al arrancar.

---

## 2. Compilar los instaladores (5 min)

En tu PC de desarrollo:

```powershell
cd ILSANNET
.\deploy\build-installers.ps1 -Version 1.0.0
```

Salen dos archivos en `%LOCALAPPDATA%\ILSAN\DeviceHub-installers\`:

| Archivo | Tamaño | Para |
|---|---|---|
| `DeviceHubServer-setup-1.0.0.exe` | ~78 MB | servidor y dashboard |
| `DeviceHubAgent-setup-1.0.0.exe` | ~28 MB | cada PC de planta |

Son **self-contained**: las PCs no necesitan tener .NET instalado.

> Si falta el compilador: `winget install JRSoftware.InnoSetup`
>
> La salida va **fuera de la carpeta de OneDrive** a proposito. Compilando dentro
> de una carpeta sincronizada, su filtro retiene el `.exe` recien creado e Inno
> falla con *"el proceso no tiene acceso al archivo"* -- un error que no menciona
> OneDrive por ningun lado. Con `-Output` puedes cambiarla.

---

## 3. Instalar el servidor (5 min)

En la PC servidor, ejecuta `DeviceHubServer-setup-1.0.0.exe` como administrador.

1. Tipo de instalacion: **Servidor y dashboard**
2. Rellena los datos de MySQL del paso 1
3. Puerto gRPC: `5443`
4. En la pagina del dashboard, deja el pin vacio **por ahora** (aun no existe)

El instalador crea el servicio, lo arranca, abre el firewall y guarda la cadena
de conexion en el entorno del propio servicio -- no en un archivo de texto.

Al arrancar, el servidor crea las tablas y genera su certificado.

### Recoge los dos valores del primer arranque

**El pin SPKI** (no es secreto, hay que repartirlo):

```powershell
type C:\ProgramData\ILSANSYSTEM\DeviceHubServer\pin.txt
```

**La contrasena inicial de `admin`** (esta si es secreta y se muestra una sola vez):

```powershell
Get-EventLog -LogName Application -Source DeviceHubServer -Newest 30 |
  Where-Object Message -match 'password' | Select-Object -First 1 -Expand Message
```

Si no aparece nada, comprueba que el servicio arranco:

```powershell
Get-Service DeviceHubServer
Get-EventLog -LogName Application -Source DeviceHubServer -Newest 20 |
  Select-Object TimeGenerated, Message | Format-List
```

El fallo mas comun es la cadena de conexion. El servicio lo dice claro:
*"No se pudo preparar la base de datos"*.

---

## 4. Configurar el dashboard (2 min)

Edita `C:\Program Files\ILSAN\DeviceHub\Dashboard\appsettings.json` y pon el pin
del paso anterior:

```json
{
  "DeviceHub": {
    "ServerHost": "localhost",
    "ServerPort": 5443,
    "ServerPin": "el-pin-de-pin.txt"
  }
}
```

Abre el acceso directo **DeviceHub Dashboard** y entra como `admin` con la
contrasena del log.

### Lo primero: cambiar esa contrasena

Nacio aleatoria y quedo escrita en el registro de eventos. Cambiala desde el
dashboard antes de seguir.

### Crear los usuarios reales

| Rol | Para quien |
|---|---|
| `viewer` | quien solo consulta el estado |
| `technician` | soporte: procesos y control remoto |
| `engineer` | mantenimiento: servicios, reinicios, terminal |
| `administrator` | tu |

Nadie deberia trabajar a diario como `admin`. La auditoria distingue quien hizo
que, y con una sola cuenta compartida esa informacion no existe.

---

## 5. Generar el codigo de enrolamiento (1 min)

En el dashboard: **Generar codigo de enrolamiento**. Sale algo como
`ENROLL-8K2F-A91X`, se copia solo al portapapeles y **vence en 30 minutos**.

Para una tanda de 5 PCs, desde el servidor:

```powershell
cd "C:\Program Files\ILSAN\DeviceHub\Server"
.\DeviceHub.Server.exe --enrollment-code --uses 5 --minutes 60
```

Un codigo de un solo uso por PC es mas seguro; uno con varios usos es mas
practico para una tanda. Los dos caducan.

---

## 6. Instalar el agente en cada PC (2 min por PC)

### Interactivo

Ejecuta `DeviceHubAgent-setup-1.0.0.exe` como administrador y rellena:

- **Servidor**: IP o nombre de la PC servidor
- **Puerto**: `5443`
- **Codigo de enrolamiento**: el del paso 5
- **Pin SPKI**: el de `pin.txt`

### Silencioso (para varias PCs)

```powershell
.\DeviceHubAgent-setup-1.0.0.exe /VERYSILENT `
  /SERVER=192.168.1.20 `
  /CODE=ENROLL-8K2F-A91X `
  /PIN="el-pin-de-pin.txt"
```

Parametros opcionales: `/PORT=5443`, `/UPDATESHARE=...`, `/THUMBPRINT=...`

**Pasa siempre el `/PIN`.** Sin el, el agente confia en el primer certificado que
ve, y esa ventana es justo el momento de la instalacion.

La PC aparece en el dashboard en menos de 30 segundos.

---

## 7. Comprobar que funciona de verdad (15 min)

Compilar no es funcionar. Con las 5 PCs instaladas, prueba esto:

| Prueba | Que debe pasar |
|---|---|
| Cambiar la IP de una PC | mismo `machineId`, fila nueva en el historial de IP |
| Reiniciar una PC | vuelve sola, sin tocar nada |
| Apagar el servidor 10 min | los agentes reconectan solos al volver |
| Desconectar el cable de una | `UNREACHABLE` a los 90 s, `OFFLINE` a los 5 min |
| Reiniciar sin iniciar sesion en Windows | sigue reportando |
| Renombrar una PC desde el dashboard | el agente adopta el nombre sin tocar el equipo |
| `Ping` desde el dashboard | responde en segundos |

Las metricas tardan un minuto en aparecer: se agregan por minuto, no por segundo.

Si las cinco pasan, el nucleo esta solido.

---

## 8. Actualizaciones (cuando toque)

```powershell
.\deploy\publish-update.ps1 -Version 1.1.0 -Ring canary
```

Deja `canary` un dia en 2 PCs. Si aguanta, promociona:

```powershell
.\deploy\publish-update.ps1 -Version 1.1.0 -Ring production
```

Los agentes comprueban cada 6 h, se actualizan solos y **dan marcha atras** si la
version nueva no conecta en 5 minutos.

> **Antes de usar esto en serio: firma `DeviceHub.Agent.exe`** y pon su
> thumbprint en `UpdatePublisherThumbprint`. El SHA256 vive en el mismo recurso
> compartido que el paquete, asi que solo protege contra corrupcion: quien pueda
> **escribir** en `\\192.168.1.10\updates` ejecuta codigo como SYSTEM en todas
> las PCs. Mientras no haya firma, la unica proteccion es la ACL de ese recurso
> -- dejalo en solo lectura para todos menos para ti.

---

## Problemas frecuentes

**El servicio del servidor no arranca.**
Casi siempre es MySQL. `Get-EventLog -LogName Application -Source DeviceHubServer -Newest 5`.
Comprueba que el usuario `devicehub` entra desde la IP del servidor: el `GRANT`
es para `'devicehub'@'%'`, pero MySQL puede tener reglas de host mas estrictas.

**La PC no aparece en el dashboard.**
En esa PC: `Get-Service DeviceHubAgent`, y luego
`Get-EventLog -LogName Application -Source DeviceHubAgent -Newest 20`.
Lo mas comun es el codigo de enrolamiento vencido -- genera otro. Tambien
comprueba el puerto: `Test-NetConnection 192.168.1.20 -Port 5443`.

**Aparece `IDENTITY_CONFLICT`.**
Se clono una imagen de Windows que ya tenia el agente instalado. Resuelvelo desde
el dashboard: *Emitir identidad nueva* si de verdad son dos PCs distintas,
*Aprobar hardware nuevo* si solo le cambiaste la placa.

**Reinstale el agente y perdio su historial.**
Solo pasa si se borro `C:\ProgramData\ILSANSYSTEM\DeviceHub`. El desinstalador no
lo toca precisamente por esto.

**Todos los agentes dejaron de conectar a la vez.**
Se regenero el certificado del servidor. Los agentes tienen fijado el pin
anterior. Recupera cada uno con un *recovery code* desde el dashboard, o
restaura `C:\ProgramData\ILSANSYSTEM\DeviceHubServer\server.pfx` si tienes copia.
