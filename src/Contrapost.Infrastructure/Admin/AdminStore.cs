using Contrapost.Application.Admin;
using Contrapost.Application.Holds;
using Contrapost.Application.Reconciliation;
using Contrapost.Infrastructure.Ledger;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Admin;

/// <summary>
/// The admin reads (design doc §5.2). Plain autocommit SELECTs: no transaction, no row locks, so the
/// dashboard can never hold up the write path, and every query could later point at a read replica.
/// Pages are keyset pages in id order (UUIDv7, so creation order); one extra row tells whether more follow.
/// </summary>
internal sealed class AdminStore(NpgsqlDataSource dataSource, IReconciliationStore reconciliation) : IAdminStore
{
    private const string OpenHoldStatuses = "('pending', 'partially_captured')";
    private const string OutboxColumns = "id, aggregate_id, type, created_at, next_attempt_at, published_at, attempts, last_error";

    private sealed record CurrencyRow(string Currency, int Accounts, long DebitsPosted, long CreditsPosted);

    private sealed class OutboxRow
    {
        public Guid Id { get; init; }
        public Guid AggregateId { get; init; }
        public string Type { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset NextAttemptAt { get; init; }
        public DateTimeOffset? PublishedAt { get; init; }
        public int Attempts { get; init; }
        public string? LastError { get; init; }

        public OutboxMessageView ToView() => new(Id, AggregateId, Type, CreatedAt, NextAttemptAt, PublishedAt, Attempts, LastError);
    }

    public async Task<OverviewView> OverviewAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var currencies = await connection.QueryAsync<CurrencyRow>(new CommandDefinition(
            """
            SELECT a.currency, count(*)::int AS accounts,
                   sum(b.debits_posted)::bigint AS debits_posted, sum(b.credits_posted)::bigint AS credits_posted
            FROM accounts a JOIN account_balances b ON b.account_id = a.id
            GROUP BY a.currency ORDER BY a.currency
            """,
            cancellationToken: cancellationToken));

        // The idempotency keys record every money-moving request's final status, except 400s and 5xx,
        // which aren't stored (so a retry can run again).
        var requests = await connection.QueryAsync<RequestCount>(new CommandDefinition(
            """
            SELECT CASE
                       WHEN path = '/v1/transfers' THEN 'transfer'
                       WHEN path LIKE '/v1/transfers/%/reversal' THEN 'reversal'
                       WHEN path = '/v1/holds' THEN 'hold'
                       WHEN path LIKE '/v1/holds/%/capture' THEN 'capture'
                       WHEN path LIKE '/v1/holds/%/void' THEN 'void'
                       ELSE 'other'
                   END AS operation,
                   response_code AS status, count(*)::int AS count
            FROM (SELECT rtrim(request_path, '/') AS path, response_code FROM idempotency_keys
                  WHERE created_at > now() - interval '1 hour' AND response_code IS NOT NULL) recent
            GROUP BY 1, 2 ORDER BY 1, 2
            """,
            cancellationToken: cancellationToken));

        var outbox = await connection.QuerySingleAsync<OutboxSummary>(new CommandDefinition(
            """
            SELECT count(*)::int AS unpublished,
                   (count(*) FILTER (WHERE attempts > 0))::int AS failing,
                   coalesce(extract(epoch FROM now() - min(created_at))::int, 0) AS oldest_unpublished_seconds
            FROM outbox_messages WHERE published_at IS NULL
            """,
            cancellationToken: cancellationToken));

        var latest = await reconciliation.ListRunsAsync(1, cancellationToken);

        return new OverviewView(
            currencies.Select(c => new CurrencyTotals(c.Currency, c.Accounts, c.DebitsPosted, c.CreditsPosted, c.DebitsPosted == c.CreditsPosted)).ToList(),
            requests.ToList(),
            outbox,
            latest.Count > 0 ? latest[0] : null);
    }

    public async Task<AdminPage<AdminAccountView>> SearchAccountsAsync(AccountSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AccountRow>(new CommandDefinition(
            $"""
            SELECT a.client_id, {AccountRow.Columns}
            FROM accounts a JOIN account_balances b ON b.account_id = a.id
            WHERE (@pattern::text IS NULL OR a.code ILIKE @pattern)
              AND (@clientId::uuid IS NULL OR a.client_id = @clientId)
              AND (@currency::text IS NULL OR a.currency = @currency)
              AND (@status::text IS NULL OR a.status = @status::account_status)
              AND (@after::uuid IS NULL OR a.id > @after)
            ORDER BY a.id LIMIT @fetch
            """,
            new
            {
                pattern = search.Query is { Length: > 0 } q ? $"%{EscapeLike(q)}%" : null,
                clientId = search.ClientId,
                currency = search.Currency,
                status = search.Status is { } status ? PgEnum.ToLabel(status) : null,
                after = search.After,
                fetch = search.Limit + 1,
            },
            cancellationToken: cancellationToken));

        return Page(rows.Select(r => new AdminAccountView(r.ClientId, r.ToView())).ToList(), search.Limit, a => a.Account.Id);
    }

    public Task<Guid?> AccountOwnerAsync(Guid accountId, CancellationToken cancellationToken) =>
        OwnerAsync("SELECT client_id FROM accounts WHERE id = @id", accountId, cancellationToken);

    public Task<Guid?> TransactionOwnerAsync(Guid transactionId, CancellationToken cancellationToken) =>
        OwnerAsync("SELECT client_id FROM transactions WHERE id = @id", transactionId, cancellationToken);

    public async Task<IReadOnlyList<HoldView>> OpenHoldsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<HoldRow>(new CommandDefinition(
            $"""
            SELECT {HoldRow.Columns} FROM holds
            WHERE (debit_account_id = @accountId OR credit_account_id = @accountId) AND status IN {OpenHoldStatuses}
            ORDER BY expires_at, id LIMIT 100
            """,
            new { accountId },
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToView()).ToList();
    }

    public Task<Guid?> ReversalOfAsync(Guid transactionId, CancellationToken cancellationToken) =>
        OwnerAsync("SELECT id FROM transactions WHERE reverses_transaction_id = @id", transactionId, cancellationToken);

    public async Task<IReadOnlyList<OutboxMessageView>> EventsForAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            $"SELECT {OutboxColumns} FROM outbox_messages WHERE aggregate_id = @aggregateId ORDER BY id",
            new { aggregateId },
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToView()).ToList();
    }

    public async Task<AdminPage<AdminHoldView>> SearchHoldsAsync(HoldSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<HoldRow>(new CommandDefinition(
            $"""
            SELECT client_id, {HoldRow.Columns} FROM holds
            WHERE (@status::text IS NULL OR status = @status::hold_status)
              AND (@expiringWithinSeconds::int IS NULL
                   OR (status IN {OpenHoldStatuses} AND expires_at <= now() + make_interval(secs => @expiringWithinSeconds)))
              AND (@after::uuid IS NULL OR id > @after)
            ORDER BY id LIMIT @fetch
            """,
            new
            {
                status = search.Status is { } status ? PgEnum.ToLabel(status) : null,
                expiringWithinSeconds = search.ExpiringWithin is { } within ? (int?)within.TotalSeconds : null,
                after = search.After,
                fetch = search.Limit + 1,
            },
            cancellationToken: cancellationToken));

        return Page(rows.Select(r => new AdminHoldView(r.ClientId, r.ToView())).ToList(), search.Limit, h => h.Hold.Id);
    }

    public async Task<AdminPage<OutboxMessageView>> ListOutboxAsync(OutboxState state, Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            $"""
            SELECT {OutboxColumns} FROM outbox_messages
            WHERE published_at IS NULL AND (NOT @failing OR attempts > 0)
              AND (@after::uuid IS NULL OR id > @after)
            ORDER BY id LIMIT @fetch
            """,
            new { failing = state == OutboxState.Failing, after, fetch = limit + 1 },
            cancellationToken: cancellationToken));

        return Page(rows.Select(r => r.ToView()).ToList(), limit, m => m.Id);
    }

    private async Task<Guid?> OwnerAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken));
    }

    private static AdminPage<T> Page<T>(List<T> rows, int limit, Func<T, Guid> id) =>
        rows.Count > limit
            ? new AdminPage<T>(rows.GetRange(0, limit), id(rows[limit - 1]))
            : new AdminPage<T>(rows, null);

    /// <summary>The search is a substring match, so the operator's text must not act as a pattern.</summary>
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
