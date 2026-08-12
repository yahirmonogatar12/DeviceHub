namespace DeviceHub.Remote.Contracts;

/// <summary>
/// De donde saca un extremo su ticket de arranque.
///
/// NUNCA de la linea de comandos. Los argumentos de un proceso los lee cualquier
/// usuario de esa maquina, y el ticket es la credencial que da acceso a ver y
/// controlar la pantalla. Un atajo aqui no se revierte despues: se queda.
///
/// Por stdin, y preferiblemente redirigido desde el proceso que lanza:
///
///     launcher
///        └─ tuberia de stdin -> RemoteViewer
///
/// La entrada tecleada a mano existe solo para diagnostico y se lee SIN ECO, para
/// que no quede en pantalla ni en el historial de la consola.
///
/// En la Fase 7 el host deja de usar esto: el agente le pasa la sesion y el
/// ticket por un named pipe con ACL restringida al SID del usuario.
/// </summary>
public static class BootstrapTicket
{
    /// <summary>
    /// Devuelve el ticket, o null si no llego ninguno. La cadena se devuelve al
    /// llamante y no se guarda en ningun sitio.
    /// </summary>
    public static string? Read()
    {
        // Redirigido: la primera linea es el ticket y ya esta. Es el camino
        // bueno, y el unico que se usa fuera de diagnostico.
        if (Console.IsInputRedirected)
            return Vacio(Console.ReadLine());

        Console.Error.Write("Ticket: ");

        var escrito = SinEco();

        Console.Error.WriteLine();
        return Vacio(escrito);
    }

    private static string? Vacio(string? valor)
        => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>
    /// Lee sin mostrar lo tecleado. Es lo mismo que hace cualquier prompt de
    /// contrasena, y por el mismo motivo: lo que se ve en pantalla acaba en una
    /// foto, en una grabacion de sesion o en el hombro de al lado.
    /// </summary>
    private static string SinEco()
    {
        var texto = new System.Text.StringBuilder();

        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);

            if (tecla.Key == ConsoleKey.Enter)
                return texto.ToString();

            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (texto.Length > 0)
                    texto.Length--;

                continue;
            }

            if (!char.IsControl(tecla.KeyChar))
                texto.Append(tecla.KeyChar);
        }
    }
}
