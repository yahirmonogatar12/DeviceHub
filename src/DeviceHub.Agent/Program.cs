using DeviceHub.Agent;
using DeviceHub.Agent.Commands;
using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Inventory;
using DeviceHub.Agent.Monitoring;
using DeviceHub.Agent.Remote;
using DeviceHub.Agent.Security;
using DeviceHub.Agent.Updater;

// Diagnostico de campo: "que ve DeviceHub en esta PC?" sin instalar el servicio
// ni levantar el servidor. Contesta la pregunta que si no obliga a adivinar.
if (args.Contains("--inventory"))
{
    Console.WriteLine(Google.Protobuf.JsonFormatter.Default.Format(HardwareCollector.Collect()));
    return;
}

if (args.Contains("--metrics"))
{
    var sampler = new SystemSampler();
    Console.WriteLine("  CPU%   RAM%  Disco libre%      RX B/s      TX B/s");

    for (var i = 0; i < 3; i++)
    {
        await Task.Delay(MetricAggregation.SampleInterval);
        var sample = sampler.Sample();
        Console.WriteLine($"{sample.CpuPercent,6:0.0}{sample.MemoryPercent,7:0.0}{sample.DiskFreePercent,14:0.0}" +
                          $"{sample.NetRxBytesPerSec,12}{sample.NetTxBytesPerSec,12}");
    }

    return;
}

// ContentRootPath explicito, no opcional.
//
// Por defecto la raiz de contenido es el DIRECTORIO ACTUAL, y para un servicio
// de Windows ese directorio es C:\Windows\System32. Sin esto el agente no
// encuentra su appsettings.json y arranca con los valores por defecto: sin
// servidor, sin codigo de enrolamiento y sin pin, girando en vacio con un error
// que dice "sin codigo de enrolamiento" aunque el archivo lo tenga.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<PinnedChannelFactory>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton(sp => new UpdateService(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value,
    sp.GetRequiredService<ILogger<UpdateService>>()));

// Unico punto donde el agente elige motor remoto. Todo lo especifico de RustDesk
// (rutas, TOML, --get-id) vive dentro de esta implementacion.
builder.Services.AddSingleton<IRemoteAgentDetector, RustDeskDetector>();
builder.Services.AddSingleton(sp => new MachineIdentity(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory,
    sp.GetRequiredService<ILogger<MachineIdentity>>()));

builder.Services.AddHostedService<Worker>();

// Regla 12: el servicio debe funcionar aunque ningun usuario haya iniciado sesion.
builder.Services.AddWindowsService(options => options.ServiceName = "DeviceHubAgent");

await builder.Build().RunAsync();
