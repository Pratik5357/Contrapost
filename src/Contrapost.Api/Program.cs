using System.Text.Json;
using System.Text.Json.Serialization;
using Contrapost.Api.Auth;
using Contrapost.Api.Endpoints;
using Contrapost.Application;
using Contrapost.Application.Clients;
using Contrapost.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddApiKeyAuth();
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

// `Contrapost.Api create-client <name> [scope ...]` creates a client and prints its API key once.
if (args is ["create-client", var clientName, .. var requestedScopes])
{
    var scopes = requestedScopes.Length > 0 ? requestedScopes : Scopes.All.Where(s => s != Scopes.AdminRead).ToArray();
    if (scopes.Except(Scopes.All).ToList() is [_, ..] unknown)
    {
        await Console.Error.WriteLineAsync($"Unknown scopes: {string.Join(", ", unknown)}. Valid: {string.Join(", ", Scopes.All)}");
        Environment.ExitCode = 1;
        return;
    }

    var key = ApiKeys.Generate(app.Configuration["ApiKeys:Environment"] ?? "live");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider.GetRequiredService<IApiClientStore>()
            .CreateAsync(Guid.CreateVersion7(), clientName, key, scopes, CancellationToken.None);
    }

    Console.WriteLine($"client: {clientName}");
    Console.WriteLine($"scopes: {string.Join(" ", scopes)}");
    Console.WriteLine($"api_key: {key.Key}");
    Console.WriteLine("Store the key now; only its hash is kept.");
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(Contrapost.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapGroup("/v1")
    .RequireAuthorization()
    .MapAccountEndpoints()
    .MapTransferEndpoints();

await app.RunAsync();
