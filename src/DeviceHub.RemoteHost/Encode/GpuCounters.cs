using System.Diagnostics;
using Vortice.DXGI;

namespace DeviceHub.RemoteHost.Encode;

/// <summary>
/// Uso del motor de codificacion de la GPU y VRAM ocupada.
///
/// El porcentaje del motor de video es lo que distingue "codifica en hardware" de
/// "codifica en hardware y va ahogada". Un 95% con 30 FPS significa que esa PC no
/// tiene margen para nada mas, y en planta esa PC ademas corre el software de test.
///
/// Todo es best-effort: si los contadores no estan, se reporta null en vez de
/// tumbar la medida.
/// </summary>
public sealed class GpuCounters : IDisposable
{
    private readonly List<PerformanceCounter> _videoEncode = [];
    private readonly IDXGIAdapter3? _adapter;

    public GpuCounters(int adapterIndex)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            if (factory.EnumAdapters1((uint)adapterIndex, out var adapter).Success && adapter is not null)
            {
                using (adapter)
                    _adapter = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
            }
        }
        catch (Exception)
        {
            _adapter = null;
        }

        try
        {
            var categoria = new PerformanceCounterCategory("GPU Engine");

            foreach (var instancia in categoria.GetInstanceNames())
            {
                if (instancia.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase))
                    _videoEncode.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", instancia, true));
            }
        }
        catch (Exception)
        {
            // Sin contadores de GPU: se reporta null y ya.
            _videoEncode.Clear();
        }
    }

    /// <summary>
    /// Suma del uso de TODOS los motores de codificacion. Windows reparte el
    /// trabajo entre varias instancias, asi que mirar solo una da un numero
    /// tranquilizador y falso.
    /// </summary>
    public double? VideoEncodePercent()
    {
        if (_videoEncode.Count == 0)
            return null;

        try
        {
            return _videoEncode.Sum(c => c.NextValue());
        }
        catch (Exception)
        {
            return null;
        }
    }

    public long? VideoMemoryBytes()
    {
        if (_adapter is null)
            return null;

        try
        {
            return (long)_adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var contador in _videoEncode)
            contador.Dispose();

        _adapter?.Dispose();
    }
}
