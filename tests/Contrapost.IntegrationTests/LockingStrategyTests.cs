using Contrapost.Application.Transfers;
using Contrapost.Domain.Ledger;
using Contrapost.Infrastructure.Ledger;
using Npgsql;

namespace Contrapost.IntegrationTests;

/// <summary>
/// Every strategy from design doc §8.1 must keep the ledger correct under heavy contention; they may
/// differ in speed, never in correctness. The benchmark (bench/) measures the speed.
/// </summary>
[Collection("api")]
public sealed class LockingStrategyTests(ApiFixture api)
{
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public async Task Strategy_keeps_balances_consistent_with_postings_under_contention(string name)
    {
        using var client = await api.NewTestClientAsync();
        await using var dataSource = NpgsqlDataSource.Create(api.ConnectionString);
        var accounts = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        await api.ScalarAsync<int>(
            """
            WITH a AS (INSERT INTO accounts (id, client_id, code, currency, normal_side)
                       SELECT id, @c, 'strategy:' || id, 'INR', 'credit' FROM unnest(@ids) AS id RETURNING id)
            INSERT INTO account_balances (account_id, debits_must_not_exceed_credits, credits_must_not_exceed_debits)
            SELECT id, false, false FROM a RETURNING 1
            """,
            ("c", client.ClientId), ("ids", accounts));
        var strategy = LockingStrategy.Create(name, dataSource);

        // Three accounts and eight writers: nearly every transfer collides with another.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            var results = new List<StrategyOutcome>();
            for (var i = 0; i < 40; i++)
            {
                var from = accounts[random.Next(3)];
                var to = accounts[(Array.IndexOf(accounts, from) + 1 + random.Next(2)) % 3];
                results.Add(await strategy.PostAsync(
                    new NewTransfer(Guid.CreateVersion7(), client.ClientId, "INR", null, null,
                        [new TransferLeg(from, Direction.Debit, 10), new TransferLeg(to, Direction.Credit, 10)]),
                    CancellationToken.None));
            }

            return results;
        })));

        var all = outcomes.SelectMany(o => o).ToList();
        var posted = all.Count(o => o.Result?.IsSuccess == true);
        Assert.True(posted > 0);
        Assert.All(all.Where(o => !o.GaveUp), o => Assert.True(o.Result!.IsSuccess));

        // Exactly the posted transfers reached the ledger, and the balances match their postings.
        Assert.Equal(posted, await api.ScalarAsync<long>("SELECT count(*) FROM transactions WHERE client_id = @c", ("c", client.ClientId)));
        Assert.Equal(0, await api.ScalarAsync<long>(
            """
            SELECT count(*) FROM account_balances b
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0) AS d,
                       coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0) AS c,
                       count(*) AS n
                FROM postings p WHERE p.account_id = b.account_id) t
            WHERE b.account_id = ANY(@ids) AND (b.debits_posted <> t.d OR b.credits_posted <> t.c OR b.entry_seq <> t.n)
            """, ("ids", accounts)));
        Assert.Equal(0m, await api.ScalarAsync<decimal>(
            "SELECT sum(debits_posted) - sum(credits_posted) FROM account_balances WHERE account_id = ANY(@ids)", ("ids", accounts)));
    }
}
