using Contrapost.Application.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Contrapost.Infrastructure.RateLimiting;

/// <summary>
/// A sliding-window log per client in a Redis sorted set: one member per request, scored by time.
/// The Lua script trims, counts and adds atomically, and reads the clock from Redis, so several API
/// instances share one window regardless of their own clocks.
/// If Redis can't be reached the limiter fails open (ADR-006): requests proceed, a warning is logged.
/// </summary>
internal sealed partial class RedisRateLimiter(IConnectionMultiplexer redis, ILogger<RedisRateLimiter> logger) : IRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Returns {allowed, remaining, retry_after_ms}.
    private static readonly LuaScript Script = LuaScript.Prepare("""
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local window = tonumber(@windowMs)
        local limit = tonumber(@limit)
        redis.call('ZREMRANGEBYSCORE', @key, '-inf', now - window)
        local count = redis.call('ZCARD', @key)
        if count < limit then
            redis.call('ZADD', @key, now, now .. '-' .. @nonce)
            redis.call('PEXPIRE', @key, window)
            return {1, limit - count - 1, 0}
        end
        local oldest = redis.call('ZRANGE', @key, 0, 0, 'WITHSCORES')
        return {0, 0, tonumber(oldest[2]) + window - now}
        """);

    public async Task<RateLimitDecision> AcquireAsync(Guid clientId, int requestsPerMinute, CancellationToken cancellationToken)
    {
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script, new
            {
                key = (RedisKey)$"ratelimit:{clientId:N}",
                windowMs = (long)Window.TotalMilliseconds,
                limit = requestsPerMinute,
                nonce = Guid.NewGuid().ToString("N"),
            }))!;

            return new RateLimitDecision((int)result[0] == 1, (int)result[1], TimeSpan.FromMilliseconds(Math.Max(0, (long)result[2])));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            LogFailedOpen(clientId, ex.Message);
            return new RateLimitDecision(Allowed: true, Remaining: requestsPerMinute, TimeSpan.Zero, FailedOpen: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limiter unavailable, allowing request for client {ClientId}: {Reason}")]
    private partial void LogFailedOpen(Guid clientId, string reason);
}
