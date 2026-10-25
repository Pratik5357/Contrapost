using System.Collections.Concurrent;
using System.Net;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>
/// A small version of the headline test (design doc §13.1): many concurrent writers on the
/// same few accounts, in both directions, then the invariants are recomputed from postings.
/// </summary>
[Collection("api")]
public sealed class ConcurrencyTests(ApiFixture api)
{
    private const int Wallets = 4;
    private const long StartingBalance = 10_000;
    private const int Workers = 32;
    private const int TransfersPerWorker = 25;

    [Fact]
    public async Task Concurrent_transfers_never_deadlock_overdraw_or_lose_money()
    {
        using var http = await api.NewClientAsync();
        var clientId = Guid.Parse(http.DefaultRequestHeaders.GetValues("X-Client-Id").Single());
        var wallets = new List<Account>();
        for (var i = 0; i < Wallets; i++)
        {
            wallets.Add(await http.FundedWalletAsync($"wallet:{i}", StartingBalance));
        }

        var outcomes = new ConcurrentBag<HttpStatusCode>();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker); // seeded so a failure can be replayed
            for (var i = 0; i < TransfersPerWorker; i++)
            {
                var from = wallets[random.Next(Wallets)];
                var to = wallets.Where(w => w != from).ElementAt(random.Next(Wallets - 1));
                var amount = random.Next(1, 3_000);
                var response = await http.PostTransferAsync("INR", (from.Id, "debit", amount), (to.Id, "credit", amount));
                outcomes.Add(response.StatusCode);
            }
        })));

        // Every request either posted or was cleanly refused for insufficient funds: no 500s, no deadlocks.
        Assert.All(outcomes, status => Assert.Contains(status, new[] { HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity }));
        Assert.Contains(HttpStatusCode.Created, outcomes);

        // No money created or destroyed between the wallets, and none overdrawn.
        var balances = await Task.WhenAll(wallets.Select(w => http.BalanceAsync(w.Id)));
        Assert.Equal(Wallets * StartingBalance, balances.Sum(b => b.Posted));
        Assert.All(balances, b => Assert.True(b.Posted >= 0));

        // I3: stored totals equal totals recomputed from postings, and account_seq is gapless.
        var drifted = await api.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM account_balances b
            JOIN accounts a ON a.id = b.account_id
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0)  AS debits,
                       coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0) AS credits,
                       count(*) AS entries,
                       coalesce(max(account_seq), 0) AS max_seq
                FROM postings p WHERE p.account_id = b.account_id
            ) t
            WHERE a.client_id = @client
              AND (b.debits_posted <> t.debits OR b.credits_posted <> t.credits
                   OR b.entry_seq <> t.entries OR t.max_seq <> t.entries)
            """,
            ("client", clientId));
        Assert.Equal(0, drifted);

        // I2: across the client's accounts, total debits equal total credits.
        var imbalance = await api.ScalarAsync<decimal>(
            """
            SELECT coalesce(sum(b.debits_posted) - sum(b.credits_posted), 0)
            FROM account_balances b JOIN accounts a ON a.id = b.account_id
            WHERE a.client_id = @client
            """,
            ("client", clientId));
        Assert.Equal(0m, imbalance);

        // Every posted transfer, and only those, produced a transaction and an outbox message.
        var posted = outcomes.Count(s => s == HttpStatusCode.Created) + Wallets; // + the funding transfers
        Assert.Equal(posted, await api.ScalarAsync<long>(
            "SELECT count(*) FROM transactions WHERE client_id = @client", ("client", clientId)));
        Assert.Equal(posted, await api.ScalarAsync<long>(
            "SELECT count(*) FROM outbox_messages o JOIN transactions t ON t.id = o.aggregate_id WHERE t.client_id = @client",
            ("client", clientId)));
    }
}
