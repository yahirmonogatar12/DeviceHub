# Checkpoint de la Fase 5 — vídeo real por el relay

Probar la topología para la que se está construyendo esto, que no es localhost:

```
PC controlada .145              192.168.1.10:5443              PC técnico .211
DeviceHub.RemoteHost   ───────►  RemoteRelayService  ───────►  DeviceHub.RemoteViewer
                       saliente                      saliente
```

Lo que localhost **no** puede reproducir, y es justo el motivo de esta prueba:
switches reales, VLAN y rutas reales, TLS entre máquinas distintas, latencia real,
colas reales y NICs distintas.

---

## 1. Desplegar el servidor

```powershell
.\deploy\publish.ps1
# copiar artifacts\server\ a 192.168.1.10 y reiniciar el servicio
```

El relay va en el **mismo puerto y con el mismo certificado** que el resto: no hay
que abrir nada nuevo en el firewall. Lo que cambia es que RemoteHost y RemoteViewer
abren su **propia** conexión gRPC, así que un keyframe atascado no retrasa el
heartbeat de ningún agente.

Comprobar en el log del servidor:

```
DeviceHub Server escuchando en https://0.0.0.0:5443 (HTTP/2)
```

## 2. Arrancar los dos extremos

Con el **mismo `--session`** en los dos. Elegir un identificador reconocible: es lo
que permite cruzar los tres informes después.

En **.145** (la PC controlada):

```powershell
DeviceHub.RemoteHost.exe --relay-test `
  --server https://192.168.1.10:5443 `
  --session planta-01 `
  --allow-untrusted `
  --seconds 600 --fps 60
```

En **.211** (la PC del técnico):

```powershell
DeviceHub.RemoteViewer.exe --relay-test `
  --server https://192.168.1.10:5443 `
  --session planta-01 `
  --allow-untrusted
```

> `--allow-untrusted` **solo para esta prueba**. El certificado del servidor es
> autofirmado y estos dos procesos todavía no saben fijarlo; la Fase 17 lo
> sustituye por el mismo pin de clave pública que ya usa el agente. Los dos
> avisan por stderr cuando se usa.

Mover ventanas, abrir el MES, hacer scroll: un escritorio quieto no prueba nada
sobre el bitrate ni sobre las colas.

## 3. Qué mirar, y cómo reconciliarlo

Los tres imprimen con el mismo `session_id`. El servidor saca una línea cada
**10 s** por sesión viva; el host, cada 2 s; el viewer, cada 500 ms.

La cadena tiene que bajar de forma monótona, y **cada escalón que baja tiene un
contador que lo explica**:

```
host enviados
   ≥ relay recibidos          la diferencia son chunks perdidos en la red
   ≥ relay reenviados         la diferencia = tirados + esperandoIDR + configVieja
   ≥ viewer reconstruidos     la diferencia son chunks perdidos hacia el viewer
   ≥ viewer decodificados
```

**Leerlos del mismo instante.** Cada proceso cuenta desde su propio arranque y con
su propio reloj, así que dos lecturas separadas por unos segundos dan diferencias
que no significan nada. Ésa es la razón de que el servidor imprima el `session_id`
en cada línea.

| Fuente | Línea | Campos |
|---|---|---|
| Host `.145` | cada 2 s | `capturados`, `codificados`, `frames enviados`, `chunks`, `Mbps`, `keyframes`, `config` |
| Servidor | cada 10 s | `recibidos`, `reenviados`, `tirados`, `esperandoIDR`, `configVieja`, `cola=actual/máx`, `control`, `bytes` |
| Viewer `.211` | cada 500 ms | `chunks`, `frames`, `decodificados`, `pintados`, `render FPS`, `decode p50/p95`, `incompletos`, `invalidos`, `tardios`, `IDR`, `RTT`, `RAM` |

### Criterios

| Qué | Se espera |
|---|---|
| Imagen | El escritorio real de `.145`, no una imagen congelada ni con manchas |
| FPS de render | Sostenido, sin caídas a cero |
| RTT | En LAN son unos pocos ms, pero **con picos de cientos de ms bajo carga**, y eso no es la red: en el host de prueba el Pong sale por el mismo escritor que el vídeo y espera detrás de él. El relay sí adelanta el control; este cliente de diagnóstico no. Léelo como "latencia de ida y vuelta incluyendo cola del emisor", no como latencia de red |
| Bitrate | Gobernado por el objetivo bajo carga; con el escritorio quieto puede ser mucho menor, y eso es correcto |
| `tirados` | Idealmente 0. Si sube, `esperandoIDR` tiene que subir detrás y la imagen recuperarse en el siguiente keyframe, **no** llenarse de manchas |
| `cola` | No crece sin parar. El máximo es 4 por diseño |
| `incompletos` / `invalidos` | 0 |
| RAM del viewer | Meseta, no pendiente |
| Reconexión | Cerrar el viewer y volver a abrirlo con el mismo `--session`: el host **no** se entera y el viewer vuelve a ver en cuanto llega el siguiente IDR |

Mínimo **5–10 minutos** de escritorio real.

## 4. Lo que este checkpoint no cubre

- **Tickets**: `Hello.ticket` viaja y se le comprueba el tamaño, pero no se valida.
  Es la Fase 6, y con ella llega el *connection lease* que hace que la reconexión
  del viewer no gaste un ticket nuevo.
- **Lanzar el host desde el agente**: aquí se arranca a mano. La Fase 7 lo lanza en
  la sesión interactiva y le pasa la sesión por un named pipe.
- **Entrada**: no hay ratón ni teclado todavía. Fases 9 a 11.
- **Validación del certificado**: Fase 17.
