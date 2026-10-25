using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.Application.Transfers;

public sealed record PostTransfer(
    Guid ClientId,
    string? Currency,
    string? Reference,
    string? Description,
    IReadOnlyList<TransferLeg>? Legs);

public sealed record NewTransfer(
    Guid Id,
    Guid ClientId,
    string Currency,
    string? Reference,
    string? Description,
    IReadOnlyList<TransferLeg> Legs);

public sealed record LegView(Guid AccountId, Direction Direction, long Amount, long AccountSeq);

public sealed record TransferView(
    Guid Id,
    string Status,
    string Currency,
    string? Reference,
    string? Description,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LegView> Legs);

public interface ITransferStore
{
    /// <summary>
    /// Locks the legs' balance rows in account-id order, runs <see cref="TransferRules.Apply"/>
    /// and, if it passes, writes the transaction, postings, balances and outbox message in one
    /// database transaction (design doc §5.1, §8.1).
    /// </summary>
    Task<Result<TransferView>> PostAsync(NewTransfer transfer, CancellationToken cancellationToken);

    Task<TransferView?> GetAsync(Guid clientId, Guid transferId, CancellationToken cancellationToken);
}

public sealed class TransferService(ITransferStore store)
{
    public const int MaxReferenceLength = 128;
    public const int MaxDescriptionLength = 512;

    public async Task<Result<TransferView>> PostAsync(PostTransfer command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>();
        if (TransferRules.Validate(command.Currency, command.Legs) is { } invalid)
        {
            foreach (var (field, messages) in invalid.Errors)
            {
                errors[field] = messages;
            }
        }

        if (command.Reference is { Length: 0 or > MaxReferenceLength })
        {
            errors["reference"] = [$"Between 1 and {MaxReferenceLength} characters when given."];
        }

        if (command.Description is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"At most {MaxDescriptionLength} characters."];
        }

        if (errors.Count > 0)
        {
            return LedgerError.Validation(errors);
        }

        var transfer = new NewTransfer(
            Guid.CreateVersion7(),
            command.ClientId,
            command.Currency!,
            command.Reference,
            command.Description,
            command.Legs!);

        return await store.PostAsync(transfer, cancellationToken);
    }

    public Task<TransferView?> GetAsync(Guid clientId, Guid transferId, CancellationToken cancellationToken) =>
        store.GetAsync(clientId, transferId, cancellationToken);
}
