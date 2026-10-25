using System.Text.Json;
using System.Text.Json.Serialization;
using Contrapost.Api.Auth;
using Contrapost.Api.Endpoints;
using Contrapost.Application;
using Contrapost.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();

// `Contrapost.Api migrate` applies migrations and exits; deploys run it as a separate step.
if (args is ["migrate", ..])
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(Contrapost.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapGroup("/v1")
    .AddEndpointFilter<DevClientAuthFilter>()
    .MapAccountEndpoints()
    .MapTransferEndpoints();

await app.RunAsync();
