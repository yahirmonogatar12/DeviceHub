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
/// significa que no sonaba nada. Hay que poner algo a sonar mientras corre, y
/// por eso la salida separa paquetes de silencio de paquetes con sonido.
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
            Console.WriteLine($"Esperado:     {esperados:N0} bytes  ({bytes * 100.0 / Math.Max(esperados, 1):0.0} %)");
            Console.WriteLine($"Vueltas:      {vueltas:N0}  ({vacias:N0} sin nada)");
            Console.WriteLine($"Silencios:    {captura.Silencios:N0} paquetes");
            Console.WriteLine($"Huecos:       {captura.Discontinuidades:N0}");

            if (picos.Count > 0)
            {
                picos.Sort();
                Console.WriteLine($"Pico p50:     {picos[picos.Count / 2]:0.000}");
                Console.WriteLine($"Pico maximo:  {picos[^1]:0.000}");
            }

            if (guardar is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"PCM crudo en {guardar}");
                Console.WriteLine(
                    $"  ffplay -f {(captura.Formato.EsFlotante ? "f32le" : "s16le")} " +
                    $"-ar {captura.Formato.Hz} -ch_layout {(captura.Formato.Canales == 2 ? "stereo" : "mono")} \"{guardar}\"");
            }

            Console.WriteLine();

            // EL PORCENTAJE ES EL NUMERO. Por debajo del 99 se estan perdiendo
            // fotogramas, y en sonido eso no se ve como una imagen mas fea: se
            // oye como chasquidos.
            if (bytes == 0)
            {
                Console.WriteLine("NO SONO NADA. La captura abrio bien; simplemente no habia sonido.");
                return 3;
            }

            var porcentaje = bytes * 100.0 / Math.Max(esperados, 1);

            Console.WriteLine(porcentaje >= 99
                ? "Captura COMPLETA. Se puede construir encima."
                : $"Faltan fotogramas ({100 - porcentaje:0.0} %). Eso se oye como chasquidos.");

            return porcentaje >= 99 ? 0 : 4;
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
