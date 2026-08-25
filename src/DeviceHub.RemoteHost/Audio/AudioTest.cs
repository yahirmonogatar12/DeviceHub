using System.Diagnostics;

namespace DeviceHub.RemoteHost.Audio;

/// <summary>
/// Modo --audio-test: captura el sonido de esta PC y saca los numeros que
/// deciden si la Fase 26 se puede construir encima.
///
/// PRIMERO ESTO Y DESPUES EL TRANSPORTE, que es lo que el plan pide y lo que
/// funciono en las fases 1 y 2: la captura y el codificador se prueban en
/// hardware real antes de escribir las capas de arriba. Al reves, cinco capas
/// quedan "terminadas" sobre una base que nadie midio.
///
/// Lo que hay que saber para no engañarse: con loopback, una PC en SILENCIO no
/// entrega paquetes. Cero bytes no significa que la captura este rota --
/// significa que no sonaba nada.
///
/// Y de ahi el error que tenia esta prueba: comparaba lo capturado contra
/// Hz x segundos, o sea daba por hecho que el sonido es continuo. En una PC de
/// planta que emite un pitido por cada pieza, eso decia "23 %, faltan
/// fotogramas, se oye como chasquidos" sobre una captura PERFECTA -- un hueco
/// en sesenta segundos.
///
/// Lo que decide son los HUECOS, que es Windows diciendo que tiro fotogramas
/// porque no los recogimos, y el PICO, que distingue capturar sonido de
/// capturar silencio.
/// </summary>
public static class AudioTest
{
    public static int Run(int segundos, string? guardar)
    {
        CapturaDeSonido captura;

        try
        {
            captura = new CapturaDeSonido();
        }
        catch (SonidoNoDisponibleException ex)
        {
            Console.Error.WriteLine($"No se puede capturar el sonido: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "En un servidor sin tarjeta de sonido esto es normal: Windows no expone");
            Console.Error.WriteLine(
                "un dispositivo de salida por defecto y no hay nada que capturar.");

            return 2;
        }

        using (captura)
        {
            Console.WriteLine($"Dispositivo:  {captura.Dispositivo}");
            Console.WriteLine($"Formato:      {captura.Formato}");
            Console.WriteLine($"Bytes/s:      {captura.Formato.Hz * captura.Formato.BytesPorFotograma:N0}");
            Console.WriteLine();
            Console.WriteLine($"Capturando {segundos} s. PON ALGO A SONAR en esta PC.");
            Console.WriteLine();

            var bufer = new byte[captura.Formato.Hz * captura.Formato.BytesPorFotograma];  // 1 s
            var reloj = Stopwatch.StartNew();
            var limite = TimeSpan.FromSeconds(segundos);

            long bytes = 0, vueltas = 0, vacias = 0;
            var picos = new List<double>();
            var archivo = guardar is null ? null : new BinaryWriter(File.Create(guardar));

            // EL CODIFICADOR, en la misma corrida. Medirlo aparte con un archivo
            // grabado antes daria otro numero: aqui compite por la CPU con la
            // captura, que es como va a vivir.
            var pcm16 = new byte[bufer.Length / 2];
            var mono = new byte[bufer.Length / 4];
            AacEncoder? aac = null;
            long comprimidos = 0, paquetes = 0;
            var tiemposAac = new List<double>();

            try
            {
                aac = new AacEncoder(captura.Formato.Hz, 1);
                Console.WriteLine(
                    $"AAC:          {aac.BitsPorSegundo / 1000} kbps mono, " +
                    $"config de {aac.Configuracion.Length} bytes");
            }
            catch (AacNoDisponibleException ex)
            {
                Console.WriteLine($"AAC:          NO disponible ({ex.Message})");
                Console.WriteLine();
                Console.WriteLine("Lo que ese codificador dice que acepta:");

                foreach (var linea in AacEncoder.Acepta())
                    Console.WriteLine(linea);
            }

            Console.WriteLine();


            try
            {
                while (reloj.Elapsed < limite)
                {
                    vueltas++;
                    var leidos = captura.Recoger(bufer);

                    if (leidos == 0)
                    {
                        vacias++;
                        Thread.Sleep(5);
                        continue;
                    }

                    bytes += leidos;
                    picos.Add(Pico(bufer.AsSpan(0, leidos), captura.Formato));
                    archivo?.Write(bufer, 0, leidos);

                    if (aac is null)
                        continue;

                    // flotante -> 16 bits -> mono -> AAC. Los dos primeros pasos
                    // son nuestros y el tercero de Windows; se cronometra el
                    // conjunto porque es lo que costara en la sesion.
                    var desde = Stopwatch.GetTimestamp();

                    var enteros = Pcm16.Convertir(bufer.AsSpan(0, leidos), pcm16);
                    var enMono = captura.Formato.Canales == 2
                        ? Pcm16.AMono(pcm16.AsSpan(0, enteros), mono)
                        : 0;

                    var fuente = enMono > 0 ? mono.AsSpan(0, enMono) : pcm16.AsSpan(0, enteros);

                    foreach (var paquete in aac.Codificar(fuente, (long)(reloj.Elapsed.TotalSeconds * 1_000_000)))
                    {
                        comprimidos += paquete.Length;
                        paquetes++;
                    }

                    tiemposAac.Add((Stopwatch.GetTimestamp() - desde) * 1000.0 / Stopwatch.Frequency);
                }
            }
            finally
            {
                archivo?.Dispose();
            }

            var s = reloj.Elapsed.TotalSeconds;
            var esperados = (long)(captura.Formato.Hz * captura.Formato.BytesPorFotograma * s);

            Console.WriteLine($"Duracion:     {s:0.0} s");
            Console.WriteLine($"Capturado:    {bytes:N0} bytes");

            // NO es "cuanto se perdio": es cuanto tiempo hubo sonido.
            //
            // WASAPI en loopback no entrega un flujo continuo -- cuando no suena
            // nada, no entrega nada. Comparar contra Hz x segundos da por hecho
            // que el sonido es continuo, y en una PC de planta que emite un
            // pitido por cada pieza eso es falso: salia "23 %" y "faltan
            // fotogramas" sobre una captura perfecta.
            Console.WriteLine($"Con sonido:   {bytes * 100.0 / Math.Max(esperados, 1):0.0} % del tiempo");
            Console.WriteLine($"Vueltas:      {vueltas:N0}  ({vacias:N0} sin nada)");
            Console.WriteLine($"Silencios:    {captura.Silencios:N0} paquetes");
            Console.WriteLine($"Huecos:       {captura.Discontinuidades:N0}   <- el que decide");

            if (picos.Count > 0)
            {
                picos.Sort();
                Console.WriteLine($"Pico p50:     {picos[picos.Count / 2]:0.000}");
                Console.WriteLine($"Pico maximo:  {picos[^1]:0.000}");
            }

            if (aac is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"AAC paquetes: {paquetes:N0}");
                Console.WriteLine($"AAC bytes:    {comprimidos:N0}  ({comprimidos * 8 / Math.Max(s, 0.001) / 1000:0} kbps de media)");
                Console.WriteLine($"Compresion:   {(bytes > 0 ? (double)bytes / Math.Max(comprimidos, 1) : 0):0.0}x contra el PCM crudo");

                if (tiemposAac.Count > 0)
                {
                    tiemposAac.Sort();
                    Console.WriteLine($"AAC p50:      {tiemposAac[tiemposAac.Count / 2]:0.00} ms por bloque");
                    Console.WriteLine($"AAC p95:      {tiemposAac[(int)(tiemposAac.Count * 0.95)]:0.00} ms");
                }

                if (aac.UltimoProblema is not null)
                    Console.WriteLine($"AAC problema: {aac.UltimoProblema}");
            }

            aac?.Dispose();

            if (guardar is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"PCM crudo en {guardar}");
                Console.WriteLine(
                    $"  ffplay -f {(captura.Formato.EsFlotante ? "f32le" : "s16le")} " +
                    $"-ar {captura.Formato.Hz} -ch_layout {(captura.Formato.Canales == 2 ? "stereo" : "mono")} \"{guardar}\"");
            }

            Console.WriteLine();

            // LOS HUECOS SON EL NUMERO, no el porcentaje.
            //
            // Un hueco es Windows diciendo que tiro fotogramas porque no los
            // recogimos a tiempo, y en sonido eso no se ve como una imagen mas
            // fea: se oye como un chasquido. Uno al arrancar es normal.
            //
            // Y el PICO es el otro: sin el, una PC muda pasaria la prueba con
            // sobresaliente, que es como se aprueba una captura de sonido que no
            // captura sonido.
            if (bytes == 0)
            {
                Console.WriteLine("NO SONO NADA. La captura abrio bien; simplemente no habia sonido.");
                return 3;
            }

            if (picos.Count == 0 || picos[^1] <= 0.0001)
            {
                Console.WriteLine("Solo silencio. La captura funciona, pero no demuestra nada:");
                Console.WriteLine("pon algo a sonar en esta PC y repite.");
                return 3;
            }

            // Uno por cada diez segundos ya es demasiado: a 48 kHz cada hueco se
            // come milisegundos de audio y se oye.
            var tolerancia = Math.Max(1, (int)(s / 10));

            Console.WriteLine(captura.Discontinuidades <= tolerancia
                ? $"Captura LIMPIA: {captura.Discontinuidades} hueco(s) en {s:0} s. Se puede construir encima."
                : $"{captura.Discontinuidades} huecos en {s:0} s. Eso se oye como chasquidos.");

            return captura.Discontinuidades <= tolerancia ? 0 : 4;
        }
    }

    /// <summary>
    /// El pico de amplitud del bloque, de 0 a 1. Es lo que distingue "capturo
    /// 48000 bytes de silencio" de "capturo 48000 bytes de sonido", y sin el la
    /// prueba pasa con la PC muda.
    /// </summary>
    private static double Pico(ReadOnlySpan<byte> datos, Wasapi.Formato formato)
    {
        double maximo = 0;

        if (formato.EsFlotante && formato.BitsPorMuestra == 32)
        {
            for (var i = 0; i + 4 <= datos.Length; i += 4)
                maximo = Math.Max(maximo, Math.Abs(BitConverter.ToSingle(datos[i..(i + 4)])));
        }
        else if (formato.BitsPorMuestra == 16)
        {
            for (var i = 0; i + 2 <= datos.Length; i += 2)
                maximo = Math.Max(maximo, Math.Abs(BitConverter.ToInt16(datos[i..(i + 2)])) / 32768.0);
        }

        return maximo;
    }
}
