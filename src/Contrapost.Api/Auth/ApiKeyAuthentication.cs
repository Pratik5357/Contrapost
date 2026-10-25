using System.Security.Claims;
using System.Text.Encodings.Web;
using Contrapost.Application.Clients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Contrapost.Api.Auth;

/// <summary>Authenticates <c>Authorization: Bearer cp_&lt;env&gt;_&lt;prefix&gt;.&lt;secret&gt;</c> (design doc §11).</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiClientStore clients,
    IProblemDetailsService problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ClientIdClaim = "client_id";
    public const string ScopeClaim = "scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            || !ApiKeys.TryParse(header[bearer.Length..].Trim(), out var prefix, out var secret))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var client = await clients.FindByPrefixAsync(prefix, Context.RequestAborted);
        if (client is null || !ApiKeys.Matches(secret, client.KeyHash))
        {
            return AuthenticateResult.Fail("Unknown, revoked or wrong API key.");
        }

        var claims = client.Scopes
            .Select(scope => new Claim(ScopeClaim, scope))
            .Prepend(new Claim(ClientIdClaim, client.ClientId.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await WriteProblemAsync("unauthenticated", "Unauthenticated", "Send a valid API key: Authorization: Bearer cp_<env>_<prefix>.<secret>");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await WriteProblemAsync("insufficient-scope", "Insufficient scope", "The API key does not have the scope this endpoint requires.");
    }

    private ValueTask WriteProblemAsync(string code, string title, string detail) => problems.WriteAsync(new ProblemDetailsContext
    {
        HttpContext = Context,
        ProblemDetails =
        {
            Status = Response.StatusCode,
            Type = $"https://contrapost.dev/errors/{code}",
            Title = title,
            Detail = detail,
        },
    });
}

internal static class ApiKeyAuthenticationExtensions
{
    /// <summary>API-key authentication plus one authorization policy per scope, named after the scope.</summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services)
    {
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

        var authorization = services.AddAuthorizationBuilder();
        foreach (var scope in Scopes.All)
        {
            authorization.AddPolicy(scope, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope));
        }

        return services;
    }

    /// <summary>The authenticated client. Only valid on endpoints that require authorization.</summary>
    public static Guid GetClientId(this HttpContext http) =>
        Guid.Parse(http.User.FindFirstValue(ApiKeyAuthenticationHandler.ClientIdClaim)
            ?? throw new InvalidOperationException("Request has no authenticated client."));
}
