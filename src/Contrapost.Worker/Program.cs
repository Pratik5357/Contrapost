using Contrapost.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
// Background jobs (outbox relay, hold expiry, reconciliation, cleanup) are registered here as they are built.

await builder.Build().RunAsync();
