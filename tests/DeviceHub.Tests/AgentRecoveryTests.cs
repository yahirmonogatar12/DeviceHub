using DeviceHub.Agent.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeviceHub.Tests;

/// <summary>
/// Recuperacion de un token que el servidor ya no reconoce.
///
/// Pasa cuando un administrador emite identidad nueva sobre una maquina que esta
/// desconectada: la fila se queda con token_hash NULL, el agente sigue
/// presentando el token viejo y el servidor contesta Unauthenticated para
/// siempre. Le paso a INPUTM1 y estuvo cinco dias caida.
/// </summary>
public class AgentRecoveryTests
{
    /// <summary>
    /// La parte que de verdad importa del arreglo: al descartar el token, el
    /// machineId TIENE que sobrevivir.
    ///
    /// El arreglo manual que se venia haciendo era borrar machine.json entero, y
    /// eso genera un GUID nuevo: el recovery code -- que apunta a la maquina
    /// vieja -- sale rechazado, y si se usa uno generico la PC vuelve como
    /// maquina distinta, sin su historial ni su ubicacion.
    /// </summary>
    [Fact]
    public void Discarding_the_token_keeps_the_machine_id()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"devicehub-{Guid.NewGuid():n}");
        Directory.CreateDirectory(carpeta);

        try
        {
            var almacen = new MachineIdentity(carpeta, NullLogger<MachineIdentity>.Instance);

            var identidad = almacen.Load();
            identidad.MachineCode = "INPUTM1";
            identidad.PinnedKeys = ["pin-del-servidor"];
            identidad.ProtectedToken = MachineIdentity.Protect("token-que-el-servidor-invalido");
            almacen.Save(identidad);

            var original = identidad.MachineId;
            Assert.NotEmpty(original);

            // Exactamente lo que hace Worker.DescartarToken.
            identidad.ProtectedToken = null;
            almacen.Save(identidad);

            var vuelta = new MachineIdentity(carpeta, NullLogger<MachineIdentity>.Instance).Load();

            Assert.Equal(original, vuelta.MachineId);
            Assert.Null(vuelta.ProtectedToken);

            // Los pines tambien se quedan: sin ellos la reconexion seria un TOFU
            // contra un servidor que ya estaba fijado.
            Assert.Equal(["pin-del-servidor"], vuelta.PinnedKeys);
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    /// <summary>
    /// Un token cifrado por otra instalacion de Windows no se puede descifrar
    /// -- pasa al restaurar una imagen conservando ProgramData -- y Unprotect
    /// devuelve null en vez de reventar. Es el otro disparador del descarte.
    /// </summary>
    [Fact]
    public void An_unreadable_token_reads_as_null()
    {
        Assert.Null(MachineIdentity.Unprotect("esto-no-es-DPAPI"));
        Assert.Null(MachineIdentity.Unprotect(null));
        Assert.Null(MachineIdentity.Unprotect("   "));
    }
}
