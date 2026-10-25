using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Contrapost.Application.Observability;

/// <summary>
/// The ledger's own metrics (design doc §12). Instrument names use OpenTelemetry conventions; the
/// Prometheus exporter turns them into <c>ledger_transfers_total</c>, <c>ledger_transfer_duration_seconds</c>, and so on.
/// </summary>
public sealed class LedgerMetrics
{
    public const string MeterName = "Contrapost";

    // Seconds; resolution where ledger writes live (1 ms to 1 s), with a tail for contention.
    private static readonly double[] LatencyBuckets = [0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly Counter<long> _transfers;
    private readonly Histogram<double> _transferDuration;
    private readonly Histogram<double> _lockWait;
    private readonly Counter<long> _idempotentReplays;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _rateLimiterFailedOpen;

    public LedgerMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(MeterName);
        var latency = new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets };

        _transfers = meter.CreateCounter<long>("ledger.transfers", "{transfer}", "Transfer requests by result: posted, insufficient_funds, rejected.");
        _transferDuration = meter.CreateHistogram("ledger.transfer.duration", "s", "Time to post or reject a transfer.", advice: latency);
        _lockWait = meter.CreateHistogram("ledger.lock.wait", "s", "Time spent acquiring balance row locks.", advice: latency);
        _idempotentReplays = meter.CreateCounter<long>("ledger.idempotent_replays", "{request}", "Requests answered from a stored idempotent response.");
        _rateLimited = meter.CreateCounter<long>("ledger.rate_limited", "{request}", "Requests rejected by the rate limiter.");
        _rateLimiterFailedOpen = meter.CreateCounter<long>("ledger.rate_limiter.failed_open", "{request}", "Requests let through because the rate limiter was unavailable.");
    }

    public void RecordTransfer(string result, TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>("result", result);
        _transfers.Add(1, tag);
        _transferDuration.Record(duration.TotalSeconds, tag);
    }

    public void RecordLockWait(TimeSpan wait) => _lockWait.Record(wait.TotalSeconds);

    public void RecordIdempotentReplay() => _idempotentReplays.Add(1);

    public void RecordRateLimited() => _rateLimited.Add(1);

    public void RecordRateLimiterFailedOpen() => _rateLimiterFailedOpen.Add(1);

    /// <summary>Times an operation: <c>var started = LedgerMetrics.Start(); ... LedgerMetrics.Elapsed(started)</c>.</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);
}
