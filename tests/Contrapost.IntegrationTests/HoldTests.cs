using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

[Collection("api")]
public sealed class HoldTests(ApiFixture api)
{
    [Fact]
    public async Task Hold_reserves_funds_without_moving_them()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");

        var response = await http.PostHoldAsync(alice.Id, shop.Id, 1_200);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = (await response.Content.ReadFromJsonAsync<Hold>(ApiFixture.Json))!;
        Assert.Equal($"/v1/holds/{hold.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("pending", 1_200L, 0L, 1_200L), (hold.Status, hold.Amount, hold.CapturedAmount, hold.Remaining));

        var aliceBalance = await http.BalanceAsync(alice.Id);
        Assert.Equal((5_000L, 3_800L, 1_200L), (aliceBalance.Posted, aliceBalance.Available, aliceBalance.DebitsPending));
        Assert.Equal(1_200, (await http.BalanceAsync(shop.Id)).CreditsPending);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM postings WHERE account_id = @id", ("id", shop.Id)));
        Assert.Equal(hold, await http.HoldAsync(hold.Id));
    }

    [Fact]
    public async Task Held_funds_cannot_be_spent_twice()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var bob = await http.CreateAccountAsync("wallet:bob");
        await http.CreateHoldAsync(alice.Id, shop.Id, 700);

        var secondHold = await http.PostHoldAsync(alice.Id, shop.Id, 400);
        var transfer = await http.PostTransferAsync("INR", (alice.Id, "debit", 400), (bob.Id, "credit", 400));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondHold.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transfer.StatusCode);
        Assert.Equal("Available balance 300 is less than the debit of 400.", (await transfer.ProblemAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Partial_captures_post_until_the_hold_is_fully_captured()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 1_000);

        var first = (await (await http.CaptureAsync(hold.Id, 300)).Content.ReadFromJsonAsync<Capture>(ApiFixture.Json))!;
        Assert.Equal(("partially_captured", 300L, 700L), (first.Hold.Status, first.Hold.CapturedAmount, first.Hold.Remaining));
        Assert.Equal("capture", first.Transaction.Kind);
        Assert.Equal(hold.Id, first.Transaction.HoldId);

        var rest = await http.CaptureAsync(hold.Id); // no amount: capture everything remaining
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        var second = (await rest.Content.ReadFromJsonAsync<Capture>(ApiFixture.Json))!;
        Assert.Equal(("captured", 1_000L, 0L), (second.Hold.Status, second.Hold.CapturedAmount, second.Hold.Remaining));

        var aliceBalance = await http.BalanceAsync(alice.Id);
        Assert.Equal((4_000L, 0L), (aliceBalance.Posted, aliceBalance.DebitsPending));
        var shopBalance = await http.BalanceAsync(shop.Id);
        Assert.Equal((1_000L, 0L), (shopBalance.Posted, shopBalance.CreditsPending));

        var capture = await http.GetFromJsonAsync<Transfer>($"/v1/transfers/{first.Transaction.Id}", ApiFixture.Json);
        Assert.Equal(("capture", hold.Id), (capture!.Kind, capture.HoldId));
    }

    [Fact]
    public async Task Capture_beyond_the_remaining_amount_is_rejected()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 1_000);
        (await http.CaptureAsync(hold.Id, 800)).EnsureSuccessStatusCode();

        var response = await http.CaptureAsync(hold.Id, 300);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/capture-exceeds-remaining", (await response.ProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Void_releases_the_remainder_and_closes_the_hold()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 1_000);
        (await http.CaptureAsync(hold.Id, 250)).EnsureSuccessStatusCode();

        var voided = await http.VoidAsync(hold.Id);

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var after = (await voided.Content.ReadFromJsonAsync<Hold>(ApiFixture.Json))!;
        Assert.Equal(("voided", 250L, 0L), (after.Status, after.CapturedAmount, after.Remaining));
        var aliceBalance = await http.BalanceAsync(alice.Id);
        Assert.Equal((4_750L, 0L, 4_750L), (aliceBalance.Posted, aliceBalance.DebitsPending, aliceBalance.Available));

        var late = await http.CaptureAsync(hold.Id, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Equal("https://contrapost.dev/errors/hold-closed", (await late.ProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Expired_hold_refuses_capture_and_the_expiry_job_releases_it()
    {
        using var client = await api.NewTestClientAsync();
        var http = client.Http;
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 1_000, expiresInSeconds: 1);
        (await http.CaptureAsync(hold.Id, 400)).EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // Before the job runs, capture already refuses: it checks expires_at itself.
        var late = await http.CaptureAsync(hold.Id, 100);
        Assert.Equal("https://contrapost.dev/errors/hold-expired", (await late.ProblemAsync()).GetProperty("type").GetString());
        Assert.Equal(600, (await http.BalanceAsync(alice.Id)).DebitsPending);

        Assert.True(await api.ExpireDueHoldsAsync() >= 1);

        var expired = await http.HoldAsync(hold.Id);
        Assert.Equal(("expired", 400L, 0L), (expired.Status, expired.CapturedAmount, expired.Remaining));
        var aliceBalance = await http.BalanceAsync(alice.Id);
        Assert.Equal((4_600L, 0L), (aliceBalance.Posted, aliceBalance.DebitsPending));
        Assert.Equal(1, await api.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages WHERE aggregate_id = @id AND type = 'hold.expired'", ("id", hold.Id)));

        // Running the job again finds nothing more for this hold.
        await api.ExpireDueHoldsAsync();
        Assert.Equal(0, (await http.BalanceAsync(alice.Id)).DebitsPending);
    }

    [Fact]
    public async Task Capture_retried_with_the_same_key_captures_once()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 1_000);

        var first = await http.CaptureAsync(hold.Id, 300, idempotencyKey: "cap-1");
        var retry = await http.CaptureAsync(hold.Id, 300, idempotencyKey: "cap-1");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        // The stored body is jsonb, so a replay is the same JSON value; key order and spacing may differ.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(await first.Content.ReadAsStringAsync()),
            JsonNode.Parse(await retry.Content.ReadAsStringAsync())));
        Assert.Equal(300, (await http.HoldAsync(hold.Id)).CapturedAmount);
    }

    [Fact]
    public async Task Another_clients_hold_is_not_found()
    {
        using var owner = await api.NewClientAsync();
        using var other = await api.NewClientAsync();
        var alice = await owner.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await owner.CreateAccountAsync("merchant:shop");
        var hold = await owner.CreateHoldAsync(alice.Id, shop.Id, 1_000);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(new Uri($"/v1/holds/{hold.Id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.CaptureAsync(hold.Id, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.VoidAsync(hold.Id)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_captures_never_capture_more_than_the_hold()
    {
        using var client = await api.NewTestClientAsync();
        var http = client.Http;
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var shop = await http.CreateAccountAsync("merchant:shop");
        var hold = await http.CreateHoldAsync(alice.Id, shop.Id, 500);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => http.CaptureAsync(hold.Id, 100)));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        var final = await http.HoldAsync(hold.Id);
        Assert.Equal(("captured", 500L), (final.Status, final.CapturedAmount));

        // I5: pending totals equal the remaining amounts of open holds; I6: captured equals capture postings.
        Assert.Equal(0, await api.ScalarAsync<long>(
            """
            SELECT count(*) FROM account_balances b JOIN accounts a ON a.id = b.account_id
            WHERE a.client_id = @c AND (
                b.debits_pending  <> (SELECT coalesce(sum(amount - captured_amount), 0) FROM holds
                                      WHERE debit_account_id = b.account_id AND status IN ('pending', 'partially_captured'))
             OR b.credits_pending <> (SELECT coalesce(sum(amount - captured_amount), 0) FROM holds
                                      WHERE credit_account_id = b.account_id AND status IN ('pending', 'partially_captured')))
            """, ("c", client.ClientId)));
        Assert.Equal(500, await api.ScalarAsync<decimal>(
            "SELECT sum(p.amount) FROM postings p JOIN transactions t ON t.id = p.transaction_id WHERE t.hold_id = @h AND p.direction = 'debit'",
            ("h", hold.Id)));
    }
}
