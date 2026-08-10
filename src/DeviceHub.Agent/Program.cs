using DeviceHub.Agent;
using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Inventory;
using DeviceHub.Agent.Monitoring;
using DeviceHub.Agent.Security;

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

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<PinnedChannelFactory>();
builder.Services.AddSingleton(sp => new MachineIdentity(
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory,
    sp.GetRequiredService<ILogger<MachineIdentity>>()));

builder.Services.AddHostedService<Worker>();

// Regla 12: el servicio debe funcionar aunque ningun usuario haya iniciado sesion.
builder.Services.AddWindowsService(options => options.ServiceName = "DeviceHubAgent");

await builder.Build().RunAsync();
