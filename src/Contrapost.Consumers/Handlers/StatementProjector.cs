using Contrapost.Infrastructure.Messaging;
using Dapper;
using Npgsql;

namespace Contrapost.Consumers.Handlers;

/// <summary>
/// Builds the <c>account_statements</c> read model, one row per posting with the balance after it
/// (design doc §9.3). Events can arrive out of order, so each account's head only advances over a
/// gapless run of account_seq: a statement is complete through <c>complete_through_seq</c>.
/// </summary>
public sealed class StatementProjector : IEventConsumer
{
    public ConsumerBinding Binding => Topology.Statements;

    public async Task HandleAsync(EventEnvelope envelope, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var posted = PostedEvent.From(envelope);

        foreach (var leg in posted.Legs)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO account_statements (account_id, account_seq, transaction_id, direction, amount, balance_after, posted_at)
                VALUES (@AccountId, @AccountSeq, @transactionId, @Direction::direction, @Amount, @BalanceAfter, @postedAt)
                ON CONFLICT (account_id, account_seq) DO NOTHING;

                INSERT INTO account_statement_heads (account_id) VALUES (@AccountId)
                ON CONFLICT (account_id) DO NOTHING;

                -- Rows above the head form a gapless run while account_seq = head + row_number.
                UPDATE account_statement_heads h
                SET complete_through_seq = h.complete_through_seq + (
                        SELECT count(*)
                        FROM (SELECT s.account_seq, row_number() OVER (ORDER BY s.account_seq) AS rn
                              FROM account_statements s
                              WHERE s.account_id = h.account_id AND s.account_seq > h.complete_through_seq) run
                        WHERE run.account_seq = h.complete_through_seq + run.rn),
                    updated_at = now()
                WHERE h.account_id = @AccountId;
                """,
                new
                {
                    leg.AccountId,
                    leg.AccountSeq,
                    transactionId = posted.TransactionId,
                    leg.Direction,
                    leg.Amount,
                    leg.BalanceAfter,
                    postedAt = envelope.Time,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }
}
