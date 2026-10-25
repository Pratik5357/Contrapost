using System.Net;
using System.Net.Http.Json;
using Npgsql;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>The rules in design doc §5.1 and §7.3.</summary>
[Collection("api")]
public sealed class IdempotencyTests(ApiFixture api)
{
    [Fact]
    public async Task Retry_with_the_same_key_replays_the_response_and_posts_once()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        var first = await http.PostTransferWithKeyAsync("k1", "INR", null, (alice.Id, "debit", 300), (bob.Id, "credit", 300));
        var retry = await http.PostTransferWithKeyAsync("k1", "INR", null, (alice.Id, "debit", 300), (bob.Id, "credit", 300));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Headers.Location, retry.Headers.Location);

        var original = await first.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json);
        var replayed = await retry.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json);
        Assert.Equal(original!.Id, replayed!.Id);
        Assert.Equal(original.Legs, replayed.Legs);

        Assert.Equal(700, (await http.BalanceAsync(alice.Id)).Posted);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_rejected()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        await http.PostTransferWithKeyAsync("k1", "INR", null, (alice.Id, "debit", 300), (bob.Id, "credit", 300));

        var reused = await http.PostTransferWithKeyAsync("k1", "INR", null, (alice.Id, "debit", 999), (bob.Id, "credit", 999));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/idempotency-key-reused", (await reused.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(700, (await http.BalanceAsync(alice.Id)).Posted);
    }

    [Fact]
    public async Task Key_is_required_on_transfers()
    {
        using var http = await api.NewClientAsync();

        var response = await http.PostTransferWithKeyAsync(null, "INR", null, (Guid.NewGuid(), "debit", 1), (Guid.NewGuid(), "credit", 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ProblemAsync()).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task Business_rejection_is_stored_so_a_retry_gets_the_same_answer()
    {
        using var http = await api.NewClientAsync();
        var funding = await http.CreateAccountAsync("funding:INR", normalSide: "debit", preventOverdraft: false);
        var alice = await http.CreateAccountAsync("wallet:alice");
        var bob = await http.CreateAccountAsync("wallet:bob");

        var rejected = await http.PostTransferWithKeyAsync("pay-1", "INR", null, (alice.Id, "debit", 500), (bob.Id, "credit", 500));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        // Funds arrive, but the key's outcome is already decided.
        (await http.PostTransferAsync("INR", (funding.Id, "debit", 500), (alice.Id, "credit", 500))).EnsureSuccessStatusCode();
        var retry = await http.PostTransferWithKeyAsync("pay-1", "INR", null, (alice.Id, "debit", 500), (bob.Id, "credit", 500));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        var original = await rejected.ProblemAsync();
        var replayed = await retry.ProblemAsync();
        Assert.Equal("https://contrapost.dev/errors/insufficient-funds", replayed.GetProperty("type").GetString());
        // The replay is what was written the first time, including fields added while writing.
        Assert.Equal(original.GetProperty("trace_id").GetString(), replayed.GetProperty("trace_id").GetString());
        Assert.Equal(0, (await http.BalanceAsync(bob.Id)).Posted);
    }

    [Fact]
    public async Task Duplicate_reference_conflict_is_stored_after_its_savepoint_rollback()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        (await http.PostTransferWithKeyAsync("a", "INR", "order-1", (alice.Id, "debit", 100), (bob.Id, "credit", 100))).EnsureSuccessStatusCode();

        var conflict = await http.PostTransferWithKeyAsync("b", "INR", "order-1", (alice.Id, "debit", 100), (bob.Id, "credit", 100));
        var retry = await http.PostTransferWithKeyAsync("b", "INR", "order-1", (alice.Id, "debit", 100), (bob.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [Fact]
    public async Task Validation_failures_are_not_stored()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        var invalid = await http.PostTransferWithKeyAsync("k", "INR", null, (alice.Id, "debit", 100), (bob.Id, "credit", 90));
        var fixedUp = await http.PostTransferWithKeyAsync("k", "INR", null, (alice.Id, "debit", 100), (bob.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fixedUp.StatusCode);
    }

    [Fact]
    public async Task Keys_are_scoped_per_client()
    {
        using var first = await api.NewClientAsync();
        using var second = await api.NewClientAsync();
        var a = await first.FundedWalletAsync("wallet:a", 100);
        var b = await first.CreateAccountAsync("wallet:b");
        var c = await second.FundedWalletAsync("wallet:c", 100);
        var d = await second.CreateAccountAsync("wallet:d");

        Assert.Equal(HttpStatusCode.Created, (await first.PostTransferWithKeyAsync("shared", "INR", null, (a.Id, "debit", 10), (b.Id, "credit", 10))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await second.PostTransferWithKeyAsync("shared", "INR", null, (c.Id, "debit", 10), (d.Id, "credit", 10))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicates_post_exactly_once()
    {
        using var client = await api.NewTestClientAsync();
        var http = client.Http;
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            http.PostTransferWithKeyAsync("burst", "INR", null, (alice.Id, "debit", 100), (bob.Id, "credit", 100))));

        // Duplicates wait on the uncommitted key and then replay; none post a second time.
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var ids = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Created)
            .Select(async r => (await r.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(900, (await http.BalanceAsync(alice.Id)).Posted);
        Assert.Equal(2, await api.ScalarAsync<long>("SELECT count(*) FROM transactions WHERE client_id = @c", ("c", client.ClientId)));
    }

    [Fact]
    public async Task The_key_lock_timeout_does_not_limit_waits_for_busy_accounts()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        // Another transaction holds alice's balance row for longer than the 1 s key lock timeout.
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var holder = await connection.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM account_balances WHERE account_id = @a FOR UPDATE", connection, holder))
        {
            lockRow.Parameters.AddWithValue("a", alice.Id);
            await lockRow.ExecuteScalarAsync();
        }

        var transfer = http.PostTransferAsync("INR", (alice.Id, "debit", 100), (bob.Id, "credit", 100));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(transfer.IsCompleted);

        await holder.RollbackAsync();
        Assert.Equal(HttpStatusCode.Created, (await transfer).StatusCode);
    }

    [Fact]
    public async Task Duplicate_of_a_request_still_running_gets_409_after_the_lock_timeout()
    {
        using var client = await api.NewTestClientAsync();
        var alice = await client.Http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await client.Http.CreateAccountAsync("wallet:bob");

        // Simulate the first request: an uncommitted claim on the key, held open.
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var holder = await connection.BeginTransactionAsync();
        await using (var claim = new NpgsqlCommand(
            "INSERT INTO idempotency_keys (client_id, key, request_method, request_path, request_hash) VALUES (@c, 'slow', 'POST', '/v1/transfers', '\\x00')",
            connection, holder))
        {
            claim.Parameters.AddWithValue("c", client.ClientId);
            await claim.ExecuteNonQueryAsync();
        }

        var duplicate = await client.Http.PostTransferWithKeyAsync("slow", "INR", null, (alice.Id, "debit", 100), (bob.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/request-in-progress", (await duplicate.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("1", duplicate.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1_000, (await client.Http.BalanceAsync(alice.Id)).Posted);
    }
}
