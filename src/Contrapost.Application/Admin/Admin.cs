using Contrapost.Application.Accounts;
using Contrapost.Application.Holds;
using Contrapost.Application.Reconciliation;
using Contrapost.Application.Transfers;
using Contrapost.Domain.Ledger;

namespace Contrapost.Application.Admin;

/// <summary>Posted totals across every account in one currency. Invariant I2: debits equal credits.</summary>
public sealed record CurrencyTotals(string Currency, int Accounts, long DebitsPosted, long CreditsPosted, bool Balanced);

/// <summary>Money-moving requests over the last hour, by operation and response status (from the idempotency keys).</summary>
public sealed record RequestCount(string Operation, int Status, int Count);

public sealed record OutboxSummary(int Unpublished, int Failing, int OldestUnpublishedSeconds);

public sealed record OverviewView(
    IReadOnlyList<CurrencyTotals> Currencies,
    IReadOnlyList<RequestCount> RequestsLastHour,
    OutboxSummary Outbox,
    ReconciliationRunView? LatestReconciliation);

public sealed record AccountSearch(string? Query, Guid? ClientId, string? Currency, AccountStatus? Status, Guid? After, int Limit);

/// <summary>An account as an operator sees it: the client view plus the owning client.</summary>
public sealed record AdminAccountView(Guid ClientId, AccountView Account);

public sealed record AdminAccountDetail(Guid ClientId, AccountView Account, IReadOnlyList<HoldView> OpenHolds, EntryPage RecentEntries);

public sealed record AdminHoldView(Guid ClientId, HoldView Hold);

public sealed record HoldSearch(HoldStatus? Status, TimeSpan? ExpiringWithin, Guid? After, int Limit);

public sealed record OutboxMessageView(
    Guid Id,
    Guid AggregateId,
    string Type,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? PublishedAt,
    int Attempts,
    string? LastError);

public enum OutboxState
{
    Unpublished,
    Failing,
}

public sealed record AdminTransactionView(
    Guid ClientId,
    TransferView Transaction,
    HoldView? Hold,
    Guid? ReversedBy,
    IReadOnlyList<OutboxMessageView> Events);

/// <summary>A page in id order; <see cref="NextAfter"/> is null on the last page.</summary>
public sealed record AdminPage<T>(IReadOnlyList<T> Items, Guid? NextAfter);

/// <summary>
/// Cross-tenant reads for the admin dashboard (design doc §5.2, §7.1). Read-only and lock-free. Detail
/// reads find the owning client, then go through the same client-scoped stores the public API uses, so
/// an operator sees exactly what the client would.
/// </summary>
public interface IAdminStore
{
    Task<OverviewView> OverviewAsync(CancellationToken cancellationToken);

    Task<AdminPage<AdminAccountView>> SearchAccountsAsync(AccountSearch search, CancellationToken cancellationToken);

    /// <summary>The client that owns the row, or null if no such row exists.</summary>
    Task<Guid?> AccountOwnerAsync(Guid accountId, CancellationToken cancellationToken);

    Task<Guid?> TransactionOwnerAsync(Guid transactionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HoldView>> OpenHoldsAsync(Guid accountId, CancellationToken cancellationToken);

    Task<Guid?> ReversalOfAsync(Guid transactionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessageView>> EventsForAsync(Guid aggregateId, CancellationToken cancellationToken);

    Task<AdminPage<AdminHoldView>> SearchHoldsAsync(HoldSearch search, CancellationToken cancellationToken);

    Task<AdminPage<OutboxMessageView>> ListOutboxAsync(OutboxState state, Guid? after, int limit, CancellationToken cancellationToken);
}

public sealed class AdminService(IAdminStore admin, IAccountStore accounts, ITransferStore transfers, IHoldStore holds)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    private const int RecentEntries = 20;

    public static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

    public Task<OverviewView> OverviewAsync(CancellationToken cancellationToken) => admin.OverviewAsync(cancellationToken);

    public Task<AdminPage<AdminAccountView>> SearchAccountsAsync(AccountSearch search, CancellationToken cancellationToken) =>
        admin.SearchAccountsAsync(search, cancellationToken);

    public async Task<AdminAccountDetail?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (await admin.AccountOwnerAsync(accountId, cancellationToken) is not { } clientId
            || await accounts.GetAsync(clientId, accountId, cancellationToken) is not { } account)
        {
            return null;
        }

        var openHolds = await admin.OpenHoldsAsync(accountId, cancellationToken);
        var entries = await accounts.ListEntriesAsync(clientId, accountId, null, RecentEntries, cancellationToken);
        return new AdminAccountDetail(clientId, account, openHolds, entries!);
    }

    /// <summary>Entry history of any client's account, paged like <c>/v1/accounts/{id}/entries</c>. Null if the account doesn't exist.</summary>
    public async Task<EntryPage?> ListEntriesAsync(Guid accountId, long? beforeSeq, int? limit, CancellationToken cancellationToken) =>
        await admin.AccountOwnerAsync(accountId, cancellationToken) is { } clientId
            ? await accounts.ListEntriesAsync(clientId, accountId, beforeSeq, PageSize(limit), cancellationToken)
            : null;

    public async Task<AdminTransactionView?> GetTransactionAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        if (await admin.TransactionOwnerAsync(transactionId, cancellationToken) is not { } clientId
            || await transfers.GetAsync(clientId, transactionId, cancellationToken) is not { } transaction)
        {
            return null;
        }

        var hold = transaction.HoldId is { } holdId ? await holds.GetAsync(clientId, holdId, cancellationToken) : null;
        var reversedBy = await admin.ReversalOfAsync(transactionId, cancellationToken);
        var events = await admin.EventsForAsync(transactionId, cancellationToken);
        return new AdminTransactionView(clientId, transaction, hold, reversedBy, events);
    }

    public Task<AdminPage<AdminHoldView>> SearchHoldsAsync(HoldSearch search, CancellationToken cancellationToken) =>
        admin.SearchHoldsAsync(search, cancellationToken);

    public Task<AdminPage<OutboxMessageView>> ListOutboxAsync(OutboxState state, Guid? after, int? limit, CancellationToken cancellationToken) =>
        admin.ListOutboxAsync(state, after, PageSize(limit), cancellationToken);
}
