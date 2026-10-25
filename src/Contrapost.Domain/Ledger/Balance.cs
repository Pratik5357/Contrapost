namespace Contrapost.Domain.Ledger;

/// <summary>The four running totals kept for every account (design doc §4.3).</summary>
public readonly record struct Balance(long DebitsPosted, long CreditsPosted, long DebitsPending, long CreditsPending)
{
    public Balance Post(Direction direction, long amount) => direction == Direction.Debit
        ? this with { DebitsPosted = checked(DebitsPosted + amount) }
        : this with { CreditsPosted = checked(CreditsPosted + amount) };

    /// <summary>Reserves an amount for a hold: adds to the pending total on that side.</summary>
    public Balance Reserve(Direction direction, long amount) => direction == Direction.Debit
        ? this with { DebitsPending = checked(DebitsPending + amount) }
        : this with { CreditsPending = checked(CreditsPending + amount) };

    /// <summary>Releases a reserved amount (void or expiry).</summary>
    public Balance Release(Direction direction, long amount) => direction == Direction.Debit
        ? this with { DebitsPending = DebitsPending - amount }
        : this with { CreditsPending = CreditsPending - amount };

    /// <summary>Moves a reserved amount from pending to posted (capture).</summary>
    public Balance Settle(Direction direction, long amount) => Release(direction, amount).Post(direction, amount);

    public long Posted(NormalSide side) => side == NormalSide.Credit
        ? CreditsPosted - DebitsPosted
        : DebitsPosted - CreditsPosted;

    /// <summary>Posted balance minus pending amounts that would reduce it; overdraft checks use this.</summary>
    public long Available(NormalSide side) => side == NormalSide.Credit
        ? CreditsPosted - DebitsPosted - DebitsPending
        : DebitsPosted - CreditsPosted - CreditsPending;
}
