using Contrapost.Api.Auth;
using Contrapost.Api.Http;
using Contrapost.Application.Clients;
using Contrapost.Application.Transfers;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.Api.Endpoints;

internal static class TransferEndpoints
{
    public sealed record TransferLegRequest(Guid? AccountId, Direction? Direction, long? Amount);

    public sealed record PostTransferRequest(string? Currency, string? Reference, string? Description, IReadOnlyList<TransferLegRequest>? Legs);

    public sealed record ReverseTransferRequest(string? Description);

    public static RouteGroupBuilder MapTransferEndpoints(this RouteGroupBuilder v1)
    {
        var transfers = v1.MapGroup("/transfers");
        transfers.MapPost("/", PostAsync)
            .RequireAuthorization(Scopes.TransfersWrite)
            .AddEndpointFilter<IdempotencyFilter<PostTransferRequest>>();
        transfers.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Scopes.AccountsRead);
        transfers.MapPost("/{id:guid}/reversal", ReverseAsync)
            .RequireAuthorization(Scopes.TransfersWrite)
            .AddEndpointFilter<IdempotencyFilter<ReverseTransferRequest>>()
            .WithMetadata(new ReplayLocation("/v1/transfers/"));
        return v1;
    }

    private static async Task<IResult> PostAsync(PostTransferRequest request, HttpContext http, TransferService transfers, CancellationToken ct)
    {
        if (ToLegs(request.Legs) is not { } legs)
        {
            return MissingLegFields(request.Legs!).ToProblem();
        }

        var result = await transfers.PostAsync(
            new PostTransfer(http.GetClientId(), request.Currency, request.Reference, request.Description, legs), ct);

        return result.IsSuccess
            ? TypedResults.Created($"/v1/transfers/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, TransferService transfers, CancellationToken ct) =>
        await transfers.GetAsync(http.GetClientId(), id, ct) is { } transfer
            ? TypedResults.Ok(transfer)
            : LedgerProblems.NotFound("transfer-not-found", "Transfer not found", $"Transfer {id} does not exist.");

    private static async Task<IResult> ReverseAsync(Guid id, ReverseTransferRequest? request, HttpContext http, TransferService transfers, CancellationToken ct)
    {
        var result = await transfers.ReverseAsync(http.GetClientId(), id, request?.Description, ct);

        // The reversal is itself a transaction, served at /v1/transfers/{id} like any other.
        return result.IsSuccess
            ? TypedResults.Created($"/v1/transfers/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    /// <summary>
    /// Missing legs become an empty list for the domain rules to reject. Returns null when a leg
    /// is missing one of its fields.
    /// </summary>
    private static List<TransferLeg>? ToLegs(IReadOnlyList<TransferLegRequest>? legs)
    {
        if (legs is null)
        {
            return [];
        }

        if (legs.Any(l => l.AccountId is null || l.Direction is null || l.Amount is null))
        {
            return null;
        }

        return legs.Select(l => new TransferLeg(l.AccountId!.Value, l.Direction!.Value, l.Amount!.Value)).ToList();
    }

    private static LedgerError MissingLegFields(IReadOnlyList<TransferLegRequest> legs)
    {
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < legs.Count; i++)
        {
            if (legs[i].AccountId is null) errors[$"legs[{i}].account_id"] = ["Required."];
            if (legs[i].Direction is null) errors[$"legs[{i}].direction"] = ["Required: debit or credit."];
            if (legs[i].Amount is null) errors[$"legs[{i}].amount"] = ["Required."];
        }

        return LedgerError.Validation(errors);
    }
}
