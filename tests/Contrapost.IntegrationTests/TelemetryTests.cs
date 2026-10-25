using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Contrapost.Infrastructure.Messaging;
using Contrapost.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>Metrics and end-to-end tracing (design doc §12).</summary>
[Collection("api")]
public sealed class TelemetryTests(ApiFixture api)
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string IncomingTraceparent = $"00-{TraceId}-00f067aa0ba902b7-01";

    [Fact]
    public async Task Transfers_are_counted_by_result_and_lock_waits_are_measured()
    {
        var measurements = new ConcurrentBag<(string Instrument, double Value, string? Result)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Contrapost")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => measurements.Add((i.Name, v, Result(tags))));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => measurements.Add((i.Name, v, Result(tags))));
        listener.Start();

        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        (await http.PostTransferAsync("INR", (alice.Id, "debit", 400), (bob.Id, "credit", 400))).EnsureSuccessStatusCode();
        await http.PostTransferAsync("INR", (alice.Id, "debit", 9_999), (bob.Id, "credit", 9_999));

        Assert.Contains(measurements, m => m is { Instrument: "ledger.transfers", Result: "posted" });
        Assert.Contains(measurements, m => m is { Instrument: "ledger.transfers", Result: "insufficient_funds" });
        Assert.Contains(measurements, m => m.Instrument == "ledger.transfer.duration" && m.Value > 0);
        Assert.Contains(measurements, m => m.Instrument == "ledger.lock.wait");
    }

    [Fact]
    public async Task One_transfer_is_one_trace_from_the_request_through_the_relay_to_the_consumers()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 1_000);
        var bob = await http.CreateAccountAsync("wallet:bob");

        // The client sends a W3C trace context; the API continues it.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/transfers", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                currency = "INR",
                legs = new object[]
                {
                    new { account_id = alice.Id, direction = "debit", amount = 250 },
                    new { account_id = bob.Id, direction = "credit", amount = 250 },
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("traceparent", IncomingTraceparent);
        var transfer = (await (await http.SendAsync(request)).Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;

        var stored = await api.ScalarAsync<string>(
            "SELECT traceparent FROM outbox_messages WHERE aggregate_id = @id AND type = 'transfer.posted'", ("id", transfer.Id));
        Assert.Equal(TraceId, stored.Split('-')[1]);

        await using var services = api.MessagingServices();
        using var consumers = await api.StartConsumersAsync();
        var relay = services.GetRequiredService<OutboxRelay>();
        while ((await relay.RelayOnceAsync(500, CancellationToken.None)).Claimed > 0)
        {
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (spans.Count(s => s.TraceId.ToHexString() == TraceId && s.Kind == ActivityKind.Consumer) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var trace = spans.Where(s => s.TraceId.ToHexString() == TraceId).ToList();
        Assert.Contains(trace, s => s is { Kind: ActivityKind.Producer, DisplayName: "transfer.posted publish" });
        Assert.Equal(2, trace.Count(s => s is { Kind: ActivityKind.Consumer, DisplayName: "transfer.posted process" })); // statements and notifier
        await consumers.StopAsync();
    }

    private static string? Result(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "result")
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}
