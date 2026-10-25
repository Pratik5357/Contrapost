using System.Text;
using System.Text.Json;
using Contrapost.Api.Auth;
using Contrapost.Application.Idempotency;
using Contrapost.Domain;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Contrapost.Api.Http;

/// <summary>
/// Endpoint metadata: where a replayed 201's resource lives, as <c>{Prefix}{id}</c>. Without it the
/// Location is <c>{request path}/{id}</c>, which fits collection POSTs such as <c>/v1/transfers</c>.
/// </summary>
internal sealed record ReplayLocation(string Prefix);

/// <summary>
/// Requires an <c>Idempotency-Key</c> header and applies the rules in design doc §7.3. The endpoint
/// runs inside the transaction that claimed the key; its response is stored and committed with the
/// ledger writes. <typeparamref name="TRequest"/> is the endpoint's bound body, re-serialized to
/// get a canonical form for the request hash; an omitted optional body hashes as <c>null</c>.
/// </summary>
internal sealed class IdempotencyFilter<TRequest>(IIdempotencyScope scope, IOptions<JsonOptions> jsonOptions) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var json = jsonOptions.Value.SerializerOptions;

        string? key = http.Request.Headers[HeaderName];
        if (string.IsNullOrWhiteSpace(key) || key.Length > IdempotencyKey.MaxLength)
        {
            return LedgerError.Validation(new Dictionary<string, string[]>
            {
                [HeaderName] = [$"Required on this endpoint; at most {IdempotencyKey.MaxLength} characters."],
            }).ToProblem();
        }

        var body = JsonSerializer.Serialize(context.Arguments.OfType<TRequest>().SingleOrDefault(), json);
        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? "";
        var claim = await scope.BeginAsync(
            new IdempotencyKey(http.GetClientId(), key, method, path, IdempotencyKey.HashRequest(method, path, body)),
            http.RequestAborted);

        switch (claim)
        {
            case ClaimResult.Replay replay:
                return new ReplayResult(replay.Response, http.GetEndpoint()?.Metadata.GetMetadata<ReplayLocation>());
            case ClaimResult.KeyReused:
                return new LedgerError(ErrorKind.Rejected, "idempotency-key-reused", "Idempotency key reused",
                    "This idempotency key was already used for a different request.").ToProblem();
            case ClaimResult.InProgress:
                http.Response.Headers.RetryAfter = "1";
                return new LedgerError(ErrorKind.Conflict, "request-in-progress", "Request in progress",
                    "A request with this idempotency key is still being processed. Retry shortly.").ToProblem();
        }

        var result = await next(context);

        // Store every outcome the ledger reached, including business rejections, so a retry gets
        // the same answer. Validation failures (400) are not stored; the transaction rolls back.
        if (result is IStatusCodeHttpResult { StatusCode: { } status } && status != StatusCodes.Status400BadRequest && status < 500)
        {
            var stored = result switch
            {
                IValueHttpResult { Value: { } value } => JsonSerializer.Serialize(value, value.GetType(), json),
                _ => "null",
            };
            await scope.CompleteAsync(new StoredResponse(status, stored), http.RequestAborted);
        }

        return result;
    }

    /// <summary>Writes a stored response with <c>Idempotent-Replayed: true</c>.</summary>
    private sealed class ReplayResult(StoredResponse response, ReplayLocation? location) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var http = httpContext.Response;
            http.StatusCode = response.StatusCode;
            http.Headers["Idempotent-Replayed"] = "true";
            http.ContentType = response.StatusCode >= 400 ? "application/problem+json" : "application/json; charset=utf-8";

            if (response.StatusCode == StatusCodes.Status201Created)
            {
                using var document = JsonDocument.Parse(response.Body);
                if (document.RootElement.TryGetProperty("id", out var id))
                {
                    var prefix = location?.Prefix ?? $"{httpContext.Request.Path.Value?.TrimEnd('/')}/";
                    http.Headers.Location = prefix + id.GetString();
                }
            }

            await http.WriteAsync(response.Body, Encoding.UTF8, httpContext.RequestAborted);
        }
    }
}
