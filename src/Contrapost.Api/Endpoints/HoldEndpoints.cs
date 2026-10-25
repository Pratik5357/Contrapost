using Contrapost.Api.Auth;
using Contrapost.Api.Http;
using Contrapost.Application.Clients;
using Contrapost.Application.Holds;

namespace Contrapost.Api.Endpoints;

internal static class HoldEndpoints
{
    public sealed record CreateHoldRequest(Guid? DebitAccountId, Guid? CreditAccountId, string? Currency, long? Amount, int? ExpiresInSeconds);

    /// <summary>Omit <see cref="Amount"/> (or the whole body) to capture everything remaining.</summary>
    public sealed record CaptureHoldRequest(long? Amount);

    public sealed record VoidHoldRequest;

    public static RouteGroupBuilder MapHoldEndpoints(this RouteGroupBuilder v1)
    {
        var holds = v1.MapGroup("/holds");
        holds.MapPost("/", CreateAsync)
            .RequireAuthorization(Scopes.HoldsWrite)
            .AddEndpointFilter<IdempotencyFilter<CreateHoldRequest>>();
        holds.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Scopes.AccountsRead);
        holds.MapPost("/{id:guid}/capture", CaptureAsync)
            .RequireAuthorization(Scopes.HoldsWrite)
            .AddEndpointFilter<IdempotencyFilter<CaptureHoldRequest>>();
        holds.MapPost("/{id:guid}/void", VoidAsync)
            .RequireAuthorization(Scopes.HoldsWrite)
            .AddEndpointFilter<IdempotencyFilter<VoidHoldRequest>>();
        return v1;
    }

    private static async Task<IResult> CreateAsync(CreateHoldRequest request, HttpContext http, HoldService holds, CancellationToken ct)
    {
        var result = await holds.CreateAsync(new CreateHold(http.GetClientId(), request.DebitAccountId, request.CreditAccountId,
            request.Currency, request.Amount, request.ExpiresInSeconds), ct);

        return result.IsSuccess
            ? TypedResults.Created($"/v1/holds/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, HoldService holds, CancellationToken ct) =>
        await holds.GetAsync(http.GetClientId(), id, ct) is { } hold
            ? TypedResults.Ok(hold)
            : LedgerProblems.NotFound("hold-not-found", "Hold not found", $"Hold {id} does not exist.");

    private static async Task<IResult> CaptureAsync(Guid id, CaptureHoldRequest? request, HttpContext http, HoldService holds, CancellationToken ct)
    {
        var result = await holds.CaptureAsync(http.GetClientId(), id, request?.Amount, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    // `request` carries nothing yet; it is bound so the idempotency filter hashes a void like any other POST.
    private static async Task<IResult> VoidAsync(Guid id, VoidHoldRequest? request, HttpContext http, HoldService holds, CancellationToken ct)
    {
        var result = await holds.VoidAsync(http.GetClientId(), id, ct);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }
}
