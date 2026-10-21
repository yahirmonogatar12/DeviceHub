# Seguridad

Requisito desde la Fase 1, no una mejora futura. Lo que sigue esta implementado.

## Identidad de maquina

GUID generado localmente en el primer arranque y guardado en
`C:\ProgramData\ILSANSYSTEM\DeviceHub\machine.json`, con ACL restringida a
`SYSTEM` y `Administradores`. El token de la maquina vive ahi cifrado con DPAPI
scope `LocalMachine`.

## Deteccion de clonacion

Un GUID en disco sobrevive a `sysprep` y a la clonacion de imagen. Dos detectores:

**1. Fingerprint de hardware** — SHA256 de UUID SMBIOS + serial de baseboard.
Es una senal fuerte, **no una verdad absoluta**: las placas industriales reportan
valores genericos. Por eso viaja con una confianza:

| Confianza | Cuando | Efecto |
|---|---|---|
| `HIGH` | ambos componentes validos | dispara `IDENTITY_CONFLICT` |
| `MEDIUM` | solo uno valido | no bloquea |
| `LOW` | ninguno valido | no se usa como detector |

Se descartan `00000000-...`, todo `F`, `To Be Filled By O.E.M.`,
`Default string`, `System Serial Number` y similares. Ademas, si el mismo
fingerprint aparece en **3 o mas maquinas distintas** el servidor lo degrada a
`LOW` solo: cubre el lote de placas que salio de fabrica con el mismo valor sin
tener que adivinar la lista completa por adelantado.

El conflicto solo se dispara si **ambos lados** son `HIGH`. Si el guardado era
`LOW`, la diferencia puede venir de un WMI que fallo.

**2. Streams concurrentes** — dos `Connect` vivos con el mismo `machineId` no
tienen explicacion legitima. No depende del hardware y funciona incluso con
confianza `LOW`. Es el detector que de verdad sostiene el sistema.

En conflicto se rechaza al agente entrante, el original sigue trabajando, y lo
resuelve **un humano** desde el dashboard: *Aprobar hardware nuevo* (cambio
legitimo de placa) o *Emitir identidad nueva* (era un clon). Deliberadamente no
se regenera el GUID de forma automatica: un cambio de motherboard huerfanaria la
maquina y perderia su historial.

## Enrolamiento

Nada de token compartido dentro del instalador: uno filtrado registraria maquinas
para siempre. El administrador genera `ENROLL-XXXX-XXXX`, valido 30 minutos y un
solo uso por defecto. Se guarda **solo hasheado** y se muestra en claro una vez.

El consumo es un `UPDATE` condicional unico:

```sql
UPDATE enrollment_codes
SET used_count = used_count + 1, ...
WHERE code_hash = ? AND expires_at > ? AND used_count < max_uses
```

Una sola sentencia cubre expiracion, agotamiento y la carrera de dos agentes
usando el mismo codigo en paralelo.

**Recovery code**: mismo mecanismo con `target_machine_id`. Repone token y pines
de una maquina existente conservando identidad, historial de IP y auditoria.

## TLS

El servidor autogenera su certificado en el primer arranque; no hace falta montar
una PKI para una LAN cerrada.

Los agentes **fijan la clave publica (SPKI)**, no el thumbprint del certificado.
Renovar un certificado vencido reutilizando el mismo par de claves no mueve el
pin: la renovacion deja de ser un evento capaz de desconectar la planta entera.

El pin es un **conjunto**. Para un cambio real de clave:

```
1. ConfigUpdate -> los agentes aceptan {A, B}
2. esperar a que el 100% de las maquinas ONLINE reporten B en su heartbeat
3. Kestrel cambia al certificado B
4. ConfigUpdate -> se retira A
```

El paso 2 es la puerta: si una sola maquina no confirmo B, no se avanza.

**Break-glass**: para una PC que estuvo apagada durante la rotacion, el
administrador emite un recovery code explicitamente. Nunca una transicion
automatica por tiempo — una PC apagada diez dias por mantenimiento no debe
cambiar sola su estado de identidad.

**TOFU**: si el instalador no trae el pin, el agente confia en el primer
certificado que ve y lo fija. La ventana de exposicion es la del codigo de
enrolamiento, que dura minutos.

## Secretos

| Dato | Como se guarda |
|---|---|
| Token de maquina | SHA256 en la BD; DPAPI en el agente |
| Codigo de enrolamiento | SHA256, nunca en claro |
| Password de usuario | PBKDF2-HMAC-SHA256, 600.000 iteraciones (los hashes de antes guardan las suyas y suben al cambiarse), salt por usuario |
| Clave JWT y `server.pfx` | `C:\ProgramData\ILSANSYSTEM\DeviceHubServer`, en claro, con ACL **solo SYSTEM y Administradores** puesta por el servidor en cada arranque |
| Cadena de conexion | `DEVICEHUB_DB_CONNECTION` en el entorno **del servicio** (`HKLM\...\Services\DeviceHubServer\Environment`), no de maquina |

No hay usuario sembrado con password fija: el servidor crea `admin` con una
password aleatoria y la escribe una sola vez en el log. Un default hardcodeado es
exactamente lo que nadie cambia despues.

Las comparaciones de token y password son en tiempo fijo. El login devuelve el
mismo mensaje para usuario inexistente y password incorrecta.

## Sesiones del dashboard

El JWT dura 12 h, pero no se fia de lo que lleva dentro: cada token tiene un
sello (resumen de rol, estado activo y hash de la contrasena) que se compara en
cada llamada con la fila actual del usuario. Degradarlo, desactivarlo o
cambiarle la contrasena invalida sus tokens en segundos. Limite: los streams
largos (`WatchMachines`, el visor) se autorizan al abrirse y siguen hasta que se
cierran.

El login se limita por **IP** (sin el puerto efimero): 5 intentos por cuenta y
20 sumando todas las cuentas, en 5 minutos. No hay limite por cuenta a secas,
porque dejaria a cualquiera de la red bloquear al admin.

## Gestor de archivos y terminal

El gestor de archivos deja los directorios del sistema en **solo lectura**,
resolviendo junctions y symlinks en cada segmento de la ruta, sin borrado
recursivo y con tope de 4 MB por transferencia.

Eso **no es una barrera frente a quien tenga el terminal**: `RunShell`
(Engineer o superior) ejecuta PowerShell como SYSTEM y puede hacer todo lo que
el gestor de archivos impide. En la practica, un Engineer es administrador de
todas las PCs. Las protecciones del gestor evitan errores, no abusos.

## Auto-actualizacion

Se descarga, verifica y extrae en `<DataDirectory>\updates` (solo SYSTEM y
Administradores), nunca en `%TEMP%`. El SHA256 viaja con el paquete y solo
protege contra corrupcion.

**Con `UpdatePublisherThumbprint` vacio, que es el valor por defecto, no se
verifica firma**: quien pueda escribir en el origen de actualizaciones (la
carpeta `UpdatesPath` del servidor, o el recurso SMB de respaldo) ejecuta codigo
como SYSTEM en toda la planta. El agente lo avisa en el log en cada comprobacion.

## Limites conocidos

- La CLI del servidor (`DeviceHub.Server.exe --command ...`) se audita como
  `cli` sin rol: quien tenga acceso a la maquina del servidor se salta la
  escalera de roles (no las sesiones: rechaza `RunShell`).
- La deteccion de clones por fingerprint se degrada a `LOW` cuando 3 o mas
  maquinas lo comparten, que es lo normal en lotes de placas industriales. Ahi
  solo queda el detector de streams simultaneos: un clon que nunca coincide
  encendido con el original no se detecta.

## Pendiente (Fase 13)

- mTLS con certificado por agente
- matriz completa de roles Viewer / Technician / Engineer / Administrator
- rate limiting por maquina
- rotacion del certificado disparada desde el dashboard
