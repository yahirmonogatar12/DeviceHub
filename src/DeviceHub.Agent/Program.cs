using DeviceHub.Agent;
using DeviceHub.Agent.Identity;
using DeviceHub.Agent.Security;

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
