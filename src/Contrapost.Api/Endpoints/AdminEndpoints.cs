using Contrapost.Api.Http;
using Contrapost.Application.Clients;
using Contrapost.Application.Reconciliation;

namespace Contrapost.Api.Endpoints;

/// <summary>Operator reads (design doc §7.1). The only endpoints that cross tenants; all require admin:read.</summary>
internal static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").RequireAuthorization(Scopes.AdminRead);
        admin.MapGet("/reconciliation/runs", ListRunsAsync);
        admin.MapGet("/reconciliation/runs/{id:guid}/findings", ListFindingsAsync);
        return v1;
    }

    private static async Task<IResult> ListRunsAsync(int? limit, IReconciliationStore reconciliation, CancellationToken ct) =>
        TypedResults.Ok(await reconciliation.ListRunsAsync(Math.Clamp(limit ?? 20, 1, 100), ct));

    private static async Task<IResult> ListFindingsAsync(Guid id, int? limit, IReconciliationStore reconciliation, CancellationToken ct) =>
        await reconciliation.ListFindingsAsync(id, Math.Clamp(limit ?? 100, 1, 1000), ct) is { } findings
            ? TypedResults.Ok(findings)
            : LedgerProblems.NotFound("reconciliation-run-not-found", "Reconciliation run not found", $"Run {id} does not exist.");
}
