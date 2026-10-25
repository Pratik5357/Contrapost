using System.Net;
using System.Net.Http.Json;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

[Collection("api")]
public sealed class AccountTests(ApiFixture api)
{
    [Fact]
    public async Task Created_account_starts_at_zero_and_can_be_read_back()
    {
        using var http = await api.NewClientAsync();

        var response = await http.PostAsJsonAsync("/v1/accounts", new { code = "wallet:alice", currency = "INR", normal_side = "credit" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<Account>(ApiFixture.Json))!;
        Assert.Equal($"/v1/accounts/{created.Id}", response.Headers.Location?.OriginalString);
        Assert.True(created.PreventOverdraft);
        Assert.Equal(new Balance(0, 0, 0, 0, 0, 0), created.Balance);

        var fetched = await http.GetFromJsonAsync<Account>($"/v1/accounts/{created.Id}", ApiFixture.Json);
        Assert.Equal(created, fetched);
    }

    [Fact]
    public async Task Account_creation_writes_an_outbox_message()
    {
        using var http = await api.NewClientAsync();

        var account = await http.CreateAccountAsync("wallet:events");

        var count = await api.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE aggregate_id = @id AND type = 'account.created'", ("id", account.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Duplicate_code_for_the_same_client_is_a_conflict()
    {
        using var http = await api.NewClientAsync();
        await http.CreateAccountAsync("wallet:dup");

        var response = await http.PostAsJsonAsync("/v1/accounts", new { code = "wallet:dup", currency = "INR", normal_side = "credit" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/account-code-taken", (await response.ProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Same_code_is_allowed_for_different_clients()
    {
        using var first = await api.NewClientAsync();
        using var second = await api.NewClientAsync();

        await first.CreateAccountAsync("wallet:shared-code");
        await second.CreateAccountAsync("wallet:shared-code");
    }

    [Fact]
    public async Task Invalid_fields_return_400_with_field_errors()
    {
        using var http = await api.NewClientAsync();

        var response = await http.PostAsJsonAsync("/v1/accounts", new { code = "has spaces", currency = "inr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ProblemAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("code", out _));
        Assert.True(errors.TryGetProperty("currency", out _));
        Assert.True(errors.TryGetProperty("normal_side", out _));
    }

    [Fact]
    public async Task Another_clients_account_is_not_found()
    {
        using var owner = await api.NewClientAsync();
        using var other = await api.NewClientAsync();
        var account = await owner.CreateAccountAsync("wallet:private");

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(new Uri($"/v1/accounts/{account.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(new Uri($"/v1/accounts/{account.Id}/entries", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_known_client_are_unauthenticated()
    {
        using var anonymous = api.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri($"/v1/accounts/{Guid.NewGuid()}", UriKind.Relative))).StatusCode);

        anonymous.DefaultRequestHeaders.Add("X-Client-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri($"/v1/accounts/{Guid.NewGuid()}", UriKind.Relative))).StatusCode);
    }
}
