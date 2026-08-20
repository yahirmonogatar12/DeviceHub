namespace DeviceHub.RemoteViewer.Render;

/// <summary>
/// Con que tamano se pinta la pantalla remota. Fase 20.
///
/// Clase suelta y sin nada de WPF a proposito: vivia dentro de RelayWindow, y
/// para probar un metodo estatico de una clase que hereda de Window el proyecto
/// de pruebas tendria que activar UseWPF entero. Aqui son cuatro lineas de
/// aritmetica que se prueban sin arrastrar la interfaz detras.
/// </summary>
public static class Escalado
{
    /// <summary>
    /// `escala` 0 = adaptar al hueco disponible; cualquier otro valor es el
    /// factor sobre los pixeles reales de la pantalla remota (1 = original).
    ///
    /// Adaptar CONSERVA la relacion de aspecto. Llenar el hueco estirando es lo
    /// que hacia el swapchain por su cuenta con Scaling.Stretch, y deformaba la
    /// pantalla remota: en una PC de planta eso se nota enseguida en el texto.
    /// </summary>
    public static (double Ancho, double Alto) Encajar(
        int videoAncho, int videoAlto, double huecoAncho, double huecoAlto, double escala)
    {
        var factor = escala > 0
            ? escala
            : Math.Min(huecoAncho / videoAncho, huecoAlto / videoAlto);

        // Nunca 0: WPF trata Width=0 como "sin asignar" y el video desaparece
        // hasta el siguiente redimensionado.
        return (Math.Max(videoAncho * factor, 1), Math.Max(videoAlto * factor, 1));
    }
}
