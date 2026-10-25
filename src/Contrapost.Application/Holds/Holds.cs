using Contrapost.Application.Transfers;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.Application.Holds;

public sealed record CreateHold(
    Guid ClientId,
    Guid? DebitAccountId,
    Guid? CreditAccountId,
    string? Currency,
    long? Amount,
    int? ExpiresInSeconds);

public sealed record NewHold(Guid Id, Guid ClientId, Guid DebitAccountId, Guid CreditAccountId, string Currency, long Amount, int ExpiresInSeconds);

public sealed record HoldView(
    Guid Id,
    HoldStatus Status,
    Guid DebitAccountId,
    Guid CreditAccountId,
    string Currency,
    long Amount,
    long CapturedAmount,
    long Remaining,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>A capture's result: the hold after it, and the capture transaction it posted.</summary>
public sealed record CaptureView(HoldView Hold, TransferView Transaction);

public interface IHoldStore
{
    Task<Result<HoldView>> CreateAsync(NewHold hold, CancellationToken cancellationToken);

    Task<HoldView?> GetAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken);

    /// <summary>Locks the hold, then its two balance rows in account-id order, and posts a capture transaction.</summary>
    Task<Result<CaptureView>> CaptureAsync(Guid clientId, Guid holdId, long? amount, CancellationToken cancellationToken);

    Task<Result<HoldView>> VoidAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    /// Expires up to <paramref name="batchSize"/> overdue holds in one transaction, skipping holds
    /// another worker has locked. Returns how many were expired.
    /// </summary>
    Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class HoldService(IHoldStore store)
{
    public async Task<Result<HoldView>> CreateAsync(CreateHold command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (HoldRules.Validate(command.Currency, command.DebitAccountId, command.CreditAccountId, command.Amount, command.ExpiresInSeconds) is { } invalid)
        {
            return invalid;
        }

        return await store.CreateAsync(
            new NewHold(
                Guid.CreateVersion7(),
                command.ClientId,
                command.DebitAccountId!.Value,
                command.CreditAccountId!.Value,
                command.Currency!,
                command.Amount!.Value,
                command.ExpiresInSeconds ?? HoldRules.DefaultExpirySeconds),
            cancellationToken);
    }

    public Task<HoldView?> GetAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken) =>
        store.GetAsync(clientId, holdId, cancellationToken);

    public async Task<Result<CaptureView>> CaptureAsync(Guid clientId, Guid holdId, long? amount, CancellationToken cancellationToken)
    {
        if (amount is <= 0)
        {
            return LedgerError.Validation(new Dictionary<string, string[]> { ["amount"] = ["Must be a positive integer in minor units."] });
        }

        return await store.CaptureAsync(clientId, holdId, amount, cancellationToken);
    }

    public Task<Result<HoldView>> VoidAsync(Guid clientId, Guid holdId, CancellationToken cancellationToken) =>
        store.VoidAsync(clientId, holdId, cancellationToken);
}
