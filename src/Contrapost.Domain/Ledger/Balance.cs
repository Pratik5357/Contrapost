namespace Contrapost.Domain.Ledger;

/// <summary>The four running totals kept for every account (design doc §4.3).</summary>
public readonly record struct Balance(long DebitsPosted, long CreditsPosted, long DebitsPending, long CreditsPending)
{
    public Balance Post(Direction direction, long amount) => direction == Direction.Debit
        ? this with { DebitsPosted = checked(DebitsPosted + amount) }
        : this with { CreditsPosted = checked(CreditsPosted + amount) };

    public long Posted(NormalSide side) => side == NormalSide.Credit
        ? CreditsPosted - DebitsPosted
        : DebitsPosted - CreditsPosted;

    /// <summary>Posted balance minus pending amounts that would reduce it; overdraft checks use this.</summary>
    public long Available(NormalSide side) => side == NormalSide.Credit
        ? CreditsPosted - DebitsPosted - DebitsPending
        : DebitsPosted - CreditsPosted - CreditsPending;
}
