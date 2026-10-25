using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>
/// The headline test (design doc §13.1): many workers doing random transfers, holds, captures, voids and
/// reversals on shared accounts, with overdraft attempts and retries that reuse idempotency keys, while the
/// expiry job runs. Then reconciliation must find nothing. CI runs a short version; the full run is
/// <c>CONTRAPOST_SOAK_SECONDS=300 CONTRAPOST_SOAK_WORKERS=64 CONTRAPOST_SOAK_WALLETS=1000 dotnet test --filter HeadlineConcurrency</c>.
/// </summary>
[Collection("api")]
public sealed class HeadlineConcurrencyTests(ApiFixture api, ITestOutputHelper output)
{
    private static readonly int Seconds = Setting("CONTRAPOST_SOAK_SECONDS", 15);
    private static readonly int Workers = Setting("CONTRAPOST_SOAK_WORKERS", 32);
    private static readonly int WalletCount = Setting("CONTRAPOST_SOAK_WALLETS", 100);
    private const int MerchantCount = 20;
    private const long StartingBalance = 100_000;

    private sealed record Sent(string Operation, string Path, string Key, object Body, HttpStatusCode Status, string Response);

    [Fact]
    public async Task Random_concurrent_operations_leave_a_ledger_that_reconciles_cleanly()
    {
        using var client = await api.NewTestClientAsync();
        var http = client.Http;
        var wallets = await SetUpAsync(http);
        var merchants = new List<Guid>();
        for (var i = 0; i < MerchantCount; i++)
        {
            merchants.Add((await http.CreateAccountAsync($"merchant:{i}")).Id);
        }

        var holds = new ConcurrentBag<Guid>();
        var transfers = new ConcurrentBag<Guid>();
        var history = new ConcurrentQueue<Sent>();
        var outcomes = new ConcurrentDictionary<string, int>();
        var unexpected = new ConcurrentQueue<string>();
        var replayMismatches = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(Seconds));

        var expiry = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await api.ExpireDueHoldsAsync();
                await Task.Delay(500, CancellationToken.None);
            }
        });

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            while (!stop.IsCancellationRequested)
            {
                var roll = random.Next(100);
                var wallet = wallets[random.Next(wallets.Count)];
                (string Op, string Path, object Body, HttpStatusCode[] Allowed)? request = roll switch
                {
                    < 45 => ("transfer", "/v1/transfers", TransferBody(wallet, random.Next(4) == 0 ? merchants[random.Next(MerchantCount)] : Other(wallets, wallet, random), random.Next(1, 30_000)),
                        [HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity]),
                    < 60 => ("hold", "/v1/holds", new
                    {
                        debit_account_id = wallet,
                        credit_account_id = merchants[random.Next(MerchantCount)],
                        currency = "INR",
                        amount = random.Next(1, 20_000),
                        expires_in_seconds = random.Next(1, 20),
                    }, [HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity]),
                    < 72 when Pick(holds, random) is { } hold => ("capture", $"/v1/holds/{hold}/capture",
                        new { amount = random.Next(3) == 0 ? (long?)null : random.Next(1, 10_000) },
                        [HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity]),
                    < 78 when Pick(holds, random) is { } hold => ("void", $"/v1/holds/{hold}/void", new { },
                        [HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity]),
                    < 85 when Pick(transfers, random) is { } transfer => ("reversal", $"/v1/transfers/{transfer}/reversal", new { },
                        [HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity]),
                    _ => null,
                };

                if (request is null)
                {
                    await ReplayAsync(http, history, random, replayMismatches, outcomes);
                    continue;
                }

                var (op, path, body, allowed) = request.Value;
                var key = Guid.NewGuid().ToString();
                using var response = await http.PostWithKeyAsync(path, key, body);
                var text = await response.Content.ReadAsStringAsync();
                outcomes.AddOrUpdate($"{op} {(int)response.StatusCode}", 1, (_, n) => n + 1);

                if (!allowed.Contains(response.StatusCode))
                {
                    unexpected.Enqueue($"{op} {path} -> {(int)response.StatusCode}: {text}");
                    continue;
                }

                history.Enqueue(new Sent(op, path, key, body, response.StatusCode, text));
                if (response.IsSuccessStatusCode && JsonNode.Parse(text) is { } json)
                {
                    if (op == "hold") holds.Add(json["id"]!.GetValue<Guid>());
                    if (op == "transfer") transfers.Add(json["id"]!.GetValue<Guid>());
                }
            }
        })));
        clock.Stop();
        await expiry;

        // Let every hold reach its end, then reconcile the whole database.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await api.ExpireDueHoldsAsync();
        var run = await api.ReconcileAsync();

        var total = outcomes.Values.Sum();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Workers} workers, {WalletCount} wallets, {Seconds}s: {total} requests ({total / clock.Elapsed.TotalSeconds:F0}/s)"));
        foreach (var (outcome, count) in outcomes.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {outcome,-16} {count}");
        }

        output.WriteLine($"reconciliation: {run?.Status}, {run?.FindingsCount} findings, {run?.AccountsChecked} accounts");

        foreach (var mismatch in replayMismatches.Take(5))
        {
            output.WriteLine($"MISMATCH {mismatch}");
        }

        Assert.Empty(unexpected);
        Assert.Empty(replayMismatches);
        Assert.True(outcomes.Keys.Any(k => k.StartsWith("replay", StringComparison.Ordinal)), "no replays happened");
        Assert.Equal(("clean", 0), (run!.Status, run.FindingsCount));
        Assert.Equal(0m, await api.ScalarAsync<decimal>(
            """
            SELECT sum(b.debits_posted) - sum(b.credits_posted) FROM account_balances b JOIN accounts a ON a.id = b.account_id
            WHERE a.client_id = @c
            """, ("c", client.ClientId)));
    }

    /// <summary>Resends an earlier request with its idempotency key; it must replay the original response exactly.</summary>
    private static async Task ReplayAsync(HttpClient http, ConcurrentQueue<Sent> history, Random random,
        ConcurrentQueue<string> mismatches, ConcurrentDictionary<string, int> outcomes)
    {
        var sent = history.Skip(random.Next(Math.Max(1, history.Count))).FirstOrDefault();
        if (sent is null)
        {
            return;
        }

        using var replay = await http.PostWithKeyAsync(sent.Path, sent.Key, sent.Body);
        var text = await replay.Content.ReadAsStringAsync();
        outcomes.AddOrUpdate($"replay {(int)replay.StatusCode}", 1, (_, n) => n + 1);

        var replayed = replay.Headers.TryGetValues("Idempotent-Replayed", out var values) && values.Single() == "true";
        if (replay.StatusCode != sent.Status || !replayed || !JsonNode.DeepEquals(JsonNode.Parse(sent.Response), JsonNode.Parse(text)))
        {
            mismatches.Enqueue($"{sent.Operation} {sent.Path} key {sent.Key}: first {(int)sent.Status} {sent.Response}, replay {(int)replay.StatusCode} {text} (replayed header: {replayed})");
        }
    }

    private static async Task<List<Guid>> SetUpAsync(HttpClient http)
    {
        var funding = await http.CreateAccountAsync("funding:INR", normalSide: "debit", preventOverdraft: false);
        var wallets = new ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(Enumerable.Range(0, WalletCount), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, _) =>
        {
            var wallet = await http.CreateAccountAsync($"wallet:{i}");
            (await http.PostTransferAsync("INR", (funding.Id, "debit", StartingBalance), (wallet.Id, "credit", StartingBalance))).EnsureSuccessStatusCode();
            wallets.Add(wallet.Id);
        });
        return [.. wallets];
    }

    private static object TransferBody(Guid from, Guid to, long amount) => new
    {
        currency = "INR",
        legs = new object[]
        {
            new { account_id = from, direction = "debit", amount },
            new { account_id = to, direction = "credit", amount },
        },
    };

    private static Guid Other(List<Guid> wallets, Guid not, Random random)
    {
        while (true)
        {
            var candidate = wallets[random.Next(wallets.Count)];
            if (candidate != not)
            {
                return candidate;
            }
        }
    }

    private static Guid? Pick(ConcurrentBag<Guid> bag, Random random)
    {
        var count = bag.Count;
        return count == 0 ? null : bag.Skip(random.Next(count)).FirstOrDefault();
    }

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
