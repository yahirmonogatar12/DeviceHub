using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceHub.Dashboard;

/// <summary>
/// La sesion del dashboard, para no volver a teclear la contrasena cada vez que
/// se abre.
///
/// SE GUARDA EL TOKEN, NO LA CONTRASENA. Es la diferencia que importa: el token
/// dura doce horas y solo sirve contra este servidor, mientras que una
/// contrasena guardada vale para siempre y en cualquier sitio. Un turno cabe
/// entero en doce horas, que es justo lo que se pedia.
///
/// Cifrado con DPAPI en ambito de USUARIO, no de maquina: solo esa cuenta de
/// Windows en esa PC puede descifrarlo. Otro usuario de la misma PC -- o el
/// mismo archivo copiado a otra -- no saca nada.
///
/// Aun asi, esto convierte "entrar al dashboard" en "estar sentado en esa sesion
/// de Windows". Es una decision tomada a sabiendas: el dashboard es de
/// administradores, no lo usa ningun tecnico de planta, y las PCs desde las que
/// se administra ya se bloquean solas.
/// </summary>
public static class SesionGuardada
{
    private sealed record Guardado(string Token, string Usuario, string Rol, DateTimeOffset Vence);

    private static string Ruta => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ILSAN", "DeviceHub", "sesion.dat");

    /// <summary>Margen para no arrancar con un token que caduca en el camino.</summary>
    private static readonly TimeSpan Colchon = TimeSpan.FromMinutes(2);

    public static void Guardar(string token, string usuario, string rol, DateTimeOffset vence)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);

            var json = JsonSerializer.SerializeToUtf8Bytes(new Guardado(token, usuario, rol, vence));

            File.WriteAllBytes(Ruta, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            // Que no se pueda guardar la sesion no es motivo para no tenerla: se
            // sigue con la de esta ejecucion y la proxima vez se teclea.
        }
    }

    /// <summary>Devuelve la sesion guardada si sigue siendo utilizable, o null.</summary>
    public static (string Token, string Usuario, string Rol)? Leer()
    {
        try
        {
            if (!File.Exists(Ruta))
                return null;

            var json = ProtectedData.Unprotect(File.ReadAllBytes(Ruta), null, DataProtectionScope.CurrentUser);
            var guardado = JsonSerializer.Deserialize<Guardado>(Encoding.UTF8.GetString(json));

            if (guardado is null || guardado.Token.Length == 0)
                return null;

            // Caducado es lo mismo que no tenerlo, y ademas se borra: un token
            // vencido en disco no sirve para nada y es una credencial menos que
            // dejar por ahi.
            if (guardado.Vence - Colchon <= DateTimeOffset.UtcNow)
            {
                Borrar();
                return null;
            }

            return (guardado.Token, guardado.Usuario, guardado.Rol);
        }
        catch (Exception)
        {
            // Perfil DPAPI cambiado, archivo a medio escribir, formato viejo. En
            // todos los casos lo mismo: no hay sesion, y se pide la contrasena.
            Borrar();
            return null;
        }
    }

    public static void Borrar()
    {
        try
        {
            if (File.Exists(Ruta))
                File.Delete(Ruta);
        }
        catch (Exception)
        {
        }
    }
}
