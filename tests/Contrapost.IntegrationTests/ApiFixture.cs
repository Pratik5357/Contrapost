using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contrapost.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

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
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            builder.UseSetting("Auth:DevClientHeader", "true");
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
    }

    /// <summary>Creates a fresh API client row and an HttpClient authenticated as it, so tests don't share state.</summary>
    public async Task<HttpClient> NewClientAsync()
    {
        var clientId = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes) VALUES (@id, 'test', @prefix, '\\x00', '{}')",
                connection);
            command.Parameters.AddWithValue("id", clientId);
            command.Parameters.AddWithValue("prefix", $"cp_test_{clientId:N}");
            await command.ExecuteNonQueryAsync();
        }

        var http = _factory!.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", clientId.ToString());
        return http;
    }

    public HttpClient AnonymousClient() => _factory!.CreateClient();

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

[CollectionDefinition("api")]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>;

/// <summary>Typed helpers over the HTTP API, mirroring the request and response shapes.</summary>
public static class LedgerApi
{
    public sealed record Balance(long Posted, long Available, long DebitsPosted, long CreditsPosted, long DebitsPending, long CreditsPending);

    public sealed record Account(Guid Id, string Code, string Currency, string NormalSide, bool PreventOverdraft, string Status, Balance Balance);

    public sealed record Leg(Guid AccountId, string Direction, long Amount, long AccountSeq);

    public sealed record Transfer(Guid Id, string Status, string Currency, string? Reference, IReadOnlyList<Leg> Legs);

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
        http.PostAsJsonAsync("/v1/transfers", new
        {
            currency,
            reference,
            legs = legs.Select(l => new { account_id = l.Account, direction = l.Direction, amount = l.Amount }),
        });

    /// <summary>Funds a wallet from a system account that is allowed to go negative.</summary>
    public static async Task<Account> FundedWalletAsync(this HttpClient http, string code, long amount, string currency = "INR")
    {
        var funding = await http.CreateAccountAsync($"funding:{code}", currency, normalSide: "debit", preventOverdraft: false);
        var wallet = await http.CreateAccountAsync(code, currency);
        var response = await http.PostTransferAsync(currency, (funding.Id, "debit", amount), (wallet.Id, "credit", amount));
        response.EnsureSuccessStatusCode();
        return wallet;
    }

    public static async Task<Balance> BalanceAsync(this HttpClient http, Guid accountId) =>
        (await http.GetFromJsonAsync<Balance>($"/v1/accounts/{accountId}/balance", ApiFixture.Json))!;

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
