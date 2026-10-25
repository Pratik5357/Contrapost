using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Contrapost.Consumers;
using Contrapost.Consumers.Handlers;
using Contrapost.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>Outbox relay, broker topology and the consumers (design doc §9).</summary>
[Collection("api")]
public sealed class MessagingTests(ApiFixture api)
{
    [Fact]
    public async Task Relay_publishes_outbox_rows_with_confirms_and_marks_them_published()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var transfer = (await (await http.PostTransferAsync("INR", (alice.Id, "debit", 1_200), (bob.Id, "credit", 1_200)))
            .Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;

        await using var services = api.MessagingServices();
        var broker = services.GetRequiredService<RabbitMqConnection>();
        await using var probe = await broker.CreateChannelAsync(confirms: false, CancellationToken.None);
        var queue = (await probe.QueueDeclareAsync(exclusive: true)).QueueName;
        await probe.QueueBindAsync(queue, Topology.Exchange, "transfer.posted");

        await DrainOutboxAsync(services);

        var envelope = await FindAsync(probe, queue, e => e.Subject == transfer.Id);
        Assert.Equal("transfer.posted", envelope.Type);
        Assert.Equal(EventEnvelope.ApiSource, envelope.Source);
        var legs = envelope.Data.GetProperty("legs").EnumerateArray().ToList();
        var aliceLeg = legs.Single(l => l.GetProperty("account_id").GetGuid() == alice.Id);
        Assert.Equal(2, aliceLeg.GetProperty("account_seq").GetInt64());
        Assert.Equal("credit", aliceLeg.GetProperty("normal_side").GetString());
        Assert.Equal(3_800, aliceLeg.GetProperty("balance_after").GetInt64());

        Assert.Equal(1, await api.ScalarAsync<int>(
            "SELECT attempts FROM outbox_messages WHERE id = @id AND published_at IS NOT NULL", ("id", envelope.Id)));
    }

    [Fact]
    public async Task Broker_outage_leaves_outbox_rows_untouched()
    {
        using var http = await api.NewClientAsync();
        var account = await http.CreateAccountAsync("wallet:during-outage");

        await using var services = api.MessagingServices("amqp://guest:guest@localhost:1");
        await Assert.ThrowsAsync<BrokerUnavailableException>(() =>
            services.GetRequiredService<OutboxRelay>().RelayOnceAsync(100, CancellationToken.None));

        // No backoff and no attempt counted: when the broker returns, the row is due immediately.
        Assert.Equal(1, await api.ScalarAsync<long>(
            """
            SELECT count(*) FROM outbox_messages
            WHERE aggregate_id = @id AND published_at IS NULL AND attempts = 0 AND next_attempt_at <= now()
            """, ("id", account.Id)));
    }

    [Fact]
    public async Task Redelivered_event_is_projected_once()
    {
        await using var services = api.MessagingServices();
        var inbox = services.GetRequiredService<InboxProcessor>();
        var projector = services.GetRequiredService<StatementProjector>();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var envelope = Posted(Guid.NewGuid(), (a, "debit", 300, 1, "credit", 700), (b, "credit", 300, 1, "credit", 300));

        Assert.True(await inbox.ProcessAsync(projector, envelope, CancellationToken.None));
        Assert.False(await inbox.ProcessAsync(projector, envelope, CancellationToken.None));

        Assert.Equal(1, await api.ScalarAsync<long>(
            "SELECT count(*) FROM inbox_messages WHERE consumer = 'statements' AND message_id = @id", ("id", envelope.Id)));
        Assert.Equal(700, await api.ScalarAsync<long>(
            "SELECT balance_after FROM account_statements WHERE account_id = @a AND account_seq = 1", ("a", a)));
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT complete_through_seq FROM account_statement_heads WHERE account_id = @a", ("a", a)));
    }

    [Fact]
    public async Task Statement_is_complete_only_through_a_gapless_run_of_entries()
    {
        await using var services = api.MessagingServices();
        var inbox = services.GetRequiredService<InboxProcessor>();
        var projector = services.GetRequiredService<StatementProjector>();
        var (account, other) = (Guid.NewGuid(), Guid.NewGuid());

        // Entries 2 and 3 arrive before entry 1, as they can with several relays.
        await inbox.ProcessAsync(projector, Posted(Guid.NewGuid(), (account, "credit", 20, 2, "credit", 30), (other, "debit", 20, 1, "debit", 20)), default);
        await inbox.ProcessAsync(projector, Posted(Guid.NewGuid(), (account, "credit", 5, 3, "credit", 35), (other, "debit", 5, 2, "debit", 25)), default);
        Assert.Equal(0, await HeadAsync(account));

        await inbox.ProcessAsync(projector, Posted(Guid.NewGuid(), (account, "credit", 10, 1, "credit", 10), (other, "debit", 10, 3, "debit", 35)), default);
        Assert.Equal(3, await HeadAsync(account));
        Assert.Equal(3, await HeadAsync(other));
    }

    [Fact]
    public async Task Low_balance_alert_is_raised_once_per_event()
    {
        await using var services = api.MessagingServices();
        var inbox = services.GetRequiredService<InboxProcessor>();
        var notifier = services.GetRequiredService<LowBalanceNotifier>();
        var (wallet, shop) = (Guid.NewGuid(), Guid.NewGuid());
        // Both end below the threshold of 1000, but only the wallet's balance went down.
        var envelope = Posted(Guid.NewGuid(), (wallet, "debit", 4_500, 7, "credit", 500), (shop, "credit", 4_500, 3, "credit", 900));

        await inbox.ProcessAsync(notifier, envelope, default);
        await inbox.ProcessAsync(notifier, envelope, default);

        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM low_balance_alerts WHERE account_id = @a", ("a", wallet)));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM low_balance_alerts WHERE account_id = @a", ("a", shop)));
    }

    [Fact]
    public async Task Events_flow_from_the_api_through_the_broker_into_both_consumers()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 5_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        (await http.PostTransferAsync("INR", (alice.Id, "debit", 4_600), (bob.Id, "credit", 4_600))).EnsureSuccessStatusCode();

        await using var services = api.MessagingServices();
        using var consumers = await api.StartConsumersAsync();
        await DrainOutboxAsync(services);

        await EventuallyAsync(async () => await api.ScalarAsync<long>(
            "SELECT count(*) FROM account_statements WHERE account_id = @a", ("a", alice.Id)) == 2);
        Assert.Equal(400, await api.ScalarAsync<long>(
            "SELECT balance_after FROM account_statements WHERE account_id = @a AND account_seq = 2", ("a", alice.Id)));
        Assert.Equal(2, await HeadAsync(alice.Id));
        await EventuallyAsync(async () => await api.ScalarAsync<long>(
            "SELECT count(*) FROM low_balance_alerts WHERE account_id = @a", ("a", alice.Id)) == 1);

        await consumers.StopAsync();
    }

    [Fact]
    public async Task Malformed_and_poison_messages_end_up_in_the_dead_letter_queue()
    {
        await using var services = api.MessagingServices();
        var broker = services.GetRequiredService<RabbitMqConnection>();
        await using var channel = await broker.CreateChannelAsync(confirms: true, CancellationToken.None);
        await channel.QueuePurgeAsync(Topology.Statements.DeadLetterQueue);
        using var consumers = await api.StartConsumersAsync();

        // Not an envelope at all: dead-lettered on first delivery.
        await channel.BasicPublishAsync(Topology.Exchange, "transfer.posted", mandatory: false,
            new BasicProperties { MessageId = "garbage" }, Encoding.UTF8.GetBytes("not json"));
        // A valid envelope the handler can't process: requeued until the delivery limit, then dead-lettered.
        var poison = new EventEnvelope(Guid.NewGuid(), "transfer.posted", EventEnvelope.ApiSource, Guid.NewGuid(), DateTimeOffset.UtcNow,
            JsonDocument.Parse("""{"legs":[]}""").RootElement);
        await channel.BasicPublishAsync(Topology.Exchange, "transfer.posted", mandatory: false,
            new BasicProperties { MessageId = poison.Id.ToString() }, poison.ToBytes());

        var deadLetters = new List<string?>();
        await EventuallyAsync(async () =>
        {
            while (await channel.BasicGetAsync(Topology.Statements.DeadLetterQueue, autoAck: true) is { } message)
            {
                deadLetters.Add(message.BasicProperties.MessageId);
            }

            return deadLetters.Count >= 2;
        }, timeout: TimeSpan.FromSeconds(30));

        Assert.Contains("garbage", deadLetters);
        Assert.Contains(poison.Id.ToString(), deadLetters);
        Assert.Equal(0, await api.ScalarAsync<long>(
            "SELECT count(*) FROM inbox_messages WHERE message_id = @id", ("id", poison.Id)));

        await consumers.StopAsync();
    }

    private Task<long> HeadAsync(Guid accountId) =>
        api.ScalarAsync<long>("SELECT complete_through_seq FROM account_statement_heads WHERE account_id = @a", ("a", accountId));

    private static async Task DrainOutboxAsync(IServiceProvider services)
    {
        var relay = services.GetRequiredService<OutboxRelay>();
        while ((await relay.RelayOnceAsync(500, CancellationToken.None)).Claimed > 0)
        {
        }
    }

    private static async Task<EventEnvelope> FindAsync(IChannel channel, string queue, Func<EventEnvelope, bool> match)
    {
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            var envelope = EventEnvelope.Parse(message.Body.Span);
            if (match(envelope))
            {
                return envelope;
            }
        }

        throw new Xunit.Sdk.XunitException("Expected event was not published.");
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("Condition was not met in time.");
            }

            await Task.Delay(100);
        }
    }

    private static EventEnvelope Posted(Guid transactionId, params (Guid Account, string Direction, long Amount, long Seq, string NormalSide, long BalanceAfter)[] legs)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            transaction_id = transactionId,
            legs = legs.Select(l => new
            {
                account_id = l.Account,
                direction = l.Direction,
                amount = l.Amount,
                account_seq = l.Seq,
                normal_side = l.NormalSide,
                balance_after = l.BalanceAfter,
            }),
        });
        return new EventEnvelope(Guid.NewGuid(), "transfer.posted", EventEnvelope.ApiSource, transactionId, DateTimeOffset.UtcNow, data);
    }
}
