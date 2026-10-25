using System.Globalization;

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
    /// </summary>
    public static Result<IReadOnlyDictionary<Guid, Balance>> Apply(
        string currency,
        IReadOnlyList<TransferLeg> legs,
        IReadOnlyDictionary<Guid, LockedAccount> accounts)
    {
        var updated = new Dictionary<Guid, Balance>(legs.Count);

        foreach (var leg in legs)
        {
            if (!accounts.TryGetValue(leg.AccountId, out var account))
            {
                return new LedgerError(ErrorKind.NotFound, "account-not-found", "Account not found",
                    $"Account {leg.AccountId} does not exist.")
                {
                    Extensions = new Dictionary<string, object?> { ["account_id"] = leg.AccountId },
                };
            }

            if (account.Currency != currency)
            {
                return new LedgerError(ErrorKind.Rejected, "currency-mismatch", "Currency mismatch",
                    $"Account {account.Id} holds {account.Currency}, but the transfer is in {currency}.")
                {
                    Extensions = new Dictionary<string, object?> { ["account_id"] = account.Id },
                };
            }

            if (account.Status != AccountStatus.Active)
            {
                return new LedgerError(ErrorKind.Rejected, "account-not-active", "Account is not active",
                    $"Account {account.Id} is {account.Status.ToString().ToLowerInvariant()}.")
                {
                    Extensions = new Dictionary<string, object?> { ["account_id"] = account.Id },
                };
            }

            var balance = account.Balance.Post(leg.Direction, leg.Amount);
            if (!account.OverdraftRule.Allows(balance))
            {
                var available = account.Balance.Available(account.NormalSide);
                var side = leg.Direction.ToString().ToLowerInvariant();
                return new LedgerError(ErrorKind.Rejected, "insufficient-funds", "Insufficient funds",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Available balance {available} is less than the {side} of {leg.Amount}."))
                {
                    Extensions = new Dictionary<string, object?>
                    {
                        ["account_id"] = account.Id,
                        ["available"] = available,
                    },
                };
            }

            updated[account.Id] = balance;
        }

        return updated;
    }
}
