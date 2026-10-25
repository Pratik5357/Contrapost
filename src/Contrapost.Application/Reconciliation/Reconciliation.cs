using System.Text.Json;

namespace Contrapost.Application.Reconciliation;

public sealed record ReconciliationRunView(
    Guid Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? AccountsChecked,
    int? FindingsCount);

public sealed record FindingView(long Id, string CheckName, Guid? SubjectId, JsonElement? Expected, JsonElement? Actual);

/// <summary>
/// Recomputes state from the ledger's source of truth and records every difference
/// (design doc §8.3, §10). A run is "clean" when it finds nothing.
/// </summary>
public interface IReconciliationStore
{
    /// <summary>Runs every check once. Returns null if another run holds the reconciliation lock.</summary>
    Task<ReconciliationRunView?> RunAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconciliationRunView>> ListRunsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Null if the run doesn't exist.</summary>
    Task<IReadOnlyList<FindingView>?> ListFindingsAsync(Guid runId, int limit, CancellationToken cancellationToken);
}
