// Locking-strategy benchmark (design doc §8.1): strategies A, B and C at several contention levels.
// Contention is the size of the account pool every transfer picks two accounts from: 2 means every
// transfer collides; 1000 means collisions are rare. Each cell gets its own freshly migrated database
// (so earlier cells' rows can't slow later ones) and a short unmeasured warm-up; afterwards,
// reconciliation must be clean.
//
//   dotnet run -c Release --project bench/Contrapost.Benchmarks -- [--workers 4] [--seconds 5] [--pools 2,10,100,1000] [--out docs/results]
//
// Defaults are light so it can run on a laptop alongside other work; raise --workers and --seconds on a
// dedicated machine. Run it on a quiet machine: other load skews every cell.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Contrapost.Application.Reconciliation;
using Contrapost.Application.Transfers;
using Contrapost.Benchmarks;
using Contrapost.Domain.Ledger;
using Contrapost.Infrastructure;
using Contrapost.Infrastructure.Ledger;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

var options = Options.Parse(args);
Console.WriteLine($"workers={options.Workers} seconds={options.Seconds} pools={string.Join(",", options.Pools)} strategies={string.Join(",", options.Strategies)}");

await using var postgres = new PostgreSqlBuilder("postgres:16").Build();
await postgres.StartAsync();

var results = new List<Cell>();
var cellNumber = 0;
foreach (var pool in options.Pools)
{
    foreach (var name in options.Strategies)
    {
        await using var services = await FreshDatabaseAsync(postgres.GetConnectionString(), $"bench_{++cellNumber}");
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var clientId = Guid.CreateVersion7();
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes) VALUES (@clientId, 'bench', 'cp_bench_1', '\\x00', '{}')",
                new { clientId });
        }

        var accounts = await CreateAccountsAsync(dataSource, clientId, pool);
        var strategy = LockingStrategy.Create(name, dataSource);
        await RunCellAsync(strategy, clientId, accounts, options with { Seconds = 2 }); // warm-up, not measured
        var cell = await RunCellAsync(strategy, clientId, accounts, options);

        await using var scope = services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IReconciliationStore>().RunAsync(CancellationToken.None);
        cell = cell with { Reconciliation = $"{run?.Status} ({run?.FindingsCount} findings)" };

        results.Add(cell);
        Console.WriteLine(cell.ToConsole());
        foreach (var error in cell.ErrorSamples)
        {
            Console.WriteLine($"    error: {error}");
        }
    }
}

Directory.CreateDirectory(options.Out);
await File.WriteAllTextAsync(Path.Combine(options.Out, "locking-benchmark.csv"), Report.Csv(results));
await File.WriteAllTextAsync(Path.Combine(options.Out, "locking-benchmark.svg"), Report.Svg(results, options));
Console.WriteLine();
Console.WriteLine(Report.MarkdownTable(results));
Console.WriteLine($"wrote {options.Out}/locking-benchmark.csv and .svg");

static async Task<ServiceProvider> FreshDatabaseAsync(string serverConnectionString, string database)
{
    await using (var server = new NpgsqlConnection(serverConnectionString))
    {
        await server.OpenAsync();
        await server.ExecuteAsync($"CREATE DATABASE {database}");
    }

    var connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
        .Build();
    var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider();
    await services.MigrateDatabaseAsync();
    return services;
}

static async Task<Guid[]> CreateAccountsAsync(NpgsqlDataSource dataSource, Guid clientId, int count)
{
    // No overdraft rule, so no transfer is ever refused: the cell measures concurrency control only.
    var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToArray();
    await using var connection = await dataSource.OpenConnectionAsync();
    await connection.ExecuteAsync(
        """
        INSERT INTO accounts (id, client_id, code, currency, normal_side)
        SELECT id, @clientId, 'bench:' || id, 'INR', 'credit' FROM unnest(@ids) AS id;
        INSERT INTO account_balances (account_id, debits_must_not_exceed_credits, credits_must_not_exceed_debits)
        SELECT id, false, false FROM unnest(@ids) AS id;
        """,
        new { clientId, ids });
    return ids;
}

static async Task<Cell> RunCellAsync(LockingStrategy strategy, Guid clientId, Guid[] accounts, Options options)
{
    var latencies = new ConcurrentBag<double>();
    var attempts = new ConcurrentBag<int>();
    var gaveUp = 0;
    var errors = new ConcurrentBag<string>();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
    var clock = Stopwatch.StartNew();

    await Task.WhenAll(Enumerable.Range(0, options.Workers).Select(worker => Task.Run(async () =>
    {
        var random = new Random(worker);
        while (!stop.IsCancellationRequested)
        {
            var from = accounts[random.Next(accounts.Length)];
            var to = accounts[random.Next(accounts.Length)];
            if (from == to)
            {
                continue;
            }

            var amount = random.Next(1, 1_000);
            var transfer = new NewTransfer(Guid.CreateVersion7(), clientId, "INR", null, null,
                [new TransferLeg(from, Direction.Debit, amount), new TransferLeg(to, Direction.Credit, amount)]);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var outcome = await strategy.PostAsync(transfer, CancellationToken.None);
                if (outcome.GaveUp)
                {
                    Interlocked.Increment(ref gaveUp);
                }
                else
                {
                    latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    attempts.Add(outcome.Attempts);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                errors.Add(ex.Message);
            }
        }
    })));
    clock.Stop();

    var sorted = latencies.Order().ToArray();
    double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
    return new Cell(
        strategy.Name,
        accounts.Length,
        sorted.Length,
        sorted.Length / clock.Elapsed.TotalSeconds,
        Percentile(0.5),
        Percentile(0.99),
        attempts.IsEmpty ? 0 : attempts.Average(),
        gaveUp,
        errors.Count,
        "")
    {
        ErrorSamples = [.. errors.Distinct().Take(3)],
    };
}

namespace Contrapost.Benchmarks
{
    internal sealed record Options(int Workers, int Seconds, int[] Pools, string[] Strategies, string Out)
    {
        public static Options Parse(string[] args)
        {
            string Value(string name, string fallback)
            {
                var index = Array.IndexOf(args, $"--{name}");
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }

            return new Options(
                int.Parse(Value("workers", "4"), CultureInfo.InvariantCulture),
                int.Parse(Value("seconds", "5"), CultureInfo.InvariantCulture),
                [.. Value("pools", "2,10,100,1000").Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture))],
                Value("strategies", "A,B,C").Split(','),
                Value("out", "docs/results"));
        }
    }

    internal sealed record Cell(
        string Strategy, int Pool, int Posted, double PerSecond, double P50Ms, double P99Ms,
        double AvgAttempts, int GaveUp, int Errors, string Reconciliation)
    {
        public IReadOnlyList<string> ErrorSamples { get; init; } = [];

        public string ToConsole() => string.Create(CultureInfo.InvariantCulture,
            $"{Strategy,-15} pool={Pool,5}  {PerSecond,7:F0}/s  p50={P50Ms,6:F1}ms  p99={P99Ms,7:F1}ms  attempts={AvgAttempts:F2}  gave_up={GaveUp}  errors={Errors}  {Reconciliation}");
    }
}
