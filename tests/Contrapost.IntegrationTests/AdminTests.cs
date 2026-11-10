using System.Net;
using System.Net.Http.Json;
using Contrapost.Application.Clients;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>The admin endpoints (design doc §5.2, §7.1): cross-tenant, read-only, keyset-paged.</summary>
[Collection("api")]
public sealed class AdminTests(ApiFixture api)
{
    private sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

    private sealed record AdminAccount(Guid ClientId, Account Account);

    private sealed record AccountDetail(Guid ClientId, Account Account, IReadOnlyList<Hold> OpenHolds, EntryPage RecentEntries);

    private sealed record AdminHold(Guid ClientId, Hold Hold);

    private sealed record OutboxMessage(Guid Id, Guid AggregateId, string Type, DateTimeOffset? PublishedAt, int Attempts, string? LastError);

    private sealed record TransactionDetail(Guid ClientId, Transfer Transaction, Hold? Hold, Guid? ReversedBy, IReadOnlyList<OutboxMessage> Events);

    private sealed record CurrencyTotals(string Currency, int Accounts, long DebitsPosted, long CreditsPosted, bool Balanced);

    private sealed record RequestCount(string Operation, int Status, int Count);

    private sealed record OutboxSummary(int Unpublished, int Failing, int OldestUnpublishedSeconds);

    private sealed record Overview(IReadOnlyList<CurrencyTotals> Currencies, IReadOnlyList<RequestCount> RequestsLastHour, OutboxSummary Outbox);

    [Fact]
    public async Task Admin_reads_require_the_admin_scope()
    {
        using var ordinary = await api.NewClientAsync(Scopes.AccountsRead, Scopes.TransfersWrite);
        using var admin = await api.NewClientAsync(Scopes.AdminRead);

        foreach (var path in new[] { "/v1/admin/overview", "/v1/admin/accounts", "/v1/admin/holds", "/v1/admin/outbox" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(new Uri(path, UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri(path, UriKind.Relative))).StatusCode);
        }
    }

    [Fact]
    public async Task Account_search_crosses_clients_filters_and_pages_without_gaps()
    {
        using var first = await api.NewTestClientAsync();
        using var second = await api.NewTestClientAsync();
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var mine = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            mine.Add((await first.Http.CreateAccountAsync($"wallet:{tag}:{i}")).Id);
        }

        var theirs = await second.Http.CreateAccountAsync($"wallet:{tag}:other", currency: "USD");
        await first.Http.CreateAccountAsync("wallet:no-match");

        // q matches across both clients; client_id and currency narrow it.
        var all = await AllPagesAsync<AdminAccount>(admin, $"/v1/admin/accounts?q={tag}&limit=1");
        Assert.Equal(mine.Append(theirs.Id).Order(), all.Select(a => a.Account.Id).Order());
        Assert.Equal(mine.Order(), all.Where(a => a.ClientId == first.ClientId).Select(a => a.Account.Id).Order());
        Assert.Equal([theirs.Id], (await AllPagesAsync<AdminAccount>(admin, $"/v1/admin/accounts?q={tag}&currency=USD")).Select(a => a.Account.Id));
        Assert.Equal(4, (await AllPagesAsync<AdminAccount>(admin, $"/v1/admin/accounts?client_id={first.ClientId}")).Count);
        Assert.Empty(await AllPagesAsync<AdminAccount>(admin, $"/v1/admin/accounts?q={tag}&status=frozen"));

        // The search is a substring match, not a LIKE pattern.
        Assert.Empty(await AllPagesAsync<AdminAccount>(admin, $"/v1/admin/accounts?client_id={first.ClientId}&q=%25"));
    }

    [Fact]
    public async Task Bad_filters_are_rejected()
    {
        using var admin = await api.NewClientAsync(Scopes.AdminRead);

        foreach (var path in new[]
        {
            "/v1/admin/accounts?status=open", "/v1/admin/accounts?currency=inr", "/v1/admin/accounts?after=nope",
            "/v1/admin/holds?status=done", "/v1/admin/holds?expiring_within=0", "/v1/admin/outbox?state=stuck",
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(new Uri(path, UriKind.Relative))).StatusCode);
        }
    }

    [Fact]
    public async Task Account_detail_shows_balances_open_holds_and_pages_its_entries()
    {
        using var client = await api.NewTestClientAsync();
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        var alice = await client.Http.FundedWalletAsync("wallet:alice", 10_000);
        var shop = await client.Http.CreateAccountAsync("merchant:shop");
        for (var i = 0; i < 24; i++)
        {
            (await client.Http.PostTransferAsync("INR", (alice.Id, "debit", 10), (shop.Id, "credit", 10))).EnsureSuccessStatusCode();
        }

        var open = await client.Http.CreateHoldAsync(alice.Id, shop.Id, 500);
        var voided = await client.Http.CreateHoldAsync(alice.Id, shop.Id, 100);
        (await client.Http.VoidAsync(voided.Id)).EnsureSuccessStatusCode();

        var detail = (await admin.GetFromJsonAsync<AccountDetail>($"/v1/admin/accounts/{alice.Id}", ApiFixture.Json))!;

        Assert.Equal(client.ClientId, detail.ClientId);
        Assert.Equal((9_760L, 9_260L), (detail.Account.Balance.Posted, detail.Account.Balance.Available));
        Assert.Equal([open.Id], detail.OpenHolds.Select(h => h.Id));
        Assert.Equal(20, detail.RecentEntries.Entries.Count);
        Assert.Equal(25, detail.RecentEntries.Entries[0].AccountSeq);

        var rest = (await admin.GetFromJsonAsync<EntryPage>(
            $"/v1/admin/accounts/{alice.Id}/entries?after={detail.RecentEntries.NextCursor}", ApiFixture.Json))!;
        Assert.Equal([5L, 4, 3, 2, 1], rest.Entries.Select(e => e.AccountSeq));
        Assert.Null(rest.NextCursor);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(new Uri($"/v1/admin/accounts/{Guid.NewGuid()}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Transaction_detail_links_reversals_holds_and_outbox_events()
    {
        using var client = await api.NewTestClientAsync();
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        var alice = await client.Http.FundedWalletAsync("wallet:alice", 10_000);
        var bob = await client.Http.CreateAccountAsync("wallet:bob");
        var payment = (await (await client.Http.PostTransferAsync("INR", (alice.Id, "debit", 1_000), (bob.Id, "credit", 1_000)))
            .Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
        var reversal = (await (await client.Http.ReverseAsync(payment.Id)).Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
        var hold = await client.Http.CreateHoldAsync(alice.Id, bob.Id, 300);
        var capture = (await (await client.Http.CaptureAsync(hold.Id, 200)).Content.ReadFromJsonAsync<Capture>(ApiFixture.Json))!;

        var paid = (await admin.GetFromJsonAsync<TransactionDetail>($"/v1/admin/transactions/{payment.Id}", ApiFixture.Json))!;
        Assert.Equal((client.ClientId, reversal.Id), (paid.ClientId, paid.ReversedBy));
        Assert.Equal(2, paid.Transaction.Legs.Count);
        Assert.Equal(["transfer.posted"], paid.Events.Select(e => e.Type));

        var reversed = (await admin.GetFromJsonAsync<TransactionDetail>($"/v1/admin/transactions/{reversal.Id}", ApiFixture.Json))!;
        Assert.Equal((payment.Id, (Guid?)null), (reversed.Transaction.ReversesTransactionId, reversed.ReversedBy));
        Assert.Equal(["transfer.reversed"], reversed.Events.Select(e => e.Type));

        var captured = (await admin.GetFromJsonAsync<TransactionDetail>($"/v1/admin/transactions/{capture.Transaction.Id}", ApiFixture.Json))!;
        Assert.Equal((hold.Id, "partially_captured", 200L), (captured.Hold!.Id, captured.Hold.Status, captured.Hold.CapturedAmount));
        Assert.Equal(["hold.captured"], captured.Events.Select(e => e.Type));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(new Uri($"/v1/admin/transactions/{Guid.NewGuid()}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Holds_can_be_listed_by_status_and_upcoming_expiry()
    {
        using var client = await api.NewTestClientAsync();
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        var alice = await client.Http.FundedWalletAsync("wallet:alice", 10_000);
        var shop = await client.Http.CreateAccountAsync("merchant:shop");
        var soon = await client.Http.CreateHoldAsync(alice.Id, shop.Id, 100, expiresInSeconds: 120);
        var later = await client.Http.CreateHoldAsync(alice.Id, shop.Id, 100, expiresInSeconds: 3_600);
        var voided = await client.Http.CreateHoldAsync(alice.Id, shop.Id, 100, expiresInSeconds: 60);
        (await client.Http.VoidAsync(voided.Id)).EnsureSuccessStatusCode();

        var expiring = (await AllPagesAsync<AdminHold>(admin, "/v1/admin/holds?expiring_within=600&limit=200"))
            .Where(h => h.ClientId == client.ClientId).Select(h => h.Hold.Id).ToList();
        Assert.Equal([soon.Id], expiring);

        var voidedHolds = (await AllPagesAsync<AdminHold>(admin, "/v1/admin/holds?status=voided&limit=200"))
            .Where(h => h.ClientId == client.ClientId).Select(h => h.Hold.Id).ToList();
        Assert.Equal([voided.Id], voidedHolds);
        Assert.DoesNotContain(later.Id, voidedHolds);
    }

    [Fact]
    public async Task Outbox_lists_unpublished_and_failing_messages()
    {
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        var aggregate = Guid.NewGuid();
        var failing = await api.ScalarAsync<Guid>(
            """
            INSERT INTO outbox_messages (id, aggregate_id, type, payload, attempts, last_error, next_attempt_at)
            VALUES (gen_random_uuid(), @a, 'test.failing', '{}', 3, 'broker nacked', now() + interval '1 minute') RETURNING id
            """, ("a", aggregate));
        var waiting = await api.ScalarAsync<Guid>(
            "INSERT INTO outbox_messages (id, aggregate_id, type, payload) VALUES (gen_random_uuid(), @a, 'test.waiting', '{}') RETURNING id",
            ("a", aggregate));
        var published = await api.ScalarAsync<Guid>(
            "INSERT INTO outbox_messages (id, aggregate_id, type, payload, published_at) VALUES (gen_random_uuid(), @a, 'test.done', '{}', now()) RETURNING id",
            ("a", aggregate));

        var unpublished = (await AllPagesAsync<OutboxMessage>(admin, "/v1/admin/outbox?limit=200")).Where(m => m.AggregateId == aggregate).ToList();
        Assert.Equal(new[] { failing, waiting }.Order(), unpublished.Select(m => m.Id).Order());
        Assert.DoesNotContain(published, unpublished.Select(m => m.Id));

        var failingOnly = (await AllPagesAsync<OutboxMessage>(admin, "/v1/admin/outbox?state=failing&limit=200")).Where(m => m.AggregateId == aggregate).ToList();
        Assert.Equal([(failing, 3, "broker nacked")], failingOnly.Select(m => (m.Id, m.Attempts, m.LastError)));

        await api.ScalarAsync<int>("DELETE FROM outbox_messages WHERE aggregate_id = @a RETURNING 1", ("a", aggregate));
    }

    [Fact]
    public async Task Overview_shows_balanced_totals_recent_requests_and_the_outbox()
    {
        using var client = await api.NewTestClientAsync();
        using var admin = await api.NewClientAsync(Scopes.AdminRead);
        // A currency no other test uses, so its totals are this test's alone.
        var currency = "Q" + (char)('A' + Random.Shared.Next(26)) + (char)('A' + Random.Shared.Next(26));
        var wallet = await client.Http.FundedWalletAsync("wallet:alice", 5_000, currency);
        var other = await client.Http.CreateAccountAsync("wallet:bob", currency);
        var overdraft = await client.Http.PostTransferAsync(currency, (wallet.Id, "debit", 9_000), (other.Id, "credit", 9_000));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overdraft.StatusCode);

        var overview = (await admin.GetFromJsonAsync<Overview>("/v1/admin/overview", ApiFixture.Json))!;

        Assert.Equal(new CurrencyTotals(currency, 3, 5_000, 5_000, true), overview.Currencies.Single(c => c.Currency == currency));
        Assert.Contains(overview.RequestsLastHour, r => r is { Operation: "transfer", Status: 201, Count: > 0 });
        Assert.Contains(overview.RequestsLastHour, r => r is { Operation: "transfer", Status: 422, Count: > 0 });
        Assert.True(overview.Outbox.Unpublished > 0); // the test host runs no relay
    }

    /// <summary>Follows next_cursor to the end, checking that pages never repeat an item.</summary>
    private static async Task<List<T>> AllPagesAsync<T>(HttpClient http, string path)
    {
        var items = new List<T>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        string? cursor = null;
        do
        {
            var page = (await http.GetFromJsonAsync<Page<T>>(cursor is null ? path : $"{path}{separator}after={cursor}", ApiFixture.Json))!;
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(items.Count, items.Distinct().Count());
        return items;
    }
}
