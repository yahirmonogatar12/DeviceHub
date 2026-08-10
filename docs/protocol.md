# Protocolo

Definicion completa: [`src/DeviceHub.Contracts/Protos/devicehub.proto`](../src/DeviceHub.Contracts/Protos/devicehub.proto).

El package es `devicehub.v1`. **Versionado desde el dia 1** (regla 13): romper
compatibilidad exige un package nuevo, no editar el existente.

## Compatibilidad

Agregar variantes a un `oneof` o campos a un mensaje es compatible hacia atras en
proto3: un agente viejo ignora lo que no conoce. Por eso `AgentMessage` y
`ServerMessage` nacieron con una sola variante cada uno y las fases siguientes
solo suman.

Lo que **no** se puede hacer sin cambiar de version: renumerar campos, cambiar su
tipo, o reutilizar un numero liberado.

## AgentService

Autenticacion por token de maquina en el header `authorization: Bearer <token>`,
mas `x-machine-id`.

### `Register(RegisterRequest) -> RegisterReply`

Primer arranque o recovery. Consume un codigo de un solo uso.

```
enrollment_code   ENROLL-8K2F-A91X
machine_id        GUID generado localmente
hostname
fingerprint       { hash, confidence }
agent_version
```

Devuelve el token permanente, el `machine_code` autoritativo y los pines SPKI
aceptados.

Errores:

| Codigo | Causa |
|---|---|
| `InvalidArgument` | `machine_id` no es un GUID |
| `PermissionDenied` | codigo invalido, vencido o ya consumido |
| `FailedPrecondition` | conflicto de identidad, o recovery code de otra maquina |

### `Connect(stream AgentMessage) -> stream ServerMessage`

Stream persistente. El agente sube `Heartbeat` cada 30 s; el servidor baja
`ConfigUpdate` cuando cambia el `machine_code` o el conjunto de pines.

El servidor rechaza con `FailedPrecondition` si ya hay otro stream vivo para el
mismo `machine_id`. Ese es el detector de clonacion que no depende del hardware.

El agente reconecta con backoff exponencial (1 s -> 60 s, con jitter). Ante
`FailedPrecondition` espera 5 minutos: un conflicto de identidad lo resuelve un
administrador, no el reintento.

## AdminService

Autenticacion por JWT (`Login` es anonimo). El rol viaja como claim y se verifica
con `[Authorize(Roles = ...)]`.

| RPC | Rol minimo |
|---|---|
| `Login` | anonimo |
| `ListMachines`, `GetMachine`, `WatchMachines` | autenticado |
| `MoveMachine`, `CreateEnrollmentCode`, `ResolveIdentityConflict` | `administrator` |

`WatchMachines` empuja primero el estado completo y despues solo los cambios, para
que el dashboard no haga polling.

## Zonas horarias

Todos los `google.protobuf.Timestamp` son UTC por definicion. La conversion a
hora local ocurre unicamente en el WPF.
