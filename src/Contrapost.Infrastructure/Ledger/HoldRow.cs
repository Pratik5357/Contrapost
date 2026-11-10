using Contrapost.Application.Holds;
using Contrapost.Domain.Ledger;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>A holds row. Dapper maps snake_case columns.</summary>
internal sealed class HoldRow
{
    public const string Columns = """
        id, debit_account_id, credit_account_id, currency, amount, captured_amount,
        status::text AS status, expires_at, created_at, now() AS db_now
        """;

    /// <summary>Only set by the admin reads, which select <c>client_id</c> as well.</summary>
    public Guid ClientId { get; init; }
    public Guid Id { get; init; }
    public Guid DebitAccountId { get; init; }
    public Guid CreditAccountId { get; init; }
    public string Currency { get; init; } = "";
    public long Amount { get; init; }
    public long CapturedAmount { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset DbNow { get; init; }

    public HoldState ToState() => new(Id, DebitAccountId, CreditAccountId, Currency, Amount, CapturedAmount,
        PgEnum.ToHoldStatus(Status), ExpiresAt);

    public HoldView ToView()
    {
        var state = ToState();
        return new HoldView(Id, state.Status, DebitAccountId, CreditAccountId, Currency, Amount, CapturedAmount,
            state.Remaining, ExpiresAt, CreatedAt);
    }
}
