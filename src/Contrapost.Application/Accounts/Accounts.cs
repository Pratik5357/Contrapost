using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.Application.Accounts;

public sealed record CreateAccount(Guid ClientId, string? Code, string? Currency, NormalSide? NormalSide, bool PreventOverdraft);

public sealed record NewAccount(Guid Id, Guid ClientId, string Code, string Currency, NormalSide NormalSide, OverdraftRule OverdraftRule);

public sealed record BalanceView(
    long Posted,
    long Available,
    long DebitsPosted,
    long CreditsPosted,
    long DebitsPending,
    long CreditsPending)
{
    public static BalanceView From(Balance balance, NormalSide side) => new(
        balance.Posted(side),
        balance.Available(side),
        balance.DebitsPosted,
        balance.CreditsPosted,
        balance.DebitsPending,
        balance.CreditsPending);
}

public sealed record AccountView(
    Guid Id,
    string Code,
    string Currency,
    NormalSide NormalSide,
    bool PreventOverdraft,
    AccountStatus Status,
    DateTimeOffset CreatedAt,
    BalanceView Balance);

public sealed record EntryView(Guid TransactionId, long AccountSeq, Direction Direction, long Amount, DateTimeOffset CreatedAt);

/// <summary>Entries newest first. <see cref="NextBeforeSeq"/> is null on the last page.</summary>
public sealed record EntryPage(IReadOnlyList<EntryView> Entries, long? NextBeforeSeq);

public interface IAccountStore
{
    /// <summary>Creates the account, its balance row and an account.created outbox message in one transaction.</summary>
    Task<Result<AccountView>> CreateAsync(NewAccount account, CancellationToken cancellationToken);

    Task<AccountView?> GetAsync(Guid clientId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Returns null if the account doesn't exist or belongs to another client.</summary>
    Task<EntryPage?> ListEntriesAsync(Guid clientId, Guid accountId, long? beforeSeq, int limit, CancellationToken cancellationToken);
}

public sealed class AccountService(IAccountStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<Result<AccountView>> CreateAsync(CreateAccount command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (AccountRules.ValidateNew(command.Code, command.Currency, command.NormalSide) is { } invalid)
        {
            return invalid;
        }

        var side = command.NormalSide!.Value;
        var account = new NewAccount(
            Guid.CreateVersion7(),
            command.ClientId,
            command.Code!,
            command.Currency!,
            side,
            command.PreventOverdraft ? OverdraftRules.PreventingOverdraft(side) : OverdraftRule.None);

        return await store.CreateAsync(account, cancellationToken);
    }

    public Task<AccountView?> GetAsync(Guid clientId, Guid accountId, CancellationToken cancellationToken) =>
        store.GetAsync(clientId, accountId, cancellationToken);

    public Task<EntryPage?> ListEntriesAsync(Guid clientId, Guid accountId, long? beforeSeq, int? limit, CancellationToken cancellationToken) =>
        store.ListEntriesAsync(clientId, accountId, beforeSeq, Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize), cancellationToken);
}
