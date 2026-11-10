namespace Contrapost.Application.Retention;

/// <summary>
/// Deletes data past its retention (design doc §10). Each call deletes at most <c>batchSize</c> rows in one
/// short transaction and skips rows another Worker is deleting, so several Workers can run the cleanup.
/// </summary>
public interface IRetentionStore
{
    /// <summary>
    /// Completed idempotency keys older than <paramref name="retention"/>. A retry after that is treated as a
    /// new request; a transfer's unique <c>reference</c> still catches a duplicate (§7.3).
    /// </summary>
    Task<int> DeleteExpiredIdempotencyKeysAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Outbox messages published longer ago than <paramref name="retention"/>. Unpublished rows are never
    /// deleted; reconciliation's event check (I7) only looks inside the retention window.
    /// </summary>
    Task<int> DeletePublishedOutboxMessagesAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken);
}
