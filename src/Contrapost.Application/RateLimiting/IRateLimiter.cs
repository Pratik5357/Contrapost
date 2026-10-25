namespace Contrapost.Application.RateLimiting;

/// <param name="Allowed">Whether the request may proceed.</param>
/// <param name="Remaining">Requests left in the current window after this one.</param>
/// <param name="RetryAfter">When <see cref="Allowed"/> is false, how long until a slot frees up.</param>
/// <param name="FailedOpen">True when the limiter couldn't be consulted and let the request through.</param>
public sealed record RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter, bool FailedOpen = false);

/// <summary>Per-client sliding-window rate limiting (design doc §11).</summary>
public interface IRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(Guid clientId, int requestsPerMinute, CancellationToken cancellationToken);
}
