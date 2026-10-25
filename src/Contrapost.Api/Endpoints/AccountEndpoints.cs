using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Contrapost.Api.Auth;
using Contrapost.Api.Http;
using Contrapost.Application.Accounts;
using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.Api.Endpoints;

internal static class AccountEndpoints
{
    public sealed record CreateAccountRequest(string? Code, string? Currency, NormalSide? NormalSide, bool? PreventOverdraft);

    public sealed record EntriesResponse(IReadOnlyList<EntryView> Entries, string? NextCursor);

    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder v1)
    {
        var accounts = v1.MapGroup("/accounts");
        accounts.MapPost("/", CreateAsync);
        accounts.MapGet("/{id:guid}", GetAsync);
        accounts.MapGet("/{id:guid}/balance", GetBalanceAsync);
        accounts.MapGet("/{id:guid}/entries", ListEntriesAsync);
        return v1;
    }

    private static async Task<IResult> CreateAsync(CreateAccountRequest request, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        // Accounts prevent overdrafts unless the caller explicitly opts out (system accounts).
        var result = await accounts.CreateAsync(
            new CreateAccount(http.GetClientId(), request.Code, request.Currency, request.NormalSide, request.PreventOverdraft ?? true), ct);

        return result.IsSuccess
            ? TypedResults.Created($"/v1/accounts/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, AccountService accounts, CancellationToken ct) =>
        await accounts.GetAsync(http.GetClientId(), id, ct) is { } account
            ? TypedResults.Ok(account)
            : AccountNotFound(id);

    private static async Task<IResult> GetBalanceAsync(Guid id, HttpContext http, AccountService accounts, CancellationToken ct) =>
        await accounts.GetAsync(http.GetClientId(), id, ct) is { } account
            ? TypedResults.Ok(account.Balance)
            : AccountNotFound(id);

    private static async Task<IResult> ListEntriesAsync(Guid id, string? after, int? limit, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        long? beforeSeq = null;
        if (after is not null)
        {
            if (Cursor.Decode(after) is not { } seq)
            {
                return LedgerError.Validation(new Dictionary<string, string[]> { ["after"] = ["Invalid cursor."] }).ToProblem();
            }

            beforeSeq = seq;
        }

        var page = await accounts.ListEntriesAsync(http.GetClientId(), id, beforeSeq, limit, ct);
        return page is null
            ? AccountNotFound(id)
            : TypedResults.Ok(new EntriesResponse(page.Entries, page.NextBeforeSeq is { } next ? Cursor.Encode(next) : null));
    }

    private static IResult AccountNotFound(Guid id) =>
        LedgerProblems.NotFound("account-not-found", "Account not found", $"Account {id} does not exist.");

    /// <summary>Opaque keyset cursor over account_seq (design doc §7.4).</summary>
    private static class Cursor
    {
        private const string Prefix = "seq:";

        public static string Encode(long accountSeq) =>
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Prefix + accountSeq.ToString(CultureInfo.InvariantCulture)));

        public static long? Decode(string cursor)
        {
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
                return text.StartsWith(Prefix, StringComparison.Ordinal)
                       && long.TryParse(text.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                    ? seq
                    : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
