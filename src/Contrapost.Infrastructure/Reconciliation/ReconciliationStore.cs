using System.Data;
using System.Text.Json;
using Contrapost.Application.Reconciliation;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Reconciliation;

internal sealed record ReconciliationSettings(int ChunkSize, int MaxFindingsPerCheck, TimeSpan EventWindow);

/// <summary>
/// Checks invariants I1–I8 (design doc §8.3). Reconciliation never takes row locks: every check runs in
/// a READ ONLY, REPEATABLE READ snapshot. The per-account checks (I3–I5) scan in account-id chunks, each
/// in its own short snapshot; that is consistent because an account's balances, postings and holds always
/// change together in one transaction, and short snapshots don't hold back vacuum on a large postings
/// table. The global checks are aggregate queries, each in a single snapshot.
/// </summary>
internal sealed class ReconciliationStore(NpgsqlDataSource dataSource, ReconciliationSettings settings) : IReconciliationStore
{
    private const long LockKey = 0x636f6e7472610001; // "contra" + 1: pg advisory lock for one run at a time

    private sealed record Finding(string CheckName, Guid? SubjectId, string? Expected, string? Actual);

    public async Task<ReconciliationRunView?> RunAsync(CancellationToken cancellationToken)
    {
        // A session-level advisory lock held for the whole run, so several Workers never reconcile at once.
        await using var lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await lockConnection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@LockKey)", new { LockKey }))
        {
            return null;
        }

        try
        {
            var runId = Guid.CreateVersion7();
            await lockConnection.ExecuteAsync(
                "INSERT INTO reconciliation_runs (id, started_at, status) VALUES (@runId, now(), 'running')", new { runId });

            try
            {
                var (findings, accountsChecked) = await CheckEverythingAsync(cancellationToken);
                await RecordAsync(lockConnection, runId, findings, accountsChecked, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await lockConnection.ExecuteAsync(
                    "UPDATE reconciliation_runs SET status = 'failed', finished_at = now() WHERE id = @runId", new { runId });
                throw;
            }

            return (await ListAsync(lockConnection, runId, 1, cancellationToken)).Single();
        }
        finally
        {
            await lockConnection.ExecuteAsync("SELECT pg_advisory_unlock(@LockKey)", new { LockKey });
        }
    }

    public async Task<IReadOnlyList<ReconciliationRunView>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ListAsync(connection, null, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<FindingView>?> ListFindingsAsync(Guid runId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM reconciliation_runs WHERE id = @runId)", new { runId }, cancellationToken: cancellationToken)))
        {
            return null;
        }

        var rows = await connection.QueryAsync<FindingRow>(new CommandDefinition(
            """
            SELECT id, check_name, subject_id, expected::text AS expected, actual::text AS actual
            FROM reconciliation_findings WHERE run_id = @runId ORDER BY id LIMIT @limit
            """,
            new { runId, limit },
            cancellationToken: cancellationToken));
        return rows.Select(r => new FindingView(r.Id, r.CheckName, r.SubjectId, Parse(r.Expected), Parse(r.Actual))).ToList();
    }

    private async Task<(List<Finding> Findings, int AccountsChecked)> CheckEverythingAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();

        foreach (var (name, sql) in GlobalChecks)
        {
            await using var snapshot = await SnapshotAsync(cancellationToken);
            findings.AddRange(await QueryAsync(snapshot, name, sql, new { ids = Array.Empty<Guid>() }, cancellationToken));
        }

        // Per-account checks, chunk by chunk in account-id order.
        var accountsChecked = 0;
        var after = Guid.Empty;
        while (true)
        {
            await using var snapshot = await SnapshotAsync(cancellationToken);
            var ids = (await snapshot.Connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT account_id FROM account_balances WHERE account_id > @after ORDER BY account_id LIMIT @chunk",
                new { after, chunk = settings.ChunkSize },
                snapshot.Transaction,
                cancellationToken: cancellationToken))).ToArray();
            if (ids.Length == 0)
            {
                break;
            }

            foreach (var (name, sql) in AccountChecks)
            {
                findings.AddRange(await QueryAsync(snapshot, name, sql, new { ids }, cancellationToken));
            }

            accountsChecked += ids.Length;
            after = ids[^1];
        }

        return (findings, accountsChecked);
    }

    private async Task<IEnumerable<Finding>> QueryAsync(Snapshot snapshot, string name, string sql, object parameters, CancellationToken cancellationToken)
    {
        var args = new DynamicParameters(parameters);
        args.Add("limit", settings.MaxFindingsPerCheck);
        args.Add("since", DateTimeOffset.UtcNow - settings.EventWindow);
        var rows = await snapshot.Connection.QueryAsync<FindingRow>(new CommandDefinition(sql, args, snapshot.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => new Finding(name, r.SubjectId, r.Expected, r.Actual));
    }

    private static async Task RecordAsync(NpgsqlConnection connection, Guid runId, List<Finding> findings, int accountsChecked, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reconciliation_findings (run_id, check_name, subject_id, expected, actual)
            SELECT @runId, f.check_name, f.subject_id, f.expected::jsonb, f.actual::jsonb
            FROM unnest(@checks, @subjects, @expected, @actual) AS f(check_name, subject_id, expected, actual)
            """,
            new
            {
                runId,
                checks = findings.Select(f => f.CheckName).ToArray(),
                subjects = findings.Select(f => f.SubjectId).ToArray(),
                expected = findings.Select(f => f.Expected).ToArray(),
                actual = findings.Select(f => f.Actual).ToArray(),
            },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE reconciliation_runs
            SET finished_at = now(), accounts_checked = @accountsChecked, findings_count = @count,
                status = CASE WHEN @count = 0 THEN 'clean' ELSE 'drift_found' END
            WHERE id = @runId
            """,
            new { runId, accountsChecked, count = findings.Count },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ReconciliationRunView>> ListAsync(NpgsqlConnection connection, Guid? runId, int limit, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<RunRow>(new CommandDefinition(
            """
            SELECT id, status, started_at, finished_at, accounts_checked, findings_count
            FROM reconciliation_runs
            WHERE @runId::uuid IS NULL OR id = @runId
            ORDER BY started_at DESC
            LIMIT @limit
            """,
            new { runId, limit },
            cancellationToken: cancellationToken)))
        .Select(r => new ReconciliationRunView(r.Id, r.Status, r.StartedAt, r.FinishedAt, r.AccountsChecked, r.FindingsCount))
        .ToList();

    private async Task<Snapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);
        return new Snapshot(connection, transaction);
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private sealed record Snapshot(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public int? AccountsChecked { get; init; }
        public int? FindingsCount { get; init; }
    }

    private sealed class FindingRow
    {
        public long Id { get; init; }
        public string CheckName { get; init; } = "";
        public Guid? SubjectId { get; init; }
        public string? Expected { get; init; }
        public string? Actual { get; init; }
    }

    // Each check returns (subject_id, expected, actual) for the rows that break the invariant.
    private static readonly (string Name, string Sql)[] GlobalChecks =
    [
        ("I1_unbalanced_transaction", """
            SELECT t.id AS subject_id,
                   jsonb_build_object('min_postings', 2, 'debits', coalesce(p.credits, 0))::text AS expected,
                   jsonb_build_object('postings', coalesce(p.n, 0), 'debits', coalesce(p.debits, 0), 'credits', coalesce(p.credits, 0))::text AS actual
            FROM transactions t
            LEFT JOIN (SELECT transaction_id, count(*) AS n,
                              sum(amount) FILTER (WHERE direction = 'debit') AS debits,
                              sum(amount) FILTER (WHERE direction = 'credit') AS credits
                       FROM postings GROUP BY transaction_id) p ON p.transaction_id = t.id
            WHERE coalesce(p.n, 0) < 2 OR coalesce(p.debits, 0) <> coalesce(p.credits, 0)
            LIMIT @limit
            """),
        ("I2_currency_not_zero_sum", """
            SELECT NULL::uuid AS subject_id,
                   jsonb_build_object('currency', a.currency, 'debits_posted', sum(b.credits_posted), 'debits_pending', sum(b.credits_pending))::text AS expected,
                   jsonb_build_object('currency', a.currency, 'debits_posted', sum(b.debits_posted), 'debits_pending', sum(b.debits_pending))::text AS actual
            FROM account_balances b JOIN accounts a ON a.id = b.account_id
            GROUP BY a.currency
            HAVING sum(b.debits_posted) <> sum(b.credits_posted) OR sum(b.debits_pending) <> sum(b.credits_pending)
            LIMIT @limit
            """),
        ("I6_hold_capture_mismatch", """
            SELECT h.id AS subject_id,
                   jsonb_build_object('captured_amount', coalesce(c.captured, 0))::text AS expected,
                   jsonb_build_object('captured_amount', h.captured_amount)::text AS actual
            FROM holds h
            LEFT JOIN (SELECT t.hold_id, sum(p.amount) AS captured
                       FROM transactions t JOIN postings p ON p.transaction_id = t.id AND p.direction = 'debit'
                       WHERE t.kind = 'capture' GROUP BY t.hold_id) c ON c.hold_id = h.id
            WHERE h.captured_amount <> coalesce(c.captured, 0)
            LIMIT @limit
            """),
        ("I7_transaction_event_count", """
            SELECT t.id AS subject_id,
                   jsonb_build_object('type', o.expected_type, 'count', 1)::text AS expected,
                   jsonb_build_object('type', o.expected_type, 'count', o.n)::text AS actual
            FROM transactions t
            CROSS JOIN LATERAL (
                SELECT e.expected_type, (SELECT count(*) FROM outbox_messages m WHERE m.aggregate_id = t.id AND m.type = e.expected_type) AS n
                FROM (SELECT CASE t.kind WHEN 'transfer' THEN 'transfer.posted' WHEN 'capture' THEN 'hold.captured'
                                         ELSE 'transfer.reversed' END AS expected_type) e) o
            WHERE t.created_at >= @since AND o.n <> 1
            LIMIT @limit
            """),
        ("I7_hold_event_count", """
            SELECT h.id AS subject_id,
                   jsonb_build_object('hold.created', 1, 'terminal', o.terminal_type, 'terminal_count', CASE WHEN o.terminal_type IS NULL THEN 0 ELSE 1 END)::text AS expected,
                   jsonb_build_object('hold.created', o.created_n, 'terminal', o.terminal_type, 'terminal_count', o.terminal_n)::text AS actual
            FROM holds h
            CROSS JOIN LATERAL (
                SELECT t.terminal_type,
                       count(*) FILTER (WHERE m.type = 'hold.created') AS created_n,
                       count(*) FILTER (WHERE m.type = t.terminal_type) AS terminal_n
                FROM (SELECT CASE h.status WHEN 'voided' THEN 'hold.voided' WHEN 'expired' THEN 'hold.expired' END AS terminal_type) t
                LEFT JOIN outbox_messages m ON m.aggregate_id = h.id
                GROUP BY t.terminal_type) o
            WHERE h.created_at >= @since
              AND (o.created_n <> 1 OR o.terminal_n <> CASE WHEN o.terminal_type IS NULL THEN 0 ELSE 1 END)
            LIMIT @limit
            """),
        ("I8_reversed_more_than_once", """
            SELECT reverses_transaction_id AS subject_id,
                   '{"reversals": 1}' AS expected,
                   jsonb_build_object('reversals', count(*))::text AS actual
            FROM transactions WHERE reverses_transaction_id IS NOT NULL
            GROUP BY reverses_transaction_id HAVING count(*) > 1
            LIMIT @limit
            """),
    ];

    private static readonly (string Name, string Sql)[] AccountChecks =
    [
        ("I3_balance_drift", """
            SELECT b.account_id AS subject_id,
                   jsonb_build_object('debits_posted', t.debits, 'credits_posted', t.credits, 'entry_seq', t.entries)::text AS expected,
                   jsonb_build_object('debits_posted', b.debits_posted, 'credits_posted', b.credits_posted,
                                      'entry_seq', b.entry_seq, 'max_account_seq', t.max_seq)::text AS actual
            FROM account_balances b
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0) AS debits,
                       coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0) AS credits,
                       count(*) AS entries, coalesce(max(account_seq), 0) AS max_seq
                FROM postings p WHERE p.account_id = b.account_id) t
            WHERE b.account_id = ANY(@ids)
              AND (b.debits_posted <> t.debits OR b.credits_posted <> t.credits OR b.entry_seq <> t.entries OR t.max_seq <> t.entries)
            LIMIT @limit
            """),
        ("I4_overdraft_rule_broken", """
            SELECT account_id AS subject_id,
                   jsonb_build_object('rule', CASE WHEN debits_must_not_exceed_credits THEN 'debits_must_not_exceed_credits'
                                                   ELSE 'credits_must_not_exceed_debits' END)::text AS expected,
                   jsonb_build_object('debits_posted', debits_posted, 'credits_posted', credits_posted,
                                      'debits_pending', debits_pending, 'credits_pending', credits_pending)::text AS actual
            FROM account_balances
            WHERE account_id = ANY(@ids)
              AND ((debits_must_not_exceed_credits AND debits_posted + debits_pending > credits_posted)
                OR (credits_must_not_exceed_debits AND credits_posted + credits_pending > debits_posted))
            LIMIT @limit
            """),
        ("I5_pending_drift", """
            SELECT b.account_id AS subject_id,
                   jsonb_build_object('debits_pending', d.pending, 'credits_pending', c.pending)::text AS expected,
                   jsonb_build_object('debits_pending', b.debits_pending, 'credits_pending', b.credits_pending)::text AS actual
            FROM account_balances b
            CROSS JOIN LATERAL (SELECT coalesce(sum(amount - captured_amount), 0) AS pending FROM holds
                                WHERE debit_account_id = b.account_id AND status IN ('pending', 'partially_captured')) d
            CROSS JOIN LATERAL (SELECT coalesce(sum(amount - captured_amount), 0) AS pending FROM holds
                                WHERE credit_account_id = b.account_id AND status IN ('pending', 'partially_captured')) c
            WHERE b.account_id = ANY(@ids) AND (b.debits_pending <> d.pending OR b.credits_pending <> c.pending)
            LIMIT @limit
            """),
    ];
}
