using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contrapost.Application.Clients;
using Contrapost.Consumers;
using Contrapost.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Contrapost.IntegrationTests;

/// <summary>One migrated database and API host shared by every test in the "api" collection.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString => _postgres.GetConnectionString();

    public string RabbitUri => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            builder.UseSetting("ApiKeys:Environment", "test");
            // Short, so the in-progress test doesn't wait the production 5 s.
            builder.UseSetting("Idempotency:LockTimeoutMs", "1000");
        });
        await _factory.Services.MigrateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    /// <summary>The services the Worker and Consumers processes run with (relay, inbox, handlers), unstarted.</summary>
    public ServiceProvider MessagingServices(string? rabbitUri = null)
    {
        var configuration = MessagingConfiguration(rabbitUri);
        var services = new ServiceCollection().AddLogging().AddSingleton(configuration);
        services.AddConsumers(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>A running Consumers process: both queue consumers start and consume until disposed.</summary>
    public async Task<IHost> StartConsumersAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(MessagingConfiguration(null));
        builder.Services.AddConsumers(builder.Configuration);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private IConfiguration MessagingConfiguration(string? rabbitUri) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            ["ConnectionStrings:RabbitMq"] = rabbitUri ?? RabbitUri,
            ["Consumers:LowBalanceThreshold"] = "1000",
        })
        .Build();

    /// <summary>Creates a fresh API client with its own key, so tests don't share state.</summary>
    public async Task<TestClient> NewTestClientAsync(params string[] scopes)
    {
        var clientId = Guid.CreateVersion7();
        var key = ApiKeys.Generate("test");
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiClientStore>()
                .CreateAsync(clientId, "test", key, scopes.Length > 0 ? scopes : Scopes.All, CancellationToken.None);
        }

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        return new TestClient(http, clientId, key);
    }

    public async Task<HttpClient> NewClientAsync(params string[] scopes) => (await NewTestClientAsync(scopes)).Http;

    public HttpClient AnonymousClient() => _factory!.CreateClient();

    public async Task<Contrapost.Application.Reconciliation.ReconciliationRunView?> ReconcileAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Contrapost.Application.Reconciliation.IReconciliationStore>()
            .RunAsync(CancellationToken.None);
    }

    /// <summary>Runs one expiry batch the way the Worker's job does.</summary>
    public async Task<int> ExpireDueHoldsAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Contrapost.Application.Holds.IHoldStore>()
            .ExpireDueAsync(100, CancellationToken.None);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}

public sealed record TestClient(HttpClient Http, Guid ClientId, ApiKeys.Generated Key) : IDisposable
{
    public void Dispose() => Http.Dispose();
}

[CollectionDefinition("api")]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>;

/// <summary>Typed helpers over the HTTP API, mirroring the request and response shapes.</summary>
public static class LedgerApi
{
    public sealed record Balance(long Posted, long Available, long DebitsPosted, long CreditsPosted, long DebitsPending, long CreditsPending);

    public sealed record Account(Guid Id, string Code, string Currency, string NormalSide, bool PreventOverdraft, string Status, Balance Balance);

    public sealed record Leg(Guid AccountId, string Direction, long Amount, long AccountSeq);

    public sealed record Transfer(Guid Id, string Status, string Currency, string? Reference, IReadOnlyList<Leg> Legs,
        string Kind = "", Guid? HoldId = null, Guid? ReversesTransactionId = null);

    public sealed record Hold(Guid Id, string Status, Guid DebitAccountId, Guid CreditAccountId, string Currency,
        long Amount, long CapturedAmount, long Remaining, DateTimeOffset ExpiresAt);

    public sealed record Capture(Hold Hold, Transfer Transaction);

    public sealed record Entry(Guid TransactionId, long AccountSeq, string Direction, long Amount);

    public sealed record EntryPage(IReadOnlyList<Entry> Entries, string? NextCursor);

    public static async Task<Account> CreateAccountAsync(this HttpClient http, string code, string currency = "INR",
        string normalSide = "credit", bool preventOverdraft = true)
    {
        var response = await http.PostAsJsonAsync("/v1/accounts",
            new { code, currency, normal_side = normalSide, prevent_overdraft = preventOverdraft });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Account>(ApiFixture.Json))!;
    }

    public static Task<HttpResponseMessage> PostTransferAsync(this HttpClient http, string currency, params (Guid Account, string Direction, long Amount)[] legs) =>
        http.PostTransferAsync(currency, null, legs);

    public static Task<HttpResponseMessage> PostTransferAsync(this HttpClient http, string currency, string? reference,
        params (Guid Account, string Direction, long Amount)[] legs) =>
        http.PostTransferWithKeyAsync(Guid.NewGuid().ToString(), currency, reference, legs);

    public static Task<HttpResponseMessage> PostTransferWithKeyAsync(this HttpClient http, string? idempotencyKey, string currency,
        string? reference, params (Guid Account, string Direction, long Amount)[] legs) =>
        http.PostWithKeyAsync("/v1/transfers", idempotencyKey, new
        {
            currency,
            reference,
            legs = legs.Select(l => new { account_id = l.Account, direction = l.Direction, amount = l.Amount }),
        });

    public static async Task<HttpResponseMessage> PostWithKeyAsync(this HttpClient http, string path, string? idempotencyKey, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await http.SendAsync(request);
    }

    /// <summary>Funds a wallet from a system account that is allowed to go negative.</summary>
    public static async Task<Account> FundedWalletAsync(this HttpClient http, string code, long amount, string currency = "INR")
    {
        var funding = await http.CreateAccountAsync($"funding:{code}", currency, normalSide: "debit", preventOverdraft: false);
        var wallet = await http.CreateAccountAsync(code, currency);
        var response = await http.PostTransferAsync(currency, (funding.Id, "debit", amount), (wallet.Id, "credit", amount));
        response.EnsureSuccessStatusCode();
        return wallet;
    }

    public static Task<HttpResponseMessage> PostHoldAsync(this HttpClient http, Guid debit, Guid credit, long amount,
        int? expiresInSeconds = null, string currency = "INR") =>
        http.PostWithKeyAsync("/v1/holds", Guid.NewGuid().ToString(), new
        {
            debit_account_id = debit,
            credit_account_id = credit,
            currency,
            amount,
            expires_in_seconds = expiresInSeconds,
        });

    public static async Task<Hold> CreateHoldAsync(this HttpClient http, Guid debit, Guid credit, long amount, int? expiresInSeconds = null)
    {
        var response = await http.PostHoldAsync(debit, credit, amount, expiresInSeconds);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Hold>(ApiFixture.Json))!;
    }

    public static Task<HttpResponseMessage> CaptureAsync(this HttpClient http, Guid holdId, long? amount = null, string? idempotencyKey = null) =>
        http.PostWithKeyAsync($"/v1/holds/{holdId}/capture", idempotencyKey ?? Guid.NewGuid().ToString(), new { amount });

    public static Task<HttpResponseMessage> VoidAsync(this HttpClient http, Guid holdId) =>
        http.PostWithKeyAsync($"/v1/holds/{holdId}/void", Guid.NewGuid().ToString(), new { });

    public static Task<HttpResponseMessage> ReverseAsync(this HttpClient http, Guid transactionId, string? idempotencyKey = null) =>
        http.PostWithKeyAsync($"/v1/transfers/{transactionId}/reversal", idempotencyKey ?? Guid.NewGuid().ToString(), new { });

    public static async Task<Hold> HoldAsync(this HttpClient http, Guid holdId) =>
        (await http.GetFromJsonAsync<Hold>($"/v1/holds/{holdId}", ApiFixture.Json))!;

    public static async Task<Balance> BalanceAsync(this HttpClient http, Guid accountId) =>
        (await http.GetFromJsonAsync<Balance>($"/v1/accounts/{accountId}/balance", ApiFixture.Json))!;

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
