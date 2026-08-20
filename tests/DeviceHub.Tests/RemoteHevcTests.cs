using DeviceHub.Remote.Contracts;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Los conjuntos de parametros de H.265.
///
/// Se prueba aparte de H.264 porque el fallo aqui es SILENCIOSO: leer un flujo
/// HEVC con las reglas de H.264 no lanza nada, devuelve un array vacio, y el
/// visor se queda esperando para siempre una configuracion que si venia dentro
/// del IDR. Eso se ve como una pantalla en negro sin un solo error.
///
/// La secuencia imita la que produce de verdad el NVIDIA HEVC Encoder MFT, que
/// es AUD, VPS, SPS, PPS, IDR.
/// </summary>
public class RemoteHevcTests
{
    /// <summary>Cabecera de NAL de HEVC: DOS bytes, y el tipo en los bits 1-6
    /// del primero. En H.264 era un byte y los 5 de abajo.</summary>
    private static byte[] Nal(int tipo, params byte[] carga)
        => [0, 0, 0, 1, (byte)(tipo << 1), 1, .. carga];

    private static byte[] Unidad() =>
    [
        .. Nal(35, 0x10),        // AUD, delimitador
        .. Nal(32, 0xAA, 0xBB),  // VPS
        .. Nal(33, 0xCC, 0xDD),  // SPS
        .. Nal(34, 0xEE),        // PPS
        .. Nal(19, 0x01, 0x02)   // IDR_W_RADL
    ];

    /// <summary>Los tipos de NAL que hay dentro de un trozo Annex-B, en orden.
    /// Se comprueba ESTO y no los bytes exactos: en el empalme entre dos NAL sale
    /// un 00 de mas -- el mismo que lleva ahi desde H.264 -- y es inofensivo,
    /// porque Annex-B admite ceros de relleno delante del prefijo. Afirmar sobre
    /// los bytes seria atar la prueba a esa rareza en vez de a lo que importa.
    /// </summary>
    private static List<int> Tipos(byte[] flujo)
    {
        var tipos = new List<int>();

        for (var i = 0; i + 3 < flujo.Length; i++)
        {
            if (flujo[i] == 0 && flujo[i + 1] == 0 && flujo[i + 2] == 1)
            {
                tipos.Add((flujo[i + 3] >> 1) & 0x3F);
                i += 3;
            }
        }

        return tipos;
    }

    [Fact]
    public void It_takes_vps_sps_and_pps()
    {
        var parametros = H264AnnexB.ParameterSets(Unidad(), hevc: true);

        // Los tres y nada mas: ni el delimitador ni la imagen. Y en ese orden,
        // que es el que el descodificador espera.
        Assert.Equal([32, 33, 34], Tipos(parametros));

        // Y las cargas viajan enteras: un recorte de un byte aqui daria un SPS
        // que el descodificador rechaza mucho despues y sin decir por que.
        Assert.Contains<byte>(0xAA, parametros);
        Assert.Contains<byte>(0xDD, parametros);
        Assert.Contains<byte>(0xEE, parametros);
    }

    [Fact]
    public void Reading_hevc_with_h264_rules_finds_nothing()
    {
        // ESTE es el fallo que la prueba existe para impedir. El tipo 32
        // enmascarado con 0x1F da 0, que no es ni SPS ni PPS, asi que el lector
        // de H.264 se va de vacio sin quejarse.
        Assert.Empty(H264AnnexB.ParameterSets(Unidad()));
    }

    [Fact]
    public void An_h264_unit_still_works()
    {
        // Y al reves: el flujo de siempre no se rompe. El tipo 7 leido como
        // HEVC da 3, que tampoco coincide con nada.
        byte[] h264 = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, 0x88];

        Assert.NotEmpty(H264AnnexB.ParameterSets(h264));
        Assert.Empty(H264AnnexB.ParameterSets(h264, hevc: true));
    }
}
