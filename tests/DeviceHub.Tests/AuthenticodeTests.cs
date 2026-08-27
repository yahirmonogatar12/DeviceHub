using Xunit;
using DeviceHub.Agent.Updater;

namespace DeviceHub.Tests;

/// <summary>
/// La comprobacion de firma del actualizador.
///
/// Se prueba contra archivos que existen en cualquier Windows -- uno firmado
/// por Microsoft y uno que no lo esta -- porque lo que hay que demostrar no es
/// una cuenta, es que la funcion DISTINGA. La version anterior no distinguia:
/// sacaba el certificado del PE y comparaba su huella, asi que un binario
/// alterado despues de firmarlo pasaba igual.
/// </summary>
public class AuthenticodeTests
{
    private static readonly string Firmado =
        Path.Combine(Environment.SystemDirectory, "kernel32.dll");

    [Fact]
    public void Un_binario_firmado_por_Windows_pasa()
    {
        Assert.True(File.Exists(Firmado), $"falta {Firmado}");
        Assert.Equal(0, Authenticode.Verificar(Firmado));
    }

    [Fact]
    public void Un_archivo_sin_firma_no_pasa_y_dice_por_que()
    {
        var suelto = Path.Combine(Path.GetTempPath(), $"devicehub-sinfirma-{Guid.NewGuid():N}.exe");

        // Se copia un PE de verdad y se le quita la firma cambiandole un byte:
        // asi no se prueba contra "esto no es un ejecutable", que es otra cosa.
        File.Copy(Firmado, suelto, overwrite: true);

        try
        {
            using (var f = File.Open(suelto, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Seek(f.Length / 2, SeekOrigin.Begin);
                f.WriteByte(0x00);
            }

            var codigo = Authenticode.Verificar(suelto);

            Assert.NotEqual(0, codigo);
            Assert.NotEmpty(Authenticode.Motivo(codigo));
        }
        finally
        {
            File.Delete(suelto);
        }
    }

    [Fact]
    public void Un_archivo_que_no_existe_no_pasa()
    {
        Assert.NotEqual(0, Authenticode.Verificar(
            Path.Combine(Path.GetTempPath(), "no-existe-devicehub.exe")));
    }
}
