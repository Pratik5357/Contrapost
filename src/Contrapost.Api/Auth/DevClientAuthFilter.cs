using Contrapost.Application.Clients;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Contrapost.Api.Auth;

/// <summary>
/// TEMPORARY (M1 only): identifies the caller from an <c>X-Client-Id</c> header so the ledger
/// endpoints can be built and tested before API keys exist. Enabled only when
/// <c>Auth:DevClientHeader</c> is true (Development and tests). M2 replaces this with
/// API-key authentication and scopes (design doc §11).
/// </summary>
internal sealed class DevClientAuthFilter(IConfiguration configuration, IClientDirectory clients) : IEndpointFilter
{
    public const string HeaderName = "X-Client-Id";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!configuration.GetValue<bool>("Auth:DevClientHeader"))
        {
            return Unauthorized("Authentication is not configured for this environment.");
        }

        if (!Guid.TryParse(http.Request.Headers[HeaderName], out var clientId)
            || !await clients.IsActiveAsync(clientId, http.RequestAborted))
        {
            return Unauthorized($"Send a valid, active client id in the {HeaderName} header.");
        }

        http.SetClientId(clientId);
        return await next(context);
    }

    private static ProblemHttpResult Unauthorized(string detail) => TypedResults.Problem(
        detail,
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Unauthenticated",
        type: "https://contrapost.dev/errors/unauthenticated");
}

internal static class ClientContext
{
    private const string Key = "contrapost.client_id";

    public static void SetClientId(this HttpContext http, Guid clientId) => http.Items[Key] = clientId;

    /// <summary>The authenticated client. Only valid behind <see cref="DevClientAuthFilter"/>.</summary>
    public static Guid GetClientId(this HttpContext http) =>
        http.Items[Key] as Guid? ?? throw new InvalidOperationException("Request has no authenticated client.");
}
