using System.Net;
using System.Net.Http.Headers;
using Contrapost.Application.Clients;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

[Collection("api")]
public sealed class AuthTests(ApiFixture api)
{
    private static readonly Uri AnyAccount = new($"/v1/accounts/{Guid.NewGuid()}", UriKind.Relative);

    [Fact]
    public async Task Missing_key_is_401_with_problem_details_and_a_bearer_challenge()
    {
        using var anonymous = api.AnonymousClient();

        var response = await anonymous.GetAsync(AnyAccount);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("https://contrapost.dev/errors/unauthenticated", (await response.ProblemAsync()).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("cp_test_nodot")]
    [InlineData("cp_test_unknown.secret")]
    public async Task Malformed_or_unknown_keys_are_401(string key)
    {
        using var anonymous = api.AnonymousClient();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AnyAccount)).StatusCode);
    }

    [Fact]
    public async Task Right_prefix_with_the_wrong_secret_is_401()
    {
        using var client = await api.NewTestClientAsync();
        using var impostor = api.AnonymousClient();
        impostor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{client.Key.Prefix}.wrong-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, (await impostor.GetAsync(AnyAccount)).StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_401()
    {
        using var client = await api.NewTestClientAsync();
        var account = await client.Http.CreateAccountAsync("wallet:alice");

        await api.ScalarAsync<int>("UPDATE api_clients SET revoked_at = now() WHERE id = @id RETURNING 1", ("id", client.ClientId));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.Http.GetAsync(new Uri($"/v1/accounts/{account.Id}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Missing_scope_is_403_and_granted_scopes_work()
    {
        using var writer = await api.NewTestClientAsync();
        var account = await writer.Http.CreateAccountAsync("wallet:alice");

        // A read-only key for the same client
        var readOnlyKey = ApiKeys.Generate("test");
        await api.ScalarAsync<int>(
            "INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes) VALUES (@id, 'ro', @prefix, @hash, @scopes) RETURNING 1",
            ("id", Guid.CreateVersion7()), ("prefix", readOnlyKey.Prefix), ("hash", readOnlyKey.SecretHash),
            ("scopes", new[] { Scopes.AccountsRead }));
        using var reader = api.AnonymousClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readOnlyKey.Key);

        var create = await reader.PostWithKeyAsync("/v1/accounts", null, new { code = "wallet:nope", currency = "INR", normal_side = "credit" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/insufficient-scope", (await create.ProblemAsync()).GetProperty("type").GetString());

        var transfer = await reader.PostTransferAsync("INR", (account.Id, "debit", 1), (Guid.NewGuid(), "credit", 1));
        Assert.Equal(HttpStatusCode.Forbidden, transfer.StatusCode);

        // The read-only key belongs to a different client row, so it can't see writer's account either.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(new Uri($"/v1/accounts/{account.Id}", UriKind.Relative))).StatusCode);
    }
}
