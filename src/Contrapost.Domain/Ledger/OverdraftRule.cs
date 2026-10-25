namespace Contrapost.Domain.Ledger;

/// <summary>
/// The overdraft policy flags on an account. The same rules are enforced by
/// CHECK constraints on account_balances, so these must stay in step with db/schema.sql.
/// </summary>
public enum OverdraftRule
{
    /// <summary>System accounts that may go negative, e.g. an external funding source.</summary>
    None,

    /// <summary>debits_posted + debits_pending ≤ credits_posted (credit-normal wallets).</summary>
    DebitsMustNotExceedCredits,

    /// <summary>credits_posted + credits_pending ≤ debits_posted (the mirror rule for debit-normal accounts).</summary>
    CreditsMustNotExceedDebits,
}

public static class OverdraftRules
{
    /// <summary>The rule that stops an account's balance going below zero on its normal side.</summary>
    public static OverdraftRule PreventingOverdraft(NormalSide side) => side == NormalSide.Credit
        ? OverdraftRule.DebitsMustNotExceedCredits
        : OverdraftRule.CreditsMustNotExceedDebits;

    public static bool Allows(this OverdraftRule rule, Balance balance) => rule switch
    {
        OverdraftRule.None => true,
        OverdraftRule.DebitsMustNotExceedCredits => checked(balance.DebitsPosted + balance.DebitsPending) <= balance.CreditsPosted,
        OverdraftRule.CreditsMustNotExceedDebits => checked(balance.CreditsPosted + balance.CreditsPending) <= balance.DebitsPosted,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };
}
