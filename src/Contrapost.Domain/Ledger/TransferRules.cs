namespace Contrapost.Domain.Ledger;

public sealed record TransferLeg(Guid AccountId, Direction Direction, long Amount);

/// <summary>An account as read under its row lock, ready for the transfer checks.</summary>
public sealed record LockedAccount(
    Guid Id,
    string Currency,
    NormalSide NormalSide,
    AccountStatus Status,
    OverdraftRule OverdraftRule,
    Balance Balance);

public static class TransferRules
{
    public const int MinLegs = 2;
    public const int MaxLegs = 10;

    /// <summary>Checks that need no database access: shape, amounts and Σ debits = Σ credits.</summary>
    public static LedgerError? Validate(string? currency, IReadOnlyList<TransferLeg>? legs)
    {
        var errors = new Dictionary<string, string[]>();

        if (!AccountRules.IsValidCurrency(currency))
        {
            errors["currency"] = ["Must be a three-letter uppercase ISO 4217 code."];
        }

        if (legs is null || legs.Count is < MinLegs or > MaxLegs)
        {
            errors["legs"] = [$"A transfer has between {MinLegs} and {MaxLegs} legs."];
            return LedgerError.Validation(errors);
        }

        for (var i = 0; i < legs.Count; i++)
        {
            if (legs[i].Amount <= 0)
            {
                errors[$"legs[{i}].amount"] = ["Must be a positive integer in minor units."];
            }
        }

        if (legs.Select(l => l.AccountId).Distinct().Count() != legs.Count)
        {
            errors["legs"] = ["An account can appear in only one leg."];
        }

        if (errors.Count == 0)
        {
            try
            {
                var debits = legs.Where(l => l.Direction == Direction.Debit).Sum(l => l.Amount);
                var credits = legs.Where(l => l.Direction == Direction.Credit).Sum(l => l.Amount);
                if (debits != credits)
                {
                    errors["legs"] = [$"Debits ({debits}) must equal credits ({credits})."];
                }
            }
            catch (OverflowException)
            {
                errors["legs"] = ["Amounts are too large."];
            }
        }

        return errors.Count == 0 ? null : LedgerError.Validation(errors);
    }

    /// <summary>
    /// Checks that need the locked accounts, and returns each account's new balance.
    /// <paramref name="accounts"/> holds only accounts owned by the caller, so a missing id is "not found".
    /// Reversals may touch frozen or closed accounts but still obey overdraft rules (design doc §4.5).
    /// </summary>
    public static Result<IReadOnlyDictionary<Guid, Balance>> Apply(
        string currency,
        IReadOnlyList<TransferLeg> legs,
        IReadOnlyDictionary<Guid, LockedAccount> accounts,
        bool isReversal = false)
    {
        var updated = new Dictionary<Guid, Balance>(legs.Count);

        foreach (var leg in legs)
        {
            var usable = AccountChecks.Usable(accounts, leg.AccountId, currency, allowInactive: isReversal);
            if (usable.Error is { } unusable)
            {
                return unusable;
            }

            var account = usable.Value;
            var balance = account.Balance.Post(leg.Direction, leg.Amount);
            if (AccountChecks.WithinOverdraftRule(account, balance, leg.Direction, leg.Amount) is { } overdrawn)
            {
                return overdrawn;
            }

            updated[account.Id] = balance;
        }

        return updated;
    }

    /// <summary>The legs of a reversal: the original legs with debit and credit swapped.</summary>
    public static IReadOnlyList<TransferLeg> Reversed(IEnumerable<TransferLeg> legs) =>
        legs.Select(l => l with { Direction = l.Direction == Direction.Debit ? Direction.Credit : Direction.Debit }).ToList();
}
