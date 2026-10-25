using Contrapost.Application.Transfers;
using Contrapost.Domain.Ledger;

namespace Contrapost.Infrastructure.Ledger;

/// <summary>
/// Converts between domain enums and the PostgreSQL enum labels in db/schema.sql.
/// Values cross the wire as text and are cast in SQL (e.g. <c>@direction::direction</c>).
/// </summary>
internal static class PgEnum
{
    public static string ToLabel(Direction value) => value == Direction.Debit ? "debit" : "credit";

    public static string ToLabel(NormalSide value) => value == NormalSide.Debit ? "debit" : "credit";

    public static string ToLabel(TransactionKind value) => value switch
    {
        TransactionKind.Transfer => "transfer",
        TransactionKind.Capture => "capture",
        TransactionKind.Reversal => "reversal",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static TransactionKind ToTransactionKind(string label) => label switch
    {
        "transfer" => TransactionKind.Transfer,
        "capture" => TransactionKind.Capture,
        "reversal" => TransactionKind.Reversal,
        _ => throw new FormatException($"Unknown transaction kind '{label}'."),
    };

    public static HoldStatus ToHoldStatus(string label) => label switch
    {
        "pending" => HoldStatus.Pending,
        "partially_captured" => HoldStatus.PartiallyCaptured,
        "captured" => HoldStatus.Captured,
        "voided" => HoldStatus.Voided,
        "expired" => HoldStatus.Expired,
        _ => throw new FormatException($"Unknown hold status '{label}'."),
    };

    public static Direction ToDirection(string label) => label switch
    {
        "debit" => Direction.Debit,
        "credit" => Direction.Credit,
        _ => throw new FormatException($"Unknown direction '{label}'."),
    };

    public static NormalSide ToNormalSide(string label) => label switch
    {
        "debit" => NormalSide.Debit,
        "credit" => NormalSide.Credit,
        _ => throw new FormatException($"Unknown normal side '{label}'."),
    };

    public static AccountStatus ToAccountStatus(string label) => label switch
    {
        "active" => AccountStatus.Active,
        "frozen" => AccountStatus.Frozen,
        "closed" => AccountStatus.Closed,
        _ => throw new FormatException($"Unknown account status '{label}'."),
    };

    public static OverdraftRule ToOverdraftRule(bool debitsMustNotExceedCredits, bool creditsMustNotExceedDebits) =>
        (debitsMustNotExceedCredits, creditsMustNotExceedDebits) switch
        {
            (true, _) => OverdraftRule.DebitsMustNotExceedCredits,
            (_, true) => OverdraftRule.CreditsMustNotExceedDebits,
            _ => OverdraftRule.None,
        };
}
