namespace DeviceHub.Server.Remote;

/// <summary>
/// Los motores disponibles y cual manda cuando nadie elige. Fase 8.
///
/// Existe para que el dashboard pueda OFRECER una eleccion sin tener que saber
/// que significa cada opcion: manda un nombre y aqui se traduce a un proveedor.
/// La frontera que importa sigue en pie -- como se lanza cada motor solo lo sabe
/// el proveedor -- y lo unico que cruza es una cadena.
/// </summary>
public sealed class RemoteProviderCatalog
{
    private readonly Dictionary<string, IRemoteProvider> _motores;

    public RemoteProviderCatalog(IEnumerable<IRemoteProvider> motores, string porDefecto)
    {
        _motores = motores.ToDictionary(m => m.Provider, StringComparer.OrdinalIgnoreCase);

        PorDefecto = _motores.TryGetValue(porDefecto.Trim(), out var elegido)
            ? elegido

            // Un valor desconocido NO se ignora en silencio. Descubrir por que el
            // boton no hace lo que crees, con la planta esperando, es mucho peor
            // que un servicio que no arranca y dice el motivo.
            : throw new InvalidOperationException(
                $"DeviceHub:RemoteProvider = '{porDefecto}' no existe. " +
                $"Los valores son: {string.Join(", ", _motores.Keys)}.");
    }

    public IRemoteProvider PorDefecto { get; }

    /// <summary>Nombre vacio = el configurado. Un nombre que no existe se rechaza
    /// en vez de caer al por defecto: si alguien pide un motor concreto y recibe
    /// otro sin avisar, el fallo aparece mucho despues y en otro sitio.</summary>
    public IRemoteProvider Resolver(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            return PorDefecto;

        return _motores.TryGetValue(nombre.Trim(), out var motor)
            ? motor
            : throw new ArgumentException($"No hay ningun motor remoto llamado '{nombre}'.", nameof(nombre));
    }
}
