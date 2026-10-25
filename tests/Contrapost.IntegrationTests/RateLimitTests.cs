using System.Globalization;
using System.Net;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>Per-client sliding-window rate limiting in Redis (design doc §11).</summary>
[Collection("api")]
public sealed class RateLimitTests(ApiFixture api)
{
    private static readonly Uri AnyAccount = new($"/v1/accounts/{Guid.NewGuid()}", UriKind.Relative);

    [Fact]
    public async Task Requests_beyond_the_clients_limit_get_429_with_retry_after()
    {
        await using var host = api.CreateApi(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = api.RedisConnectionString });
        using var client = await api.NewTestClientOnAsync(host, rateLimitPerMinute: 3);

        var remaining = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var allowed = await client.Http.GetAsync(AnyAccount);
            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode); // the request ran: the account just doesn't exist
            remaining.Add(allowed.Headers.GetValues("X-RateLimit-Remaining").Single());
        }

        var limited = await client.Http.GetAsync(AnyAccount);

        Assert.Equal(["2", "1", "0"], remaining);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/rate-limited", (await limited.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("3", limited.Headers.GetValues("X-RateLimit-Limit").Single());
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public async Task Limits_are_per_client()
    {
        await using var host = api.CreateApi(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = api.RedisConnectionString });
        using var busy = await api.NewTestClientOnAsync(host, rateLimitPerMinute: 1);
        using var quiet = await api.NewTestClientOnAsync(host, rateLimitPerMinute: 1);

        await busy.Http.GetAsync(AnyAccount);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await busy.Http.GetAsync(AnyAccount)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await quiet.Http.GetAsync(AnyAccount)).StatusCode);
    }

    [Fact]
    public async Task A_limited_request_does_not_claim_its_idempotency_key()
    {
        await using var host = api.CreateApi(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = api.RedisConnectionString });
        using var client = await api.NewTestClientOnAsync(host, rateLimitPerMinute: 1);
        await client.Http.GetAsync(AnyAccount);

        var limited = await client.Http.PostTransferWithKeyAsync("after-limit", "INR", null, (Guid.NewGuid(), "debit", 1), (Guid.NewGuid(), "credit", 1));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(0, await api.ScalarAsync<long>(
            "SELECT count(*) FROM idempotency_keys WHERE client_id = @c", ("c", client.ClientId)));
    }

    [Fact]
    public async Task Requests_go_through_when_redis_is_unavailable()
    {
        // ADR-006: the limiter fails open rather than taking the API down with Redis.
        await using var host = api.CreateApi(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "localhost:1" });
        using var client = await api.NewTestClientOnAsync(host, rateLimitPerMinute: 1);

        for (var i = 0; i < 3; i++)
        {
            var response = await client.Http.GetAsync(AnyAccount);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("X-RateLimit-Remaining").Single());
        }
    }
}
