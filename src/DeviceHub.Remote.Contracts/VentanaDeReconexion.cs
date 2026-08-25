namespace DeviceHub.Remote.Contracts;

/// <summary>
/// Cuanto se sigue insistiendo en volver a una sesion que se corto.
///
/// EXISTE PORQUE LOS DOS EXTREMOS SE EQUIVOCARON IGUAL. El host y el visor
/// contaban la ventana desde que ARRANCO el intento, no desde que se cayo:
///
///     corte = UtcNow;              // antes de conectar
///     codigo = await Sesion(...);  // dura horas
///     catch when (UtcNow - corte &lt; 1 min)   // <- falso desde el minuto uno
///
/// El efecto no se ve en una prueba corta y es demoledor en una larga: pasados
/// sesenta segundos de sesion SANA, la condicion no vuelve a cumplirse nunca y
/// el primer tropiezo de red termina la sesion para siempre. Toda la maquinaria
/// de la Fase 14 -- el token de reconexion, los 30 s de gracia del relay, el
/// backoff -- quedaba inalcanzable salvo en el primer minuto.
///
/// Medido en produccion el 25/08/2026: tres sesiones muertas a las 1:57, 3:34 y
/// 5:57 horas, cada una en el primer corte que le toco, ninguna reintentada.
///
/// Vive en el contrato y no en cada extremo para que la aritmetica sea UNA y se
/// pruebe sin red ni GPU. Es todo lo que se puede probar de una reconexion en CI.
/// </summary>
public static class VentanaDeReconexion
{
    /// <summary>Lo que se insiste desde el corte. Mas larga que la gracia de 30 s
    /// del relay a proposito: quien decide si el token vale es el servidor
    /// rechazandolo, no una cuenta atras de este lado.</summary>
    public static readonly TimeSpan Duracion = TimeSpan.FromMinutes(1);

    /// <summary>A partir de aqui, un intento cuenta como reconexion CONSEGUIDA y
    /// la racha se olvida. Sin esto, un microcorte a las tres horas heredaria la
    /// ventana ya gastada de un corte de hace tres horas.</summary>
    public static readonly TimeSpan Aguanto = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Marca desde la que se cuenta la ventana despues de que falle un intento.
    ///
    /// <paramref name="racha"/> es lo que traia la racha de fallos seguidos, o
    /// null si este es el primero; <paramref name="inicio"/> es cuando arranco
    /// EL intento que acaba de fallar.
    /// </summary>
    public static DateTimeOffset Corte(DateTimeOffset? racha, DateTimeOffset inicio, DateTimeOffset ahora)
        => racha is null || ahora - inicio > Aguanto ? ahora : racha.Value;

    /// <summary>Si todavia se puede reintentar.</summary>
    public static bool Sigue(DateTimeOffset corte, DateTimeOffset ahora)
        => ahora - corte < Duracion;
}
