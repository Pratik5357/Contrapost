using System.Net;
using System.Net.Http.Json;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

[Collection("api")]
public sealed class TransferTests(ApiFixture api)
{
    [Fact]
    public async Task Multi_leg_transfer_moves_money_and_records_postings()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 100_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var fees = await http.CreateAccountAsync("revenue:fees");

        var response = await http.PostTransferAsync("INR", "order-8841",
            (alice.Id, "debit", 50_000), (bob.Id, "credit", 48_500), (fees.Id, "credit", 1_500));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = (await response.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
        Assert.Equal("posted", transfer.Status);
        Assert.Equal(3, transfer.Legs.Count);
        // alice's first entry was the funding transfer, so this is her second
        Assert.Equal(2, transfer.Legs.Single(l => l.AccountId == alice.Id).AccountSeq);

        Assert.Equal(50_000, (await http.BalanceAsync(alice.Id)).Posted);
        Assert.Equal(48_500, (await http.BalanceAsync(bob.Id)).Posted);
        Assert.Equal(1_500, (await http.BalanceAsync(fees.Id)).Posted);

        var fetched = await http.GetFromJsonAsync<Transfer>($"/v1/transfers/{transfer.Id}", ApiFixture.Json);
        Assert.Equal(transfer.Legs, fetched!.Legs);

        var outbox = await api.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE aggregate_id = @id AND type = 'transfer.posted'", ("id", transfer.Id));
        Assert.Equal(1, outbox);
    }

    [Fact]
    public async Task Insufficient_funds_is_rejected_and_writes_nothing()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 32_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        var response = await http.PostTransferAsync("INR", (alice.Id, "debit", 50_000), (bob.Id, "credit", 50_000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("https://contrapost.dev/errors/insufficient-funds", problem.GetProperty("type").GetString());
        Assert.Equal("Available balance 32000 is less than the debit of 50000.", problem.GetProperty("detail").GetString());
        Assert.Equal(alice.Id, problem.GetProperty("account_id").GetGuid());

        Assert.Equal(32_000, (await http.BalanceAsync(alice.Id)).Posted);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM postings WHERE account_id = @id", ("id", bob.Id)));
    }

    [Fact]
    public async Task Account_without_overdraft_protection_can_go_negative()
    {
        using var http = await api.NewClientAsync();
        var external = await http.CreateAccountAsync("external:INR", preventOverdraft: false);
        var wallet = await http.CreateAccountAsync("wallet:alice");

        var response = await http.PostTransferAsync("INR", (external.Id, "debit", 500), (wallet.Id, "credit", 500));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(-500, (await http.BalanceAsync(external.Id)).Posted);
        Assert.Equal(500, (await http.BalanceAsync(wallet.Id)).Posted);
    }

    [Fact]
    public async Task Unbalanced_transfer_is_a_validation_error()
    {
        using var http = await api.NewClientAsync();

        var response = await http.PostTransferAsync("INR", (Guid.NewGuid(), "debit", 100), (Guid.NewGuid(), "credit", 90));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Debits (100) must equal credits (90).",
            (await response.ProblemAsync()).GetProperty("errors").GetProperty("legs")[0].GetString());
    }

    [Fact]
    public async Task Leg_with_missing_fields_is_a_validation_error()
    {
        using var http = await api.NewClientAsync();

        var response = await http.PostWithKeyAsync("/v1/transfers", "k-missing-fields",
            new { currency = "INR", legs = new object[] { new { amount = 5 }, new { amount = 5 } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ProblemAsync()).GetProperty("errors").TryGetProperty("legs[0].account_id", out _));
    }

    [Fact]
    public async Task Currency_mismatch_is_rejected()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var usd = await http.CreateAccountAsync("wallet:alice:usd", "USD");

        var response = await http.PostTransferAsync("INR", (alice.Id, "debit", 100), (usd.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/currency-mismatch", (await response.ProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Another_clients_account_in_a_leg_is_not_found()
    {
        using var http = await api.NewClientAsync();
        using var other = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var eve = await other.CreateAccountAsync("wallet:eve");

        var response = await http.PostTransferAsync("INR", (alice.Id, "debit", 100), (eve.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (await other.BalanceAsync(eve.Id)).Posted);
    }

    [Fact]
    public async Task Duplicate_reference_is_a_conflict()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        Assert.Equal(HttpStatusCode.Created, (await http.PostTransferAsync("INR", "order-1", (alice.Id, "debit", 100), (bob.Id, "credit", 100))).StatusCode);
        var duplicate = await http.PostTransferAsync("INR", "order-1", (alice.Id, "debit", 100), (bob.Id, "credit", 100));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(900, (await http.BalanceAsync(alice.Id)).Posted);
    }

    [Fact]
    public async Task Entries_are_paged_newest_first_with_an_opaque_cursor()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        for (var i = 1; i <= 4; i++)
        {
            (await http.PostTransferAsync("INR", (alice.Id, "debit", i), (bob.Id, "credit", i))).EnsureSuccessStatusCode();
        }

        var first = await http.GetFromJsonAsync<EntryPage>($"/v1/accounts/{alice.Id}/entries?limit=3", ApiFixture.Json);
        Assert.Equal([5L, 4, 3], first!.Entries.Select(e => e.AccountSeq));
        Assert.NotNull(first.NextCursor);

        var second = await http.GetFromJsonAsync<EntryPage>($"/v1/accounts/{alice.Id}/entries?limit=3&after={first.NextCursor}", ApiFixture.Json);
        Assert.Equal([2L, 1], second!.Entries.Select(e => e.AccountSeq));
        Assert.Null(second.NextCursor);

        var bad = await http.GetAsync(new Uri($"/v1/accounts/{alice.Id}/entries?after=not-a-cursor", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
