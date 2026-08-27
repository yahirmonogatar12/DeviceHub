namespace DeviceHub.RemoteHost.Capture;

/// <summary>
/// Este capturador nacio en un escritorio y el de entrada ya es otro.
///
/// No es un error recuperable AQUI a proposito. Un capturador atado a Default
/// no puede pasarse a Winlogon por si mismo -- SetThreadDesktop no mueve un
/// hilo que ya tenga objetos USER, y cualquier hilo que haya capturado un frame
/// los tiene. Intentarlo era lo que fallaba en silencio y dejaba mandando
/// cientos de frames validos del escritorio anterior.
///
/// Quien la atrapa es la bomba de esa pantalla, que muere; el vigilante la ve
/// morir y rehace la cadena en un hilo virgen, atandolo ANTES de construir
/// nada. Ese orden es el unico que Windows permite.
/// </summary>
public sealed class EscritorioCambiadoException(string desde, string hacia)
    : Exception($"La captura nacio en el escritorio {desde} y la entrada ya esta en {hacia}; " +
                "hay que rehacerla en un hilo nuevo.")
{
    public string Desde { get; } = desde;
    public string Hacia { get; } = hacia;
}
