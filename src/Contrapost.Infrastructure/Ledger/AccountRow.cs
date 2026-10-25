using Contrapost.Application.Accounts;
using Contrapost.Domain.Ledger;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>An accounts row joined to its account_balances row. Dapper maps snake_case columns.</summary>
internal sealed class AccountRow
{
    public const string Columns = """
        a.id, a.code, a.currency, a.normal_side::text AS normal_side, a.status::text AS status, a.created_at,
        b.debits_must_not_exceed_credits, b.credits_must_not_exceed_debits,
        b.debits_posted, b.credits_posted, b.debits_pending, b.credits_pending, b.entry_seq, b.version
        """;

    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Currency { get; init; } = "";
    public string NormalSide { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public bool DebitsMustNotExceedCredits { get; init; }
    public bool CreditsMustNotExceedDebits { get; init; }
    public long DebitsPosted { get; init; }
    public long CreditsPosted { get; init; }
    public long DebitsPending { get; init; }
    public long CreditsPending { get; init; }
    public long EntrySeq { get; init; }
    public long Version { get; init; }

    public Balance Balance => new(DebitsPosted, CreditsPosted, DebitsPending, CreditsPending);

    public LockedAccount ToLocked() => new(
        Id,
        Currency,
        PgEnum.ToNormalSide(NormalSide),
        PgEnum.ToAccountStatus(Status),
        PgEnum.ToOverdraftRule(DebitsMustNotExceedCredits, CreditsMustNotExceedDebits),
        Balance);

    public AccountView ToView()
    {
        var side = PgEnum.ToNormalSide(NormalSide);
        return new AccountView(
            Id,
            Code,
            Currency,
            side,
            DebitsMustNotExceedCredits || CreditsMustNotExceedDebits,
            PgEnum.ToAccountStatus(Status),
            CreatedAt,
            BalanceView.From(Balance, side));
    }
}
