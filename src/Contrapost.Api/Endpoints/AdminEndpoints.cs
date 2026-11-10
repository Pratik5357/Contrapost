using System.Text.Json;
using Contrapost.Api.Auth;
using Contrapost.Api.Http;
using Contrapost.Application.Admin;
using Contrapost.Application.Clients;
using Contrapost.Application.Reconciliation;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;
using Microsoft.AspNetCore.Mvc;

namespace Contrapost.Api.Endpoints;

/// <summary>Operator reads (design doc §7.1). The only endpoints that cross tenants; all require admin:read.</summary>
internal static partial class AdminEndpoints
{
    public sealed record PageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").RequireAuthorization(Scopes.AdminRead).AddEndpointFilter(AuditAsync);
        admin.MapGet("/overview", OverviewAsync);
        admin.MapGet("/accounts", SearchAccountsAsync);
        admin.MapGet("/accounts/{id:guid}", GetAccountAsync);
        admin.MapGet("/accounts/{id:guid}/entries", ListEntriesAsync);
        admin.MapGet("/transactions/{id:guid}", GetTransactionAsync);
        admin.MapGet("/holds", SearchHoldsAsync);
        admin.MapGet("/outbox", ListOutboxAsync);
        admin.MapGet("/reconciliation/runs", ListRunsAsync);
        admin.MapGet("/reconciliation/runs/{id:guid}/findings", ListFindingsAsync);
        return v1;
    }

    private static async Task<IResult> OverviewAsync(AdminService admin, CancellationToken ct) =>
        TypedResults.Ok(await admin.OverviewAsync(ct));

    private static async Task<IResult> SearchAccountsAsync(
        string? q,
        [FromQuery(Name = "client_id")] Guid? clientId,
        string? currency,
        string? status,
        string? after,
        int? limit,
        AdminService admin,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var accountStatus = ParseLabel<AccountStatus>(status, "status", errors);
        var afterId = ParseCursor(after, errors);
        if (currency is not null && !(currency.Length == 3 && currency.All(char.IsAsciiLetterUpper)))
        {
            errors["currency"] = ["Use a three-letter ISO 4217 code such as INR."];
        }

        if (errors.Count > 0)
        {
            return LedgerError.Validation(errors).ToProblem();
        }

        var page = await admin.SearchAccountsAsync(
            new AccountSearch(q, clientId, currency, accountStatus, afterId, AdminService.PageSize(limit)), ct);
        return TypedResults.Ok(ToResponse(page));
    }

    private static async Task<IResult> GetAccountAsync(Guid id, AdminService admin, CancellationToken ct) =>
        await admin.GetAccountAsync(id, ct) is { } account
            ? TypedResults.Ok(new
            {
                account.ClientId,
                account.Account,
                account.OpenHolds,
                RecentEntries = ToEntries(account.RecentEntries),
            })
            : AccountNotFound(id);

    private static async Task<IResult> ListEntriesAsync(Guid id, string? after, int? limit, AdminService admin, CancellationToken ct)
    {
        long? beforeSeq = null;
        if (after is not null)
        {
            if (Cursors.DecodeSeq(after) is not { } seq)
            {
                return InvalidCursor();
            }

            beforeSeq = seq;
        }

        return await admin.ListEntriesAsync(id, beforeSeq, limit, ct) is { } page
            ? TypedResults.Ok(ToEntries(page))
            : AccountNotFound(id);
    }

    private static async Task<IResult> GetTransactionAsync(Guid id, AdminService admin, CancellationToken ct) =>
        await admin.GetTransactionAsync(id, ct) is { } transaction
            ? TypedResults.Ok(transaction)
            : LedgerProblems.NotFound("transaction-not-found", "Transaction not found", $"Transaction {id} does not exist.");

    private static async Task<IResult> SearchHoldsAsync(
        string? status,
        [FromQuery(Name = "expiring_within")] int? expiringWithinSeconds,
        string? after,
        int? limit,
        AdminService admin,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var holdStatus = ParseLabel<HoldStatus>(status, "status", errors);
        var afterId = ParseCursor(after, errors);
        if (expiringWithinSeconds is <= 0 or > 7 * 24 * 3600)
        {
            errors["expiring_within"] = ["Seconds, between 1 and 604800 (7 days)."];
        }

        if (errors.Count > 0)
        {
            return LedgerError.Validation(errors).ToProblem();
        }

        var within = expiringWithinSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var page = await admin.SearchHoldsAsync(new HoldSearch(holdStatus, within, afterId, AdminService.PageSize(limit)), ct);
        return TypedResults.Ok(ToResponse(page));
    }

    private static async Task<IResult> ListOutboxAsync(string? state, string? after, int? limit, AdminService admin, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var outboxState = ParseLabel<OutboxState>(state, "state", errors) ?? OutboxState.Unpublished;
        var afterId = ParseCursor(after, errors);
        if (errors.Count > 0)
        {
            return LedgerError.Validation(errors).ToProblem();
        }

        return TypedResults.Ok(ToResponse(await admin.ListOutboxAsync(outboxState, afterId, limit, ct)));
    }

    private static async Task<IResult> ListRunsAsync(int? limit, IReconciliationStore reconciliation, CancellationToken ct) =>
        TypedResults.Ok(await reconciliation.ListRunsAsync(Math.Clamp(limit ?? 20, 1, 100), ct));

    private static async Task<IResult> ListFindingsAsync(Guid id, int? limit, IReconciliationStore reconciliation, CancellationToken ct) =>
        await reconciliation.ListFindingsAsync(id, Math.Clamp(limit ?? 100, 1, 1000), ct) is { } findings
            ? TypedResults.Ok(findings)
            : LedgerProblems.NotFound("reconciliation-run-not-found", "Reconciliation run not found", $"Run {id} does not exist.");

    /// <summary>
    /// Every admin read crosses tenants, so each one is logged with the client (the dashboard's key) and
    /// the operator session the dashboard passes in <c>X-Operator-Session</c> (design doc §11).
    /// </summary>
    private static async ValueTask<object?> AuditAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Contrapost.Api.Admin");
        if (logger.IsEnabled(LogLevel.Information))
        {
            var path = http.Request.Path + http.Request.QueryString;
            var clientId = http.GetClientId();
            var session = http.Request.Headers["X-Operator-Session"].ToString() is { Length: > 0 } s ? s : "none";
            LogAdminRead(logger, http.Request.Method, path, clientId, session);
        }

        return await next(context);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin read {Method} {Path} by client {ClientId}, operator session {OperatorSession}")]
    private static partial void LogAdminRead(ILogger logger, string method, string path, Guid clientId, string operatorSession);

    private static PageResponse<T> ToResponse<T>(AdminPage<T> page) =>
        new(page.Items, page.NextAfter is { } next ? Cursors.EncodeId(next) : null);

    private static AccountEndpoints.EntriesResponse ToEntries(Application.Accounts.EntryPage page) =>
        new(page.Entries, page.NextBeforeSeq is { } next ? Cursors.EncodeSeq(next) : null);

    /// <summary>Parses a snake_case label (as the JSON uses) into its enum; records an error if it isn't one.</summary>
    private static TEnum? ParseLabel<TEnum>(string? label, string field, Dictionary<string, string[]> errors)
        where TEnum : struct, Enum
    {
        if (label is null)
        {
            return null;
        }

        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()) == label)
            {
                return value;
            }
        }

        errors[field] = [$"One of: {string.Join(", ", Enum.GetNames<TEnum>().Select(JsonNamingPolicy.SnakeCaseLower.ConvertName))}."];
        return null;
    }

    private static Guid? ParseCursor(string? after, Dictionary<string, string[]> errors)
    {
        if (after is null)
        {
            return null;
        }

        if (Cursors.DecodeId(after) is { } id)
        {
            return id;
        }

        errors["after"] = ["Invalid cursor."];
        return null;
    }

    private static IResult InvalidCursor() =>
        LedgerError.Validation(new Dictionary<string, string[]> { ["after"] = ["Invalid cursor."] }).ToProblem();

    private static IResult AccountNotFound(Guid id) =>
        LedgerProblems.NotFound("account-not-found", "Account not found", $"Account {id} does not exist.");
}
