using System.Globalization;

namespace Contrapost.Domain.Ledger;

/// <summary>Checks shared by transfers, holds and reversals, run against accounts read under their row locks.</summary>
public static class AccountChecks
{
    /// <summary>
    /// The account exists for this caller, is in <paramref name="currency"/> and, unless
    /// <paramref name="allowInactive"/> (reversals), is active.
    /// </summary>
    public static Result<LockedAccount> Usable(
        IReadOnlyDictionary<Guid, LockedAccount> accounts, Guid accountId, string currency, bool allowInactive = false)
    {
        if (!accounts.TryGetValue(accountId, out var account))
        {
            return Failure(ErrorKind.NotFound, "account-not-found", "Account not found",
                $"Account {accountId} does not exist.", accountId);
        }

        if (account.Currency != currency)
        {
            return Failure(ErrorKind.Rejected, "currency-mismatch", "Currency mismatch",
                $"Account {account.Id} holds {account.Currency}, but the request is in {currency}.", account.Id);
        }

        if (!allowInactive && account.Status != AccountStatus.Active)
        {
            return Failure(ErrorKind.Rejected, "account-not-active", "Account is not active",
                $"Account {account.Id} is {account.Status.ToString().ToLowerInvariant()}.", account.Id);
        }

        return account;
    }

    /// <summary>Rejects <paramref name="updated"/> if it breaks the account's overdraft rule.</summary>
    public static LedgerError? WithinOverdraftRule(LockedAccount account, Balance updated, Direction direction, long amount)
    {
        if (account.OverdraftRule.Allows(updated))
        {
            return null;
        }

        var available = account.Balance.Available(account.NormalSide);
        var side = direction.ToString().ToLowerInvariant();
        return new LedgerError(ErrorKind.Rejected, "insufficient-funds", "Insufficient funds",
            string.Create(CultureInfo.InvariantCulture, $"Available balance {available} is less than the {side} of {amount}."))
        {
            Extensions = new Dictionary<string, object?> { ["account_id"] = account.Id, ["available"] = available },
        };
    }

    private static LedgerError Failure(ErrorKind kind, string code, string title, string detail, Guid accountId) =>
        new(kind, code, title, detail)
        {
            Extensions = new Dictionary<string, object?> { ["account_id"] = accountId },
        };
}
