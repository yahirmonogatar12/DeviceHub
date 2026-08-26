# Arquitectura

```
                    ILSAN DeviceHub
                           |
              +------------+------------+
              |                         |
        DeviceHub.Server            MySQL 8
      ASP.NET Core + gRPC          devicehub
              |
        gRPC / HTTP2 / TLS
              |
    +---------+---------+
    |                   |
DeviceHub.Agent    DeviceHub.Dashboard
 Windows Service       WPF + MVVM
```

Un solo transporte: **gRPC**. El agente abre un stream bidireccional persistente
y lo mantiene; el dashboard usa la misma tecnologia contra otra superficie.

## Vocabulario

**Machine** — una PC fisica. Identificada por un GUID permanente generado en el
primer arranque del agente y guardado en
`C:\ProgramData\ILSANSYSTEM\DeviceHub\machine.json`. No depende de la IP, del
hostname ni del hardware. Es lo unico inmutable: `machine_code`, sitio, area,
linea y estacion son editables y quedan en `machine_placement_history`.

**Agent** — servicio de Windows por PC. Corre como SYSTEM, sin sesion iniciada.
Genera la identidad, se enrola con un codigo de un solo uso, y a partir de ahi
mantiene el stream con el servidor.

**Server** — servicio de Windows unico. Es lo unico que toca MySQL. Aplica las
migraciones al arrancar, autentica agentes por token y usuarios por JWT.

**Heartbeat** — mensaje del agente cada 30 s por el stream: hostname, interfaces
de red, usuario con sesion, uptime, version y fingerprint de hardware. Solo
actualiza `last_seen` salvo que el conjunto de IPs cambie.

**Session** — (Fase 11) sesion de control remoto de un usuario sobre una maquina,
con inicio, fin y origen. Auditada.

**Command** — (Fase 7) instruccion del servidor al agente por el stream que ya
existe. Tipo cerrado por enum, persistido e idempotente por `commandId`.

**RemoteProvider** — (Fase 10) abstraccion del motor de control remoto.
`IRemoteProvider` con dos metodos; primera implementacion RustDesk. Nada fuera
del provider sabe que RustDesk existe.

## Por que estas decisiones

**El stream bidireccional desde el dia 1.** Es a la vez el canal de heartbeat y
el canal servidor -> agente. Cuando lleguen los comandos (Fase 7) no hace falta
transporte nuevo: solo una variante mas en el `oneof`.

**El estado no se almacena.** `ONLINE` / `UNREACHABLE` / `OFFLINE` se derivan de
`last_seen` en cada lectura. Una columna `status` exigiria un servicio de fondo
recorriendo la tabla cada segundo y podria quedar desincronizada. El dashboard
tambien lo recalcula: una PC que se apaga no emite un mensaje de despedida.

**Contracts genera cliente y servidor.** Un solo ensamblado con
`GrpcServices="Both"`, referenciado por los tres ejecutables.

**Sin proyectos Core/Windows separados en el agente.** Un `classlib` sin un
segundo consumidor es andamiaje. Se dividira si aparece un agente Linux.

## Software de terceros que se redistribuye

Solo hay uno, y esta seccion existe porque su licencia la exige.

### Amyuni usbmmidd_v2 — el driver de pantalla virtual

Copyright 2014-2021 **Amyuni Technologies Inc.** — https://www.amyuni.com

Driver de pantalla indirecta que crea un monitor virtual en la PC controlada,
para que el tecnico pueda trabajar sin taparle la pantalla al operador
(Fase 27). Se redistribuye tal cual dentro del paquete del agente, con su
`License.txt` intacto, y vive en `vendor\usbmmidd_v2\`.

Su licencia pide tres cosas y las tres se cumplen: no atribuirnos el software,
no distribuirlo alterado, y no quitar el aviso de licencia de la distribucion.

**Se eligio por la firma, no por gusto.** Un driver de pantalla indirecta tiene
que estar firmado para instalarse en un Windows 11 con arranque seguro. Las
alternativas eran comprar un certificado EV y pasar por la atestacion de
Microsoft -- semanas y dinero por año -- o activar `testsigning` en las cinco
PCs de planta, que baja la comprobacion de integridad de arranque de toda la
maquina. Este ya viene firmado por WHQL y es redistribuible.

**Lo que su licencia advierte, y conviene tener presente:** la version gratuita
puede mostrar una pagina de publicidad, y Amyuni dice expresamente que no
siempre esta bajo su control. En una PC de planta eso importa. Solo puede pasar
al INSTALAR el driver -- la primera vez que alguien pulsa "Anadir pantalla
virtual" en esa maquina, no en cada sesion ni al actualizar el agente. Si
apareciera, la version comercial de Amyuni la quita.
