namespace DeviceHub.Remote.Contracts;

/// <summary>
/// Estado de una sesion de control remoto en el relay.
///
/// Host y viewer llegan por separado y en cualquier orden, asi que hay dos
/// estados de espera distintos: saber a quien falta es lo que permite decirle al
/// tecnico "la PC no respondio" en vez de un timeout mudo.
/// </summary>
public enum RemoteSessionState
{
    /// <summary>Creada por StartRemoteSession; los tickets ya existen.</summary>
    Created = 0,

    /// <summary>El viewer conecto primero. Falta que el agente arranque el host.</summary>
    WaitingForHost = 1,

    /// <summary>El host conecto primero. El tecnico aun no ha abierto el viewer.</summary>
    WaitingForViewer = 2,

    /// <summary>Los dos extremos emparejados. El relay reenvia.</summary>
    Connected = 3,

    /// <summary>Cierre en curso: se avisa a ambos extremos antes de soltar.</summary>
    Closing = 4,

    /// <summary>Terminada con normalidad.</summary>
    Closed = 5,

    /// <summary>
    /// Terminada por error: ticket vencido, nadie con sesion interactiva en la
    /// PC, o un extremo que se cayo. El motivo se audita.
    /// </summary>
    Failed = 6
}
