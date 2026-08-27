# Capturar y controlar el escritorio seguro (Winlogon)

Fase 19. Estado: **funciona** — se ve el login, se puede pulsar en el y la
entrada llega. Verificado en INPUT-M3 con el agente 1.143.0.

Este documento existe porque durante meses el repositorio sostuvo una
afirmacion falsa, y la sostuvo *con evidencia*. Lo que sigue es lo que hay que
saber para no volver a deducirla.

## La afirmacion falsa

> "DXGI no puede capturar el escritorio Winlogon, asi que ahi se usa GDI."

Estaba escrita en el codigo, con un comentario que citaba una observacion real:
en Winlogon la duplicacion no daba error, se quedaba entregando el ultimo frame
del escritorio anterior.

La observacion era cierta. La conclusion, no. Son dos frases distintas:

```
"DXGI no puede capturar Winlogon"                     <- nunca se probo
"DXGI no puede llevarse a Winlogon una duplicacion    <- esto es lo cierto
 creada en Default"
```

Toda la evidencia se habia recogido con el hilo de captura atado a **Default**.
El bug se estaba describiendo a si mismo.

## El orden, que no es negociable

`SetThreadDesktop` **solo mueve un hilo que no tenga objetos USER**, y cualquier
hilo que haya tocado D3D11 o Media Foundation los tiene. Asi que no se puede
mover una cadena de captura a otro escritorio: hay que construirla ya dentro.

```
hilo NUEVO (virgen)
   -> SetThreadDesktop(Winlogon)
   -> crear D3D11
   -> DuplicateOutput
   -> capturar
```

Y al revertir, lo mismo en sentido contrario. Un capturador nace atado a un
escritorio y se muere con el; cambiar de escritorio es matar la generacion y
crear otra.

Ojo con un detalle que costo una tarde: **un hilo nuevo no hereda el escritorio
del que lo creo**, nace en el del proceso. Atar el hilo que CONSTRUYE la captura
no basta si quien la USA es otro.

## Los cinco fallos que lo tapaban

Todos daban la misma cara -- imagen congelada, cero errores, contadores
subiendo -- que es la razon de que costaran tanto.

| Fallo | Sintoma | Por que era invisible |
|---|---|---|
| `OpenInputDesktop` con `GENERIC_ALL` | Winlogon lo deniega en bloque | se caia al respaldo de solo lectura sin decirlo |
| Atarse con permiso de lectura y seguir inyectando | `entrada 2748/1` con la PC sin responder | `SendInput` devuelve exito igual |
| El hilo de bombeo nacia en Default | 118 frames validos del escritorio equivocado | nada falla; la imagen es real, solo que de otro escritorio |
| Leer el DIB sin `GdiFlush` | los mismos pixeles frame tras frame | `BitBlt` devuelve TRUE; se lee el lote sin vaciar |
| Forzar GDI en cuanto el escritorio no era Default | 10 FPS y sin interfaz de LogonUI | parecia una decision deliberada, y lo era: por el motivo equivocado |

## La mascara de permisos

Sobre un escritorio el permiso se concede o se deniega **en bloque**, asi que
cada derecho que sobra es una forma nueva de que te digan que no. `GENERIC_ALL`
son nueve derechos y Winlogon no los da.

Se prueba en escalera, de estrecha a ancha:

1. `GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE` — la que Winlogon concede
2. `GENERIC_ALL` — la unica con la que se ha visto controlar el escritorio normal

No una sustituyendo a la otra: afinar esta mascara ya rompio dos veces el
control del escritorio NORMAL, y `GENERIC_EXECUTE` incluye
`DESKTOP_SWITCHDESKTOP`, que fue una de esas dos veces.

## Lo que el overlay tiene que decir

Cuatro cosas distintas que durante meses se enseñaron como una:

```
escritorio entrada=Winlogon hilo=Winlogon atado=Winlogon via DXGI  teclado@Winlogon
```

- **entrada** — que escritorio recibe la entrada de Windows ahora
- **hilo** — donde esta el hilo que captura
- **atado** — en que escritorio NACIO esta generacion
- **via** — DXGI o GDI
- **teclado** — donde esta el hilo que inyecta, y con que permiso

Cualquier discrepancia entre los tres primeros es el bug. Con un solo campo
llamado "escritorio", ninguna se veia.

## Requisitos

- `DeviceHub:SecureDesktop` en `true` (por defecto). Sin el, RemoteHost corre
  como el usuario y no puede abrir Winlogon.
- El agente es `LocalSystem`, asi que el host hereda SYSTEM.
- Ctrl+Alt+Supr sigue necesitando `SendSAS` y la directiva
  `SoftwareSASGeneration`; no se genera con `SendInput` nunca.

## Lo que aun no esta

- Escribir la contrasena y entrar esta verificado a mano una vez, no en cada
  version. No hay forma de probar esto en CI: exige una PC real bloqueada.
- El aviso de bitrate rechazado en la pantalla de bloqueo sigue apareciendo. No
  afecta al control; queda anotado.
