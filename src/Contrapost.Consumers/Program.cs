using Contrapost.Consumers;
using Contrapost.Infrastructure.Observability;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddConsumers(builder.Configuration);
builder.Services.AddTelemetry(builder.Configuration, "contrapost-consumers");

await builder.Build().RunAsync();
