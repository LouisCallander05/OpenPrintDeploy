using OpenPrintDeploy.Client.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
