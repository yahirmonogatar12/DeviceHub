# Benchmark de motores de control remoto (Fase 18)

Medir, no opinar. La decision de sustituir RustDesk -- o de no sustituirlo -- se
toma con numeros de esta planta, no con impresiones ni con lo que diga un foro.

`IRemoteProvider` ya permite enchufar al ganador cambiando una linea de registro
de dependencias, asi que este benchmark **no bloquea nada**: se puede hacer
cuando haya tiempo, con DeviceHub ya en produccion.

## Regla previa

**Un motor solo se compara consigo mismo en el mismo escenario.** Cambiar de
resolucion, de red o de contenido en pantalla mueve los numeros mas que cambiar
de motor, y entonces la tabla no dice nada.

Escenario fijo para todos:

| Variable | Valor |
|---|---|
| Red | misma LAN, cableada, sin saltos WAN |
| Host | una PC de planta real, no una VM ni un portatil de desarrollo |
| Resolucion | 1920x1080, escalado 100% |
| Contenido | el MES en uso real: abrir pantallas, escribir, desplazar listas |
| Duracion | 10 min por motor para las medias; 1 h y 8 h para estabilidad |
| Hora | fuera de cambio de turno, cuando la red no esta saturada |

Cada motor se prueba **el mismo dia**. Comparar una medicion de hoy con otra de
hace tres semanas mide la red, no el motor.

## Lo que se mide solo

```powershell
# En la PC controlada
.\deploy\benchmark-remote.ps1 -Engine rustdesk -Role host -Minutes 10

# En la PC del tecnico, a la vez
.\deploy\benchmark-remote.ps1 -Engine rustdesk -Role client -Minutes 10
```

Se ejecuta en **los dos extremos**: un motor que va fino en el host y funde la
CPU del cliente no es ligero, es un motor que movio el coste de sitio.

La CPU se normaliza por nucleos. Sin eso, un proceso que satura un core en una
maquina de 16 marcaria 100% y pareceria mucho peor de lo que es.

Tambien cuenta los **reinicios del proceso**. En una prueba de 8 h ese es el dato
que mas importa y el que se perderia si solo se promediara CPU: un motor con
mejor latencia que se cae dos veces por turno es peor que uno mediocre estable.

## Lo que NO se puede automatizar de forma honesta

**Latencia de entrada y FPS.** Medirlos desde el propio software exige que el
motor los reporte, y cada uno lo hace distinto o no lo hace. Fingir que un script
los mide daria numeros inventados.

Metodo manual para la latencia, que es el que se usa de verdad y no necesita
hardware especial:

1. En el host, abrir un cronometro con milisegundos a pantalla completa.
2. Poner las dos pantallas juntas: la del host y la del cliente mostrando la
   sesion remota.
3. Fotografiar ambas con el movil, con velocidad de obturacion rapida.
4. Restar los dos tiempos que se ven en la foto.
5. Repetir **10 veces** y quedarse con la **mediana**, no con la media: una sola
   foto puede caer justo en un refresco y dar un valor absurdo.

Para FPS, si el motor lo muestra en su propia UI, anotarlo; si no, dejar la
casilla vacia en vez de estimar.

**Sensacion de uso.** Anotarla aparte y con nombre y apellido de quien la dio.
Es informacion valida -- un tecnico que usa esto ocho horas al dia nota cosas que
no salen en una media -- pero no es una medicion y no puede presentarse como tal.

## Tabla de resultados

| Motor | Latencia mediana | FPS | CPU host | CPU cliente | RAM host | Conexion | Estable 1 h | Estable 8 h | Reinicios 8 h | Multi-monitor |
|---|---|---|---|---|---|---|---|---|---|---|
| RustDesk | | | | | | | | | | |
| MeshCentral | | | | | | | | | | |
| Sunshine/Moonlight | | | | | | | | | | |

## Como se decide

Sustituir el motor cuesta: reinstalar en todas las PCs, reentrenar a quien lo
usa, y volver a validar. Ese coste solo se paga con una mejora clara, no con un
empate tecnico.

- **Se cambia** si el candidato mejora la latencia mediana en mas del 30% **y**
  aguanta las 8 h sin reinicios.
- **No se cambia** por diferencias de CPU o RAM que no se noten al usarlo: los
  numeros estan para decidir, no para ganar una discusion.
- **Se descarta** cualquier candidato que se reinicie durante la prueba de 8 h,
  por buenos que sean sus demas numeros.

Un motor propio (Rust/C++) solo se plantea si **ningun** candidato existente pasa
el filtro. Escribir y mantener un motor de escritorio remoto es un producto
entero, no un modulo: solo se justifica si no hay nada que sirva.
