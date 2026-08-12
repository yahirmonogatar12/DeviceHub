# Fase 7 — el agente arranca el host en la sesion del usuario

Lo que esta fase anade: el tecnico pulsa **Controlar (DeviceHub)** y la PC
controlada arranca sola. Hasta la Fase 6 habia que ejecutar
`--relay-test` a mano en esa PC.

```
dashboard  --IssueRemoteTickets-->  servidor
                                        |
                                        | RemoteHostControl.START por el stream
                                        | del agente (autenticado, con pin)
                                        v
                                    DeviceHub.Agent   (Session 0, SYSTEM)
                                        |
                                        | WTSGetActiveConsoleSessionId
                                        | WTSQueryUserToken
                                        | DuplicateTokenEx
                                        | CreateEnvironmentBlock
                                        | CreateProcessAsUser  lpDesktop=winsta0\default
                                        v
                                    DeviceHub.RemoteHost --pipe NOMBRE
                                        |
                                        | named pipe con ACL al SID del usuario:
                                        | sesion + ticket + servidor + pines
                                        v
                                    relay 192.168.1.10:5443
```

Por linea de comandos viaja **solo el nombre del pipe**. El ticket va por dentro,
y ese pipe solo lo puede abrir el usuario al que se lanzo el proceso.

## Que comprobar en la PC de planta

1. **Con usuario logueado.** Pulsar el boton en el dashboard. El visor tiene que
   mostrar el escritorio de esa PC sin que nadie toque nada alli.
2. **Sin nadie logueado.** El agente registra
   `Nadie logueado en la sesion N (WTSQueryUserToken: 1314)` y el visor se queda
   esperando. Es el comportamiento correcto: sin sesion interactiva no hay
   escritorio que capturar. La pantalla de bloqueo y el escritorio seguro estan
   fuera de alcance por decision propia (Fase 16).
3. **Cerrar el visor.** El relay cierra la sesion, el host lo ve y termina. En el
   administrador de tareas de la PC controlada **no puede quedar**
   `DeviceHub.RemoteHost.exe`.
4. **Matar el servicio del agente.** El pipe se rompe y el host tambien muere: un
   proceso capturando la pantalla sin nadie que lo gobierne es justo lo que no
   puede quedarse por ahi.
5. **Agente desconectado.** El dashboard avisa
   `... el agente no esta conectado y nadie va a arrancar el host`, en vez de
   abrir un visor en negro sin explicacion.

## Lo que este tramo aprendio

**Los buffers del named pipe no pueden ser 0.** La sobrecarga corta de
`NamedPipeServerStream` los deja a cero, y para Win32 eso significa *sin buffer*:
cada escritura se queda bloqueada hasta que el otro extremo lea. Con los dos
extremos en el mismo hilo se cuelga en la primera linea; con extremos distintos
funciona hasta que uno se atasca, y entonces cuelga al otro. Medido: con 0 se
bloquea, con 4096 fluye.

**El host ya no confia a ciegas en el certificado del relay.** El agente le pasa
sus pines SPKI por el pipe y el host valida con ellos, que es lo mismo que hace
el agente para su propio canal. `--allow-untrusted` queda como escotilla de
laboratorio (`DeviceHub:RemoteAllowUntrusted` en el appsettings del agente),
apagada por defecto.

## Lo que sigue sin probarse

La gracia de reconexion de la Fase 6 -- cerrar el visor y volver dentro de 30 s
sin gastar un ticket nuevo. Tiene 20 tests con reloj inyectado, pero hasta ahora
no habia video con el que ejercitarla en vivo. Con la Fase 7 ya lo hay.
