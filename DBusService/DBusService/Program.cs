using DBusService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<GlobalMenuExporter>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
