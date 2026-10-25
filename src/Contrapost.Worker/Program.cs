using Contrapost.Infrastructure;
using Contrapost.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<HoldExpiryJob>();

await builder.Build().RunAsync();
