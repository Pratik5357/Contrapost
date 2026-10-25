using System.Net;
using System.Net.Http.Json;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

[Collection("api")]
public sealed class ReversalTests(ApiFixture api)
{
    private static async Task<Transfer> PayAsync(HttpClient http, Guid from, Guid to, long amount)
    {
        var response = await http.PostTransferAsync("INR", (from, "debit", amount), (to, "credit", amount));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
    }

    [Fact]
    public async Task Reversal_posts_the_swapped_legs_and_restores_balances()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);

        var response = await http.ReverseAsync(payment.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reversal = (await response.Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
        Assert.Equal($"/v1/transfers/{reversal.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("reversal", payment.Id), (reversal.Kind, reversal.ReversesTransactionId));
        Assert.Equal("credit", reversal.Legs.Single(l => l.AccountId == alice.Id).Direction);
        Assert.Equal(1_000, (await http.BalanceAsync(alice.Id)).Posted);
        Assert.Equal(0, (await http.BalanceAsync(bob.Id)).Posted);

        var fetched = await http.GetFromJsonAsync<Transfer>($"/v1/transfers/{reversal.Id}", ApiFixture.Json);
        Assert.Equal(reversal.Legs, fetched!.Legs);
    }

    [Fact]
    public async Task A_transaction_is_reversed_at_most_once()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);
        (await http.ReverseAsync(payment.Id)).EnsureSuccessStatusCode();

        var again = await http.ReverseAsync(payment.Id);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/already-reversed", (await again.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(1_000, (await http.BalanceAsync(alice.Id)).Posted);
    }

    [Fact]
    public async Task Concurrent_reversals_of_one_transaction_post_once()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.ReverseAsync(payment.Id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1_000, (await http.BalanceAsync(alice.Id)).Posted);
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);
        var reversal = (await (await http.ReverseAsync(payment.Id)).Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;

        var response = await http.ReverseAsync(reversal.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/not-reversible", (await response.ProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Reversal_obeys_overdraft_rules_when_the_money_is_already_spent()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var carol = await http.CreateAccountAsync("wallet:carol");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);
        await PayAsync(http, bob.Id, carol.Id, 300);

        var response = await http.ReverseAsync(payment.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/insufficient-funds", (await response.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(100, (await http.BalanceAsync(bob.Id)).Posted);
    }

    [Fact]
    public async Task Reversal_is_allowed_on_a_frozen_account()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);
        await api.ScalarAsync<int>("UPDATE accounts SET status = 'frozen' WHERE id = @id RETURNING 1", ("id", bob.Id));

        var blocked = await http.PostTransferAsync("INR", (alice.Id, "debit", 1), (bob.Id, "credit", 1));
        var reversal = await http.ReverseAsync(payment.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reversal.StatusCode);
        Assert.Equal(0, (await http.BalanceAsync(bob.Id)).Posted);
    }

    [Fact]
    public async Task Reversing_a_capture_returns_the_money_but_leaves_the_hold_captured()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 600);
        var capture = (await (await http.CaptureAsync(hold.Id)).Content.ReadFromJsonAsync<Capture>(ApiFixture.Json))!;

        var response = await http.ReverseAsync(capture.Transaction.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1_000, (await http.BalanceAsync(alice.Id)).Posted);
        Assert.Equal("captured", (await http.HoldAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task Replayed_reversal_points_at_the_reversal_transaction()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(http, alice.Id, bob.Id, 400);

        var first = await http.ReverseAsync(payment.Id, idempotencyKey: "undo-1");
        var retry = await http.ReverseAsync(payment.Id, idempotencyKey: "undo-1");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.StartsWith("/v1/transfers/", retry.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_or_foreign_transaction_is_not_found()
    {
        using var owner = await api.NewClientAsync();
        using var other = await api.NewClientAsync();
        var alice = await owner.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await owner.CreateAccountAsync("wallet:bob");
        var payment = await PayAsync(owner, alice.Id, bob.Id, 400);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.ReverseAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.ReverseAsync(payment.Id)).StatusCode);
    }
}
