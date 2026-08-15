using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// What the catalogue answers when the platform store cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-95, answered 2026-08-15: serve public-only while blind.</b> Every one of
/// these tests exists because the alternative reading of that sentence is
/// defensible and would produce different code — and because the failure mode
/// this class introduces is *serving on a permission nobody can confirm*, which
/// is exactly the kind of thing that works in every test that does not aim at
/// it.
/// </para>
/// <para>
/// The policy is tested over a delegate rather than a database, because the
/// question is what the class decides and not whether Npgsql throws. The
/// classification of *which* failures mean blind is tested here too, since
/// getting that wrong is how a bug in our own SQL turns into a degraded mode
/// that looks deliberate.
/// </para>
/// </remarks>
public sealed class CatalogFallbackTests
{
    private static PublishedService Service(
        string name, SharingScope sharing, ServiceStatus status = ServiceStatus.Started) =>
        new(Guid.NewGuid(), name, null, "FeatureServer", null, null, sharing, status, []);

    /// <summary>A store that answers, then stops answering on command.</summary>
    private sealed class Store
    {
        public PublishedService? Answer { get; set; }

        public Exception? Failure { get; set; }

        public int Reads { get; private set; }

        public Task<PublishedService?> ReadAsync(string? folder, string name, CancellationToken t)
        {
            Reads++;

            return Failure is not null
                ? Task.FromException<PublishedService?>(Failure)
                : Task.FromResult(Answer);
        }
    }

    private static (CatalogFallback Catalog, Store Store, FakeTimeProvider Clock) Build(
        PublishedService? answer, TimeSpan? window = null)
    {
        Store store = new() { Answer = answer };
        FakeTimeProvider clock = new();

        return (new CatalogFallback(store.ReadAsync, clock, window), store, clock);
    }

    /// <summary>
    /// Npgsql's own connectivity failure, which is what a stopped database
    /// produces before the server has said anything.
    /// </summary>
    private static NpgsqlException Unreachable() =>
        new NpgsqlException("Failed to connect", new System.Net.Sockets.SocketException(10061));

    // ---------- the healthy path is unchanged ----------

    /// <summary>
    /// While the store answers, every request reads it.
    /// </summary>
    /// <remarks>
    /// <b>This is the load-bearing property of the whole design.</b> If this
    /// class became a read-through cache, revocation would stop being instant
    /// and every reason the sharing model is trustworthy would weaken — for a
    /// performance gain nobody asked for. D-17 records that reading the
    /// catalogue on every request is deliberate.
    /// </remarks>
    [Fact]
    public async Task Every_request_reads_the_store_while_it_answers()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        for (int i = 0; i < 5; i++)
        {
            CatalogAnswer answer = await catalog.FindServiceAsync(null, "parcels", default);

            Assert.False(answer.Blind);
            Assert.NotNull(answer.Service);
        }

        Assert.Equal(5, store.Reads);
    }

    /// <summary>A revoked scope takes effect on the very next request.</summary>
    [Fact]
    public async Task A_change_of_sharing_is_seen_at_once()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        Assert.Equal(
            SharingScope.Public,
            (await catalog.FindServiceAsync(null, "parcels", default)).Service!.Sharing);

        store.Answer = Service("parcels", SharingScope.Private);

        Assert.Equal(
            SharingScope.Private,
            (await catalog.FindServiceAsync(null, "parcels", default)).Service!.Sharing);
    }

    // ---------- blind ----------

    /// <summary>The last answer is served when the store goes quiet.</summary>
    [Fact]
    public async Task The_last_answer_is_served_when_the_store_goes_quiet()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) =
            Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);

        store.Failure = Unreachable();
        clock.Advance(TimeSpan.FromSeconds(30));

        CatalogAnswer answer = await catalog.FindServiceAsync(null, "parcels", default);

        Assert.True(answer.Blind);
        Assert.Equal("parcels", answer.Service!.Name);
        Assert.Equal(30, (int)answer.Age.TotalSeconds);
    }

    /// <summary>
    /// A service never seen before is not invented, and not denied either.
    /// </summary>
    /// <remarks>
    /// <b>The caller gets blind-with-nothing, and the endpoint turns that into a
    /// 503 rather than a 404.</b> A 404 would be a claim, and the claim is wrong
    /// for every service published since this process last read the catalogue —
    /// and for every service at all after a restart.
    /// </remarks>
    [Fact]
    public async Task A_service_never_seen_is_not_invented()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        store.Failure = Unreachable();

        CatalogAnswer answer = await catalog.FindServiceAsync(null, "trees", default);

        Assert.True(answer.Blind);
        Assert.Null(answer.Service);
    }

    /// <summary>
    /// A deleted service does not come back during the next outage.
    /// </summary>
    /// <remarks>
    /// <b>The obvious bug in a cache like this.</b> Remembering only successes
    /// means the entry for a service that has since been unpublished is never
    /// overwritten, so it reappears the moment the store is unreachable — a
    /// layer somebody deliberately removed, back online.
    /// </remarks>
    [Fact]
    public async Task A_service_that_was_deleted_stays_deleted()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);

        store.Answer = null;
        await catalog.FindServiceAsync(null, "parcels", default);

        store.Failure = Unreachable();

        CatalogAnswer answer = await catalog.FindServiceAsync(null, "parcels", default);

        Assert.True(answer.Blind);
        Assert.Null(answer.Service);
    }

    /// <summary>The memory expires, so degraded serving is bounded.</summary>
    [Fact]
    public async Task The_memory_expires()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) =
            Build(Service("parcels", SharingScope.Public), TimeSpan.FromMinutes(15));

        await catalog.FindServiceAsync(null, "parcels", default);
        store.Failure = Unreachable();

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.NotNull((await catalog.FindServiceAsync(null, "parcels", default)).Service);

        clock.Advance(TimeSpan.FromMinutes(2));
        CatalogAnswer expired = await catalog.FindServiceAsync(null, "parcels", default);

        Assert.True(expired.Blind);
        Assert.Null(expired.Service);
    }

    /// <summary>A zero window turns degraded serving off entirely.</summary>
    /// <remarks>
    /// A deployment may prefer to stop rather than answer on a permission nobody
    /// can confirm. That is a real posture and it is reachable from
    /// configuration, so it is worth a test rather than an assumption about how
    /// the arithmetic happens to fall out.
    /// </remarks>
    [Fact]
    public async Task A_zero_window_serves_nothing_while_blind()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) =
            Build(Service("parcels", SharingScope.Public), TimeSpan.Zero);

        await catalog.FindServiceAsync(null, "parcels", default);
        store.Failure = Unreachable();
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Null((await catalog.FindServiceAsync(null, "parcels", default)).Service);
    }

    // ---------- which failures mean blind ----------

    /// <summary>
    /// A failure the server answered is a bug, and bugs are not degraded modes.
    /// </summary>
    /// <remarks>
    /// <b>This is the safety property of the class.</b> Falling back on any
    /// exception would mean a typo in our own SQL silently switches the server
    /// into serving remembered authorization — and it would look designed.
    /// </remarks>
    [Fact]
    public async Task A_server_side_error_is_not_a_degraded_mode()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);

        // 42703: undefined_column. A column we renamed and did not rename here.
        store.Failure = new PostgresException(
            "column l.sharing does not exist", "ERROR", "ERROR", "42703");

        await Assert.ThrowsAsync<PostgresException>(
            () => catalog.FindServiceAsync(null, "parcels", default));
    }

    /// <summary>
    /// The three answers that mean the server is going away, and the one that
    /// means it is full, do count as blind.
    /// </summary>
    [Theory]
    [InlineData("57P01")] // admin_shutdown
    [InlineData("57P02")] // crash_shutdown
    [InlineData("57P03")] // cannot_connect_now
    [InlineData("53300")] // too_many_connections
    public async Task A_store_saying_it_cannot_serve_counts_as_blind(string sqlState)
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);
        store.Failure = new PostgresException("going away", "FATAL", "FATAL", sqlState);

        Assert.True((await catalog.FindServiceAsync(null, "parcels", default)).Blind);
    }

    /// <summary>A timeout is the store not being there, from here.</summary>
    [Fact]
    public async Task A_timeout_counts_as_blind()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);
        store.Failure = new TimeoutException("exhausted the pool waiting for a connection");

        Assert.True((await catalog.FindServiceAsync(null, "parcels", default)).Blind);
    }

    /// <summary>Anything else is a bug and travels as one.</summary>
    [Fact]
    public async Task An_unrelated_failure_is_not_swallowed()
    {
        (CatalogFallback catalog, Store store, _) = Build(Service("parcels", SharingScope.Public));

        await catalog.FindServiceAsync(null, "parcels", default);
        store.Failure = new InvalidOperationException("the secret protector has no key");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.FindServiceAsync(null, "parcels", default));
    }

    /// <summary>The memory is per folder, because the names collide by design.</summary>
    /// <remarks>
    /// A hosted service and a registered one may share a name — that is the
    /// whole reason the folders exist and redirect to each other. Keying the
    /// memory on the name alone would serve one in place of the other, and only
    /// during an outage, which is the worst possible time to discover it.
    /// </remarks>
    [Fact]
    public async Task The_memory_is_per_folder()
    {
        Store store = new() { Answer = Service("parcels", SharingScope.Public) };
        FakeTimeProvider clock = new();
        CatalogFallback catalog = new(store.ReadAsync, clock);

        await catalog.FindServiceAsync("hosted", "parcels", default);
        store.Failure = Unreachable();

        Assert.Null((await catalog.FindServiceAsync(null, "parcels", default)).Service);
        Assert.NotNull((await catalog.FindServiceAsync("hosted", "parcels", default)).Service);
    }
}
