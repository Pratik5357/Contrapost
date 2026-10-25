using System.Text.Json;
using System.Text.Json.Serialization;
using Contrapost.Api.Auth;
using Contrapost.Api.Endpoints;
using Contrapost.Api.Http;
using Contrapost.Application;
using Contrapost.Application.Clients;
using Contrapost.Application.Reconciliation;
using Contrapost.Infrastructure;
using Contrapost.Infrastructure.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddApiKeyAuth();
builder.Services.AddTelemetry(builder.Configuration, "contrapost-api",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());
var rateLimited = builder.Services.AddRateLimiting(builder.Configuration);
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

// `Contrapost.Api reconcile` runs reconciliation once. Exit code 0: clean, 2: drift found, 1: not run.
if (args is ["reconcile", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    var run = await scope.ServiceProvider.GetRequiredService<IReconciliationStore>().RunAsync(CancellationToken.None);
    if (run is null)
    {
        await Console.Error.WriteLineAsync("Another reconciliation run is in progress.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"run: {run.Id}");
    Console.WriteLine($"status: {run.Status}");
    Console.WriteLine($"accounts_checked: {run.AccountsChecked}");
    Console.WriteLine($"findings: {run.FindingsCount}");
    Environment.ExitCode = run.Status == "clean" ? 0 : 2;
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

if (!rateLimited)
{
#pragma warning disable CA1848 // logged once at startup; a LoggerMessage delegate buys nothing here
    app.Logger.LogWarning("Rate limiting is disabled: ConnectionStrings:Redis is not configured");
#pragma warning restore CA1848
}

var v1 = app.MapGroup("/v1").RequireAuthorization();
if (rateLimited)
{
    v1.AddEndpointFilter<RateLimitFilter>();
}

v1
    .MapAccountEndpoints()
    .MapTransferEndpoints()
    .MapHoldEndpoints()
    .MapAdminEndpoints();

await app.RunAsync();
