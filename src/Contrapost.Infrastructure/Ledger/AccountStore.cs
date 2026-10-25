using System.Text.Json;
using Contrapost.Application.Accounts;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;
using Dapper;
using Npgsql;

namespace Contrapost.Infrastructure.Ledger;

internal sealed class AccountStore(NpgsqlDataSource dataSource) : IAccountStore
{
    public async Task<Result<AccountView>> CreateAsync(NewAccount account, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var flags = new
        {
            debits = account.OverdraftRule == OverdraftRule.DebitsMustNotExceedCredits,
            credits = account.OverdraftRule == OverdraftRule.CreditsMustNotExceedDebits,
        };

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO accounts (id, client_id, code, currency, normal_side,
                                      debits_must_not_exceed_credits, credits_must_not_exceed_debits)
                VALUES (@Id, @ClientId, @Code, @Currency, @normalSide::normal_side, @debits, @credits);

                INSERT INTO account_balances (account_id, debits_must_not_exceed_credits, credits_must_not_exceed_debits)
                VALUES (@Id, @debits, @credits);

                INSERT INTO outbox_messages (id, aggregate_id, type, payload)
                VALUES (@outboxId, @Id, 'account.created', @payload::jsonb);
                """,
                new
                {
                    account.Id,
                    account.ClientId,
                    account.Code,
                    account.Currency,
                    normalSide = PgEnum.ToLabel(account.NormalSide),
                    flags.debits,
                    flags.credits,
                    outboxId = Guid.CreateVersion7(),
                    payload = JsonSerializer.Serialize(new
                    {
                        account_id = account.Id,
                        code = account.Code,
                        currency = account.Currency,
                        normal_side = PgEnum.ToLabel(account.NormalSide),
                    }),
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "accounts_client_id_code_key" })
        {
            return new LedgerError(ErrorKind.Conflict, "account-code-taken", "Account code already exists",
                $"An account with code '{account.Code}' already exists.");
        }

        var created = await QueryAccountAsync(connection, transaction, account.ClientId, account.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created!.ToView();
    }

    public async Task<AccountView?> GetAsync(Guid clientId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await QueryAccountAsync(connection, null, clientId, accountId, cancellationToken);
        return row?.ToView();
    }

    public async Task<EntryPage?> ListEntriesAsync(Guid clientId, Guid accountId, long? beforeSeq, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM accounts WHERE id = @accountId AND client_id = @clientId)",
            new { accountId, clientId },
            cancellationToken: cancellationToken));
        if (!exists)
        {
            return null;
        }

        // Keyset paging on the (account_id, account_seq) unique index, newest first.
        // One extra row tells us whether another page exists.
        var rows = (await connection.QueryAsync<EntryRow>(new CommandDefinition(
            """
            SELECT transaction_id, account_seq, direction::text AS direction, amount, created_at
            FROM postings
            WHERE account_id = @accountId AND client_id = @clientId
              AND account_seq < coalesce(@beforeSeq, 9223372036854775807)
            ORDER BY account_seq DESC
            LIMIT @take
            """,
            new { accountId, clientId, beforeSeq, take = limit + 1 },
            cancellationToken: cancellationToken))).ToList();

        var page = rows.Take(limit)
            .Select(r => new EntryView(r.TransactionId, r.AccountSeq, PgEnum.ToDirection(r.Direction), r.Amount, r.CreatedAt))
            .ToList();
        return new EntryPage(page, rows.Count > limit ? page[^1].AccountSeq : null);
    }

    private static Task<AccountRow?> QueryAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid clientId, Guid accountId, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"""
            SELECT {AccountRow.Columns}
            FROM accounts a
            JOIN account_balances b ON b.account_id = a.id
            WHERE a.id = @accountId AND a.client_id = @clientId
            """,
            new { accountId, clientId },
            transaction,
            cancellationToken: cancellationToken));

    private sealed class EntryRow
    {
        public Guid TransactionId { get; init; }
        public long AccountSeq { get; init; }
        public string Direction { get; init; } = "";
        public long Amount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
