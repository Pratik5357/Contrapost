using Contrapost.Infrastructure;
using Contrapost.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration, "contrapost-worker");
builder.Services.AddHostedService<HoldExpiryJob>();
builder.Services.AddHostedService<OutboxRelayJob>();
builder.Services.AddHostedService<ReconciliationJob>();
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
