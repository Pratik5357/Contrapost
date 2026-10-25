using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contrapost.Application.Clients;
using Npgsql;
using static Contrapost.IntegrationTests.LedgerApi;

namespace Contrapost.IntegrationTests;

/// <summary>Reconciliation (design doc §8.3): clean after real activity, and loud when state is corrupted.</summary>
[Collection("api")]
public sealed class ReconciliationTests(ApiFixture api)
{
    private sealed record Run(Guid Id, string Status, int? AccountsChecked, int? FindingsCount);

    private sealed record Finding(long Id, string CheckName, Guid? SubjectId, JsonElement? Expected, JsonElement? Actual);

    [Fact]
    public async Task Run_is_clean_after_transfers_holds_captures_voids_and_reversals()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 10_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var shop = await http.CreateAccountAsync("merchant:shop");
        var payment = (await (await http.PostTransferAsync("INR", (alice.Id, "debit", 1_000), (bob.Id, "credit", 1_000)))
            .Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;
        (await http.ReverseAsync(payment.Id)).EnsureSuccessStatusCode();
        var captured = await http.CreateHoldAsync(alice.Id, shop.Id, 2_000);
        (await http.CaptureAsync(captured.Id, 500)).EnsureSuccessStatusCode();
        var voided = await http.CreateHoldAsync(alice.Id, shop.Id, 700);
        (await http.VoidAsync(voided.Id)).EnsureSuccessStatusCode();
        await http.CreateHoldAsync(alice.Id, shop.Id, 300); // left open

        var run = await api.ReconcileAsync();

        Assert.NotNull(run);
        Assert.Equal(("clean", 0), (run.Status, run.FindingsCount));
        Assert.True(run.AccountsChecked >= 4);
    }

    [Fact]
    public async Task Corrupted_state_is_reported_with_expected_and_actual_values()
    {
        using var http = await api.NewClientAsync();
        var alice = await http.FundedWalletAsync("wallet:alice", 10_000);
        var bob = await http.CreateAccountAsync("wallet:bob");
        var payment = (await (await http.PostTransferAsync("INR", (alice.Id, "debit", 1_000), (bob.Id, "credit", 1_000)))
            .Content.ReadFromJsonAsync<Transfer>(ApiFixture.Json))!;

        // Corruptions the database guards don't prevent (I1, I4 and I8 are enforced by constraints).
        await api.ScalarAsync<int>("UPDATE account_balances SET credits_posted = credits_posted + 5 WHERE account_id = @a RETURNING 1", ("a", bob.Id));
        await api.ScalarAsync<int>("UPDATE account_balances SET debits_pending = debits_pending + 7 WHERE account_id = @a RETURNING 1", ("a", alice.Id));
        var duplicateEvent = await api.ScalarAsync<Guid>(
            "INSERT INTO outbox_messages (id, aggregate_id, type, payload) VALUES (gen_random_uuid(), @t, 'transfer.posted', '{}') RETURNING id",
            ("t", payment.Id));

        try
        {
            using var admin = await api.NewClientAsync(Scopes.AdminRead);
            var run = await api.ReconcileAsync();
            Assert.Equal("drift_found", run!.Status);

            var findings = (await admin.GetFromJsonAsync<List<Finding>>($"/v1/admin/reconciliation/runs/{run.Id}/findings", ApiFixture.Json))!;
            var drift = findings.Single(f => f.CheckName == "I3_balance_drift" && f.SubjectId == bob.Id);
            Assert.Equal(1_000, drift.Expected!.Value.GetProperty("credits_posted").GetInt64());
            Assert.Equal(1_005, drift.Actual!.Value.GetProperty("credits_posted").GetInt64());
            Assert.Contains(findings, f => f.CheckName == "I5_pending_drift" && f.SubjectId == alice.Id);
            Assert.Contains(findings, f => f.CheckName == "I7_transaction_event_count" && f.SubjectId == payment.Id);
            Assert.Contains(findings, f => f.CheckName == "I2_currency_not_zero_sum");

            var runs = (await admin.GetFromJsonAsync<List<Run>>("/v1/admin/reconciliation/runs?limit=5", ApiFixture.Json))!;
            Assert.Equal(run.Id, runs[0].Id);
            Assert.Equal(findings.Count, runs[0].FindingsCount);
        }
        finally
        {
            // Undo, so later reconciliation in this collection sees a consistent ledger again.
            await api.ScalarAsync<int>("UPDATE account_balances SET credits_posted = credits_posted - 5 WHERE account_id = @a RETURNING 1", ("a", bob.Id));
            await api.ScalarAsync<int>("UPDATE account_balances SET debits_pending = debits_pending - 7 WHERE account_id = @a RETURNING 1", ("a", alice.Id));
            await api.ScalarAsync<int>("DELETE FROM outbox_messages WHERE id = @id RETURNING 1", ("id", duplicateEvent));
        }

        Assert.Equal("clean", (await api.ReconcileAsync())!.Status);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(x'636f6e7472610001'::bigint)", connection))
        {
            await hold.ExecuteNonQueryAsync();
        }

        try
        {
            Assert.Null(await api.ReconcileAsync());
        }
        finally
        {
            // Session locks survive a pooled connection's return until it is reused, so release explicitly.
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(x'636f6e7472610001'::bigint)", connection);
            await release.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Admin_endpoints_require_the_admin_scope()
    {
        using var ordinary = await api.NewClientAsync(Scopes.AccountsRead, Scopes.TransfersWrite);
        using var admin = await api.NewClientAsync(Scopes.AdminRead);

        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(new Uri("/v1/admin/reconciliation/runs", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/v1/admin/reconciliation/runs", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync(new Uri($"/v1/admin/reconciliation/runs/{Guid.NewGuid()}/findings", UriKind.Relative))).StatusCode);
    }
}
