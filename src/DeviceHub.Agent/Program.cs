using DeviceHub.Agent;
using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Inventory;
using DeviceHub.Agent.Security;

// Diagnostico de campo: "que ve DeviceHub en esta PC?" sin instalar el servicio
// ni levantar el servidor. Contesta la pregunta que si no obliga a adivinar.
if (args.Contains("--inventory"))
{
    Console.WriteLine(Google.Protobuf.JsonFormatter.Default.Format(HardwareCollector.Collect()));
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
