using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Webhooks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-266 §3: the webhook outbox — each delivery is claimed by one sender at a time, a sender that dies loses its
/// claim when the lease lapses, a webhook is deactivated by the failures its policy allows, and the queue is bounded.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebhookOutboxTests : PostgresFixture
{
    private const string One = "graticula/test WebhookOutboxTests#1";
    private const string Two = "graticula/test WebhookOutboxTests#2";

    [Fact]
    public async Task Two_senders_claiming_at_once_take_different_deliveries_and_between_them_all()
    {
        PostgresWebhookStore store = await ReadyAsync();
        WebhookDefinition hook = await HookAsync(store, failures: 100);

        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(1, await store.EnqueuePortalAsync(["/", "/items", "/items/add"], "/items/add", $"{{\"n\":{i}}}", CancellationToken.None));
        }

        // Several rounds from each, at once, until nothing is due.
        List<Guid> byOne = [];
        List<Guid> byTwo = [];

        for (int round = 0; round < 10; round++)
        {
            Task<IReadOnlyList<ClaimedDelivery>> first = store.ClaimAsync(One, 7, TimeSpan.FromMinutes(2), CancellationToken.None);
            Task<IReadOnlyList<ClaimedDelivery>> second = store.ClaimAsync(Two, 7, TimeSpan.FromMinutes(2), CancellationToken.None);
            await Task.WhenAll(first, second);

            byOne.AddRange((await first).Select(d => d.Id));
            byTwo.AddRange((await second).Select(d => d.Id));
        }

        Assert.Empty(byOne.Intersect(byTwo));
        Assert.Equal(40, byOne.Concat(byTwo).Distinct().Count());
        Assert.Equal(40, byOne.Count + byTwo.Count);

        // Claimed and leased, nothing is due to anybody else.
        Assert.Empty(await store.ClaimAsync(Two, 50, TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.All(byOne, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Equal(hook.Id, (await store.FindAsync(hook.Id, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task A_sender_that_dies_loses_its_claim_when_the_lease_lapses_and_cannot_record_over_the_next()
    {
        PostgresWebhookStore store = await ReadyAsync();
        await HookAsync(store, failures: 100);
        await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None);

        ClaimedDelivery taken = Assert.Single(await store.ClaimAsync(One, 5, TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Equal(1, taken.Attempts);

        await ExecuteAsync("update webhook_delivery set lease_until = now() - interval '1 second' where id = @id", taken.Id);

        ClaimedDelivery again = Assert.Single(await store.ClaimAsync(Two, 5, TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Equal(taken.Id, again.Id);
        Assert.Equal(2, again.Attempts);

        // The first sender's late answer lands nowhere; the second's is the one kept.
        await store.DeliveredAsync(taken.Id, One, 200, CancellationToken.None);
        Assert.Equal("pending", (await store.RecentAsync(again.Webhook.Id, 5, CancellationToken.None))[0].Status);

        await store.DeliveredAsync(again.Id, Two, 204, CancellationToken.None);
        WebhookDeliveryRecord done = (await store.RecentAsync(again.Webhook.Id, 5, CancellationToken.None))[0];
        Assert.Equal("delivered", done.Status);
        Assert.Equal(204, done.LastStatus);
    }

    [Fact]
    public async Task A_webhook_is_deactivated_when_its_failures_reach_its_policy_and_what_waited_is_dropped()
    {
        PostgresWebhookStore store = await ReadyAsync();
        WebhookDefinition hook = await HookAsync(store, failures: 2);
        await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None);
        await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None);
        await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None);

        IReadOnlyList<ClaimedDelivery> first = await store.ClaimAsync(One, 1, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.False(await store.FailedAsync(first[0].Id, One, 500, "The receiver answered 500.", DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None));
        Assert.True((await store.FindAsync(hook.Id, CancellationToken.None))!.Active);

        IReadOnlyList<ClaimedDelivery> second = await store.ClaimAsync(One, 1, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(await store.FailedAsync(second[0].Id, One, null, "Timed out.", null, CancellationToken.None));

        WebhookDefinition off = (await store.FindAsync(hook.Id, CancellationToken.None))!;
        Assert.False(off.Active);
        Assert.Contains("2 failed deliveries", off.DeactivatedReason, StringComparison.Ordinal);

        IReadOnlyList<WebhookDeliveryRecord> recent = await store.RecentAsync(hook.Id, 10, CancellationToken.None);
        Assert.DoesNotContain(recent, d => d.Status == "pending");
        Assert.Contains(recent, d => d.Status == "dropped");
        Assert.Contains(recent, d => d.Status == "failed");

        // Off, it is told nothing; on again, it is.
        Assert.Equal(0, await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None));
        Assert.True((await store.UpdateAsync(hook.Id, new WebhookUpdate(Active: true), hook.Owner, CancellationToken.None))!.Active);
        Assert.Equal(1, await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task An_event_reaches_only_the_webhooks_that_listen_for_it()
    {
        PostgresWebhookStore store = await ReadyAsync();
        await HookAsync(store, failures: 5, events: ["/groups"]);

        Assert.Equal(0, await store.EnqueuePortalAsync(["/", "/items", "/items/add"], "/items/add", "{}", CancellationToken.None));
        Assert.Equal(1, await store.EnqueuePortalAsync(["/", "/groups", "/groups/add"], "/groups/add", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task The_queue_of_one_webhook_is_bounded()
    {
        PostgresWebhookStore store = await ReadyAsync();
        WebhookDefinition hook = await HookAsync(store, failures: 5);

        await ExecuteAsync(
            $"insert into webhook_delivery (id, webhook_id, event, body) select gen_random_uuid(), @id, 'x', '{{}}' "
            + $"from generate_series(1, {IWebhookStore.PendingBound})", hook.Id);

        Assert.Equal(1, await store.EnqueuePortalAsync(["/"], "/items/add", "{}", CancellationToken.None));
        WebhookDeliveryRecord newest = (await store.RecentAsync(hook.Id, 1, CancellationToken.None))[0];
        Assert.Equal("dropped", newest.Status);
        Assert.Contains("were waiting", newest.LastError, StringComparison.Ordinal);
    }

    private async Task<PostgresWebhookStore> ReadyAsync()
    {
        await MigrateAsync();

        // Each test's webhooks are its own: the tables are this class's schema, shared by its tests, so begin empty.
        await using NpgsqlCommand clear = DataSource.CreateCommand("delete from webhook");
        await clear.ExecuteNonQueryAsync();

        return new PostgresWebhookStore(DataSource);
    }

    private async Task<WebhookDefinition> HookAsync(PostgresWebhookStore store, int failures, string[]? events = null)
    {
        Guid owner = Guid.NewGuid();

        await using (NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')"))
        {
            command.Parameters.AddWithValue("id", owner);
            command.Parameters.AddWithValue("name", "webhook-owner-" + owner.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        return (await store.CreateAsync(
            new NewWebhook(WebhookKind.Portal, null, "hook", "https://receiver.example.com/hook", events ?? ["/"], null, null, true,
                owner, "https://gis.example.com", "application/json", "json", 3, failures, 5, "{}"),
            CancellationToken.None))!;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
