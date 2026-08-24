using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// BGRA a NV12 por CPU, para las maquinas que no tienen tuberia de video.
///
/// La ruta normal convierte con ID3D11VideoProcessor y le entrega la textura al
/// codificador sin que baje a RAM. Un servidor sin tarjeta grafica no puede
/// hacer ninguna de las dos cosas: su dispositivo D3D11 -- el del adaptador de
/// gestion, o WARP -- no expone ID3D11VideoDevice, y sin el no hay procesador de
/// video ni gestor DXGI que atar al MFT.
///
/// Aqui se baja el frame a memoria con D3D11 a secas, que eso si lo sabe hacer
/// cualquier dispositivo, y se convierte con aritmetica entera. Es caro -- un
/// 1080p son 2 millones de pixeles por frame -- y es exactamente lo que hacen
/// RustDesk y AnyDesk en todas las maquinas. En una consola de servidor, que
/// pasa la vida quieta, sobra.
/// </summary>
public sealed class Nv12Cpu : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11Texture2D _copia;
    private readonly byte[] _nv12;

    /// <summary>
    /// El frame bajado, en un bufer que se reutiliza.
    ///
    /// Se copia desde el mapeo con Marshal.Copy en vez de leer el puntero
    /// directamente porque este repositorio no compila con /unsafe en ningun
    /// proyecto. Cuesta una copia de 8 MB por frame a 1080p, y es el precio de
    /// no abrir esa puerta por una funcion: esta ruta ya existe para maquinas
    /// donde lo caro es todo lo demas.
    /// </summary>
    private byte[] _bgra = [];
    private bool _hay;

    /// <summary>
    /// El ultimo NV12 convertido, o null si todavia no se convirtio ninguno.
    ///
    /// Existe para RE-ALIMENTAR el codificador cuando la pantalla no cambia. Un
    /// MFT por software retiene frames antes de soltar el primero, asi que en la
    /// consola de un servidor quieto se tragaba dos y no producia nada: no habia
    /// primer keyframe, y el visor se quedaba en "sin config" para siempre.
    /// </summary>
    public byte[]? Ultimo => _hay ? _nv12 : null;

    /// <summary>Lo que sale, que puede ser menor que lo que entra.</summary>
    public int Ancho { get; }
    public int Alto { get; }

    /// <summary>Lo que entra: el tamaño real de la pantalla.</summary>
    public int AnchoOrigen { get; }
    public int AltoOrigen { get; }

    public Nv12Cpu(ID3D11Device device, int anchoOrigen, int altoOrigen, int ancho, int alto)
    {
        if (ancho % 2 != 0 || alto % 2 != 0)
            throw new VideoEncoderUnavailableException(
                $"NV12 por CPU necesita medidas pares y llegaron {ancho}x{alto}. " +
                "Cada par de croma cubre un bloque de 2x2 pixeles.");

        _device = device;
        Ancho = ancho;
        Alto = alto;
        AnchoOrigen = anchoOrigen;
        AltoOrigen = altoOrigen;

        // NV12: un plano de luma completo y otro de croma a la mitad de alto,
        // con U y V intercalados.
        _nv12 = new byte[ancho * alto * 3 / 2];

        // Textura de ESCENIFICACION: la unica que la CPU puede mapear. Copiar a
        // ella es GPU->GPU y el mapeo es lo que cuesta.
        _copia = device.CreateTexture2D(new Texture2DDescription
        {
            // Del tamaño de la PANTALLA: es lo que se baja de la GPU. El
            // escalado ocurre despues, durante la conversion.
            Width = (uint)anchoOrigen,
            Height = (uint)altoOrigen,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    /// <summary>
    /// Baja la textura y devuelve el NV12. El bufer se REUTILIZA: quien lo reciba
    /// tiene que copiarlo antes de pedir el siguiente frame, y lo hace -- se
    /// entrega a un bufer de Media Foundation en la misma llamada.
    /// </summary>
    /// <summary>
    /// Lo que cuesta bajar la textura y convertirla, en milisegundos.
    ///
    /// SEPARADO DEL CODIFICADOR A PROPOSITO. La linea de medidas daba
    /// "codificar 22 ms" y ahi dentro habia dos cosas distintas: esto, que es
    /// codigo nuestro y se puede arreglar, y el MFT, que no. RustDesk hace esta
    /// misma conversion con ARGBToNV12 de libyuv -- SIMD escrito a mano -- y
    /// nosotros la hacemos pixel a pixel en un bucle escalar.
    ///
    /// Si este numero es la mayor parte de los 22 ms, vectorizar la conversion
    /// es la respuesta y no hay que tocar el codec. Si es una fraccion pequeña,
    /// el trabajo esta dentro del MFT y vectorizar no habria servido de nada.
    /// </summary>
    public double UltimoMs { get; private set; }

    public byte[] Convertir(ID3D11Texture2D origen)
    {
        var reloj = System.Diagnostics.Stopwatch.GetTimestamp();
        var contexto = _device.ImmediateContext;

        contexto.CopyResource(_copia, origen);

        var mapa = contexto.Map(_copia, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            var bytes = (int)mapa.RowPitch * AltoOrigen;

            if (_bgra.Length < bytes)
                _bgra = new byte[bytes];

            System.Runtime.InteropServices.Marshal.Copy(mapa.DataPointer, _bgra, 0, bytes);

            Convertir(_bgra, (int)mapa.RowPitch, _nv12, AnchoOrigen, AltoOrigen, Ancho, Alto);
        }
        finally
        {
            contexto.Unmap(_copia, 0);
        }

        _hay = true;

        UltimoMs = (System.Diagnostics.Stopwatch.GetTimestamp() - reloj) * 1000.0
                   / System.Diagnostics.Stopwatch.Frequency;

        return _nv12;
    }

    /// <summary>
    /// La conversion, sin GPU ni nada que mapear: se prueba con numeros.
    ///
    /// BT.601 en RANGO DE ESTUDIO (16-235), que es lo que un codificador espera
    /// de un NV12 sin mas metadatos. Con rango completo la imagen sale lavada al
    /// descodificar, y el error es dificil de ver mirando el codigo -- se ve
    /// mirando la pantalla y dudando de la camara.
    ///
    /// El croma se toma del pixel superior izquierdo de cada bloque de 2x2 y no
    /// del promedio: la diferencia es invisible en un escritorio, y promediar
    /// cuatro pixeles por cada par de bytes duplica el coste de la parte cara.
    /// </summary>
    public static void Convertir(
        ReadOnlySpan<byte> bgra, int strideBgra, Span<byte> nv12, int ancho, int alto)
        => Convertir(bgra, strideBgra, nv12, ancho, alto, ancho, alto);

    /// <summary>
    /// Igual, pero REDUCIENDO de paso.
    ///
    /// Existe para las PCs sin GPU. Ahi el escalado lo hacia el
    /// ID3D11VideoProcessor, que es justo la pieza que no esta, y sin el la
    /// unica salida era codificar la pantalla entera: en un Xeon sin GPU eso
    /// son cuatro veces mas pixeles de los que hacen falta para ver un
    /// escritorio remoto.
    ///
    /// Vecino mas proximo, en punto fijo 16.16. Interpolar seria mejor imagen y
    /// varias veces mas trabajo, y aqui el trabajo es exactamente lo que sobra.
    /// Sobre texto se nota; sobre texto a media resolucion se nota de todas
    /// formas, y lo que se estaba viendo antes era una imagen a medio segundo.
    /// </summary>
    public static void Convertir(
        ReadOnlySpan<byte> bgra, int strideBgra, Span<byte> nv12,
        int anchoOrigen, int altoOrigen, int ancho, int alto)
    {
        var croma = ancho * alto;

        // Una division por eje, no una por pixel.
        var pasoX = (int)(((long)anchoOrigen << 16) / ancho);
        var pasoY = (int)(((long)altoOrigen << 16) / alto);

        for (var y = 0; y < alto; y++)
        {
            var fila = (y * pasoY >> 16) * strideBgra;
            var destino = y * ancho;

            for (var x = 0; x < ancho; x++)
            {
                var p = fila + (x * pasoX >> 16) * 4;

                int b = bgra[p];
                int g = bgra[p + 1];
                int r = bgra[p + 2];

                nv12[destino + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                // Una vez por bloque de 2x2, en su esquina.
                if ((y & 1) != 0 || (x & 1) != 0)
                    continue;

                var uv = croma + y / 2 * ancho + x;

                nv12[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                nv12[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }

    public void Dispose() => _copia.Dispose();
}
