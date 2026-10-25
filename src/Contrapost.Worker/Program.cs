using Contrapost.Infrastructure;
using Contrapost.Infrastructure.Observability;
using Contrapost.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration, "contrapost-worker");
builder.Services.AddTelemetry(builder.Configuration, "contrapost-worker");
builder.Services.AddSingleton<LedgerHealthMetrics>();
builder.Services.AddHostedService<OutboxMonitorJob>();
builder.Services.AddHostedService<HoldExpiryJob>();
builder.Services.AddHostedService<OutboxRelayJob>();
builder.Services.AddHostedService<ReconciliationJob>();
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
