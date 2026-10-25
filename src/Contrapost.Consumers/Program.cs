using Contrapost.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddConsumers(builder.Configuration);

await builder.Build().RunAsync();
