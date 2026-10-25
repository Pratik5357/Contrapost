using System.Globalization;
using System.Security.Claims;
using Contrapost.Api.Auth;
using Contrapost.Application.Observability;
using Contrapost.Application.RateLimiting;

namespace Contrapost.Api.Http;

/// <summary>
/// Applies each client's <c>rate_limit_rpm</c> (design doc §11). It sits on the /v1 group, so it runs
/// after authentication and before endpoint filters: a limited request never claims an idempotency key.
/// </summary>
internal sealed class RateLimitFilter(IRateLimiter limiter, LedgerMetrics metrics) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!int.TryParse(http.User.FindFirstValue(ApiKeyAuthenticationHandler.RateLimitClaim), CultureInfo.InvariantCulture, out var limit))
        {
            return await next(context); // not an API-key request; authorization has already decided
        }

        var decision = await limiter.AcquireAsync(http.GetClientId(), limit, http.RequestAborted);
        http.Response.Headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.FailedOpen)
        {
            metrics.RecordRateLimiterFailedOpen();
        }

        if (decision.Allowed)
        {
            return await next(context);
        }

        metrics.RecordRateLimited();

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            $"Rate limit of {limit} requests per minute exceeded. Retry in {seconds}s.",
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            type: "https://contrapost.dev/errors/rate-limited");
    }
}
