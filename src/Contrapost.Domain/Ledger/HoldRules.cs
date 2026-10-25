namespace Contrapost.Domain.Ledger;

public enum HoldStatus
{
    Pending,
    PartiallyCaptured,
    Captured,
    Voided,
    Expired,
}

/// <summary>A hold as read under its row lock.</summary>
public sealed record HoldState(
    Guid Id,
    Guid DebitAccountId,
    Guid CreditAccountId,
    string Currency,
    long Amount,
    long CapturedAmount,
    HoldStatus Status,
    DateTimeOffset ExpiresAt)
{
    public bool IsOpen => Status is HoldStatus.Pending or HoldStatus.PartiallyCaptured;

    /// <summary>What is still reserved: <c>amount − captured_amount</c> while open, 0 once final (design doc §4.4).</summary>
    public long Remaining => IsOpen ? Amount - CapturedAmount : 0;
}

/// <summary>New balances for the hold's two accounts, plus the hold's new state.</summary>
public sealed record HoldChange(Balance Debit, Balance Credit, long CapturedAmount, HoldStatus Status);

/// <summary>The hold lifecycle from design doc §4.4. Holds change pending totals only; captures also post.</summary>
public static class HoldRules
{
    public const int MinExpirySeconds = 1;
    public const int MaxExpirySeconds = 30 * 24 * 60 * 60;
    public const int DefaultExpirySeconds = 7 * 24 * 60 * 60;

    public static LedgerError? Validate(string? currency, Guid? debitAccountId, Guid? creditAccountId, long? amount, int? expiresInSeconds)
    {
        var errors = new Dictionary<string, string[]>();

        if (!AccountRules.IsValidCurrency(currency))
        {
            errors["currency"] = ["Must be a three-letter uppercase ISO 4217 code."];
        }

        if (debitAccountId is null)
        {
            errors["debit_account_id"] = ["Required."];
        }

        if (creditAccountId is null)
        {
            errors["credit_account_id"] = ["Required."];
        }
        else if (creditAccountId == debitAccountId)
        {
            errors["credit_account_id"] = ["Must differ from debit_account_id."];
        }

        if (amount is not > 0)
        {
            errors["amount"] = ["Must be a positive integer in minor units."];
        }

        if (expiresInSeconds is < MinExpirySeconds or > MaxExpirySeconds)
        {
            errors["expires_in_seconds"] = [$"Between {MinExpirySeconds} and {MaxExpirySeconds} when given."];
        }

        return errors.Count == 0 ? null : LedgerError.Validation(errors);
    }

    /// <summary>Reserves <paramref name="amount"/> on both accounts, checked against the available balance.</summary>
    public static Result<HoldChange> Create(
        string currency, Guid debitAccountId, Guid creditAccountId, long amount, IReadOnlyDictionary<Guid, LockedAccount> accounts)
    {
        var debit = AccountChecks.Usable(accounts, debitAccountId, currency);
        if (debit.Error is { } debitError)
        {
            return debitError;
        }

        var credit = AccountChecks.Usable(accounts, creditAccountId, currency);
        if (credit.Error is { } creditError)
        {
            return creditError;
        }

        var debitBalance = debit.Value.Balance.Reserve(Direction.Debit, amount);
        var creditBalance = credit.Value.Balance.Reserve(Direction.Credit, amount);
        return AccountChecks.WithinOverdraftRule(debit.Value, debitBalance, Direction.Debit, amount)
            ?? AccountChecks.WithinOverdraftRule(credit.Value, creditBalance, Direction.Credit, amount)
            ?? (Result<HoldChange>)new HoldChange(debitBalance, creditBalance, 0, HoldStatus.Pending);
    }

    /// <summary>
    /// Captures <paramref name="amount"/> (default: everything remaining). <paramref name="now"/> is the
    /// database clock, so workers with skewed clocks agree on expiry (design doc §8.4).
    /// </summary>
    public static Result<HoldChange> Capture(HoldState hold, long? amount, DateTimeOffset now, LockedAccount debit, LockedAccount credit)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(debit);
        ArgumentNullException.ThrowIfNull(credit);

        if (OpenAndUnexpired(hold, now) is { } closed)
        {
            return closed;
        }

        var capture = amount ?? hold.Remaining;
        if (capture <= 0)
        {
            return LedgerError.Validation(new Dictionary<string, string[]>
            {
                ["amount"] = ["Must be a positive integer in minor units."],
            });
        }

        // Depends on the hold's state, so it is a business rejection (stored for retries), not validation.
        if (capture > hold.Remaining)
        {
            return new LedgerError(ErrorKind.Rejected, "capture-exceeds-remaining", "Capture exceeds remaining amount",
                $"Hold {hold.Id} has {hold.Remaining} remaining; cannot capture {capture}.");
        }

        var captured = hold.CapturedAmount + capture;
        return new HoldChange(
            debit.Balance.Settle(Direction.Debit, capture),
            credit.Balance.Settle(Direction.Credit, capture),
            captured,
            captured == hold.Amount ? HoldStatus.Captured : HoldStatus.PartiallyCaptured);
    }

    /// <summary>Releases the remaining amount, ending the hold as voided or expired.</summary>
    public static Result<HoldChange> Release(HoldState hold, HoldStatus outcome, DateTimeOffset now, LockedAccount debit, LockedAccount credit)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(debit);
        ArgumentNullException.ThrowIfNull(credit);

        if (outcome == HoldStatus.Voided && OpenAndUnexpired(hold, now) is { } closed)
        {
            return closed;
        }

        if (outcome == HoldStatus.Expired && (!hold.IsOpen || hold.ExpiresAt > now))
        {
            throw new InvalidOperationException($"Hold {hold.Id} is not due to expire.");
        }

        return new HoldChange(
            debit.Balance.Release(Direction.Debit, hold.Remaining),
            credit.Balance.Release(Direction.Credit, hold.Remaining),
            hold.CapturedAmount,
            outcome);
    }

    private static LedgerError? OpenAndUnexpired(HoldState hold, DateTimeOffset now)
    {
        if (!hold.IsOpen)
        {
            return new LedgerError(ErrorKind.Rejected, "hold-closed", "Hold is closed",
                $"Hold {hold.Id} is {Label(hold.Status)}.");
        }

        // Checked here, not left to the expiry job, which runs only every 30 s (design doc §4.4).
        if (hold.ExpiresAt <= now)
        {
            return new LedgerError(ErrorKind.Rejected, "hold-expired", "Hold has expired",
                $"Hold {hold.Id} expired at {hold.ExpiresAt:O}.");
        }

        return null;
    }

    public static string Label(HoldStatus status) => status switch
    {
        HoldStatus.Pending => "pending",
        HoldStatus.PartiallyCaptured => "partially_captured",
        HoldStatus.Captured => "captured",
        HoldStatus.Voided => "voided",
        HoldStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
