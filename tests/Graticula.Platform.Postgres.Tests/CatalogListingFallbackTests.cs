using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// What the catalogue lists when the platform store cannot list.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-127](../../docs/architecture-debt.md)'s first axis.</b> ADR-026 answers Q-95 with a
/// fallback that could resolve one named service and could not list — so the four faces that
/// begin by *enumerating* had no degraded path at all, and the row's own words are that there
/// was <i>no degraded listing capability</i>. These tests are about the half that was missing.
/// </para>
/// <para>
/// <b>The distinction under test is null against empty.</b> A remembered listing is a document;
/// an empty listing is a claim that this server publishes nothing, and a client that believes
/// it stops asking. Every test below is ultimately about which of those two a caller is handed.
/// </para>
/// <para>
/// Over a delegate rather than a database, for the reason
/// <see cref="CatalogFallbackTests"/> gives: the question is what the class decides.
/// </para>
/// </remarks>
public sealed class CatalogListingFallbackTests
{
    private static PublishedService Service(
        string name, SharingScope sharing = SharingScope.Public) =>
        new(Guid.NewGuid(), name, null, "FeatureServer", null, null, sharing,
            ServiceStatus.Started, []);

    /// <summary>A store that lists, then stops listing on command.</summary>
    private sealed class Store
    {
        public IReadOnlyList<PublishedService> Services { get; set; } = [];

        public Exception? Failure { get; set; }

        public int Lists { get; private set; }

        public Task<IReadOnlyList<PublishedService>> ListAsync(CancellationToken token)
        {
            Lists++;

            return Failure is not null
                ? Task.FromException<IReadOnlyList<PublishedService>>(Failure)
                : Task.FromResult(Services);
        }

        /// <summary>The resolve, which fails when the listing does.</summary>
        /// <remarks>
        /// <b>It has to be able to fail, or one test below asserts nothing.</b> The question
        /// *did the listing fill the per-service memory* can only be asked of a store that
        /// cannot be read: a healthy one answers from itself and says nothing about what is
        /// remembered.
        /// </remarks>
        public Task<PublishedService?> ReadAsync(
            string? folder, string name, CancellationToken t) =>
            Failure is not null
                ? Task.FromException<PublishedService?>(Failure)
                : Task.FromResult<PublishedService?>(null);
    }

    private static (CatalogFallback Catalog, Store Store, FakeTimeProvider Clock) Build(
        TimeSpan? window = null, params string[] services)
    {
        Store store = new();
        FakeTimeProvider clock = new();

        foreach (string name in services)
        {
            store.Services = [.. store.Services, Service(name)];
        }

        return (
            new CatalogFallback(store.ReadAsync, clock, window, null, store.ListAsync),
            store,
            clock);
    }

    private static NpgsqlException Unreachable() =>
        new("Failed to connect", new System.Net.Sockets.SocketException(10061));

    // ---------- the healthy path is unchanged ----------

    /// <summary>While the store lists, every request reads it.</summary>
    /// <remarks>
    /// <b>The same load-bearing property the resolve has.</b> If this became a read-through
    /// cache, a service published a second ago would be missing from the directory and every
    /// argument about why the catalogue is trustworthy would weaken — to save a query on a path
    /// that is already one query.
    /// </remarks>
    [Fact]
    public async Task Every_request_lists_the_store_while_it_answers()
    {
        (CatalogFallback catalog, Store store, _) = Build(null, "parcels", "roads");

        for (int i = 0; i < 5; i++)
        {
            CatalogListing listing = await catalog.ListServicesAsync(default);

            Assert.False(listing.Blind);
            Assert.Equal(TimeSpan.Zero, listing.Age);
            Assert.Equal(2, listing.Services!.Count);
        }

        Assert.Equal(5, store.Lists);
    }

    /// <summary>A service published now is in the very next listing.</summary>
    [Fact]
    public async Task A_new_service_appears_at_once()
    {
        (CatalogFallback catalog, Store store, _) = Build(null, "parcels");

        Assert.Single((await catalog.ListServicesAsync(default)).Services!);

        store.Services = [.. store.Services, Service("roads")];

        Assert.Equal(2, (await catalog.ListServicesAsync(default)).Services!.Count);
    }

    // ---------- blind ----------

    /// <summary>An unreachable store is answered from the last listing it gave.</summary>
    [Fact]
    public async Task An_unreachable_store_is_answered_from_the_last_listing()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) =
            Build(null, "parcels", "roads");

        await catalog.ListServicesAsync(default);

        store.Failure = Unreachable();
        clock.Advance(TimeSpan.FromSeconds(20));

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.True(listing.Blind);
        Assert.Equal(TimeSpan.FromSeconds(20), listing.Age);
        Assert.Equal(2, listing.Services!.Count);
    }

    /// <summary>
    /// With nothing remembered, the answer is null and not an empty list.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole distinction, and it is the one a caller gets wrong.</b> An empty
    /// list flows straight through a filter and out into a capabilities document that says this
    /// server publishes nothing. Null cannot: it has to be handled, and every face handles it by
    /// refusing with 503. After a restart during an outage there is nothing remembered, so this
    /// is not a corner — it is the first request of the worst case.
    /// </remarks>
    [Fact]
    public async Task With_nothing_remembered_the_answer_is_null_rather_than_empty()
    {
        (CatalogFallback catalog, Store store, _) = Build(null, "parcels");

        store.Failure = Unreachable();

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.True(listing.Blind);
        Assert.Null(listing.Services);
    }

    /// <summary>
    /// A store that publishes nothing is remembered as publishing nothing.
    /// </summary>
    /// <remarks>
    /// <b>The other side of the test above, and it is why the two states are separate.</b> An
    /// empty listing is a real answer: a server with no services says so, and while blind it
    /// keeps saying so rather than refusing.
    /// </remarks>
    [Fact]
    public async Task An_empty_listing_is_remembered_as_an_answer()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) = Build();

        Assert.Empty((await catalog.ListServicesAsync(default)).Services!);

        store.Failure = Unreachable();
        clock.Advance(TimeSpan.FromSeconds(5));

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.True(listing.Blind);
        Assert.NotNull(listing.Services);
        Assert.Empty(listing.Services!);
    }

    // ---------- public-only, which is Q-95's answer ----------

    /// <summary>A blind listing carries only the services shared with everybody.</summary>
    /// <remarks>
    /// <para>
    /// <b>The rule the resolve has always had, applied where a listing cannot walk past it.</b>
    /// `ServiceLookup.VisibleAsync` refuses any scope but `Public` while blind, on the grounds
    /// that the sharing value it would otherwise check is itself remembered. A listing hands
    /// five faces a list they filter by sharing — with the same remembered values, and a group
    /// membership that cannot be re-read is not evidence of anything.
    /// </para>
    /// <para>
    /// <b>So the filter is in the fallback and not in the faces.</b> Five copies of a safety
    /// rule is four chances to forget it, and the one that forgot would be the one that
    /// enumerates.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_blind_listing_carries_only_public_services()
    {
        (CatalogFallback catalog, Store store, _) = Build();

        store.Services =
        [
            Service("open"),
            Service("private", SharingScope.Private),
            Service("shared", SharingScope.Organization),
        ];

        Assert.Equal(3, (await catalog.ListServicesAsync(default)).Services!.Count);

        store.Failure = Unreachable();

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.True(listing.Blind);
        Assert.Single(listing.Services!);
        Assert.Equal("open", listing.Services![0].Name);
    }

    /// <summary>
    /// A server whose services are all private refuses rather than claiming to have none.
    /// </summary>
    /// <remarks>
    /// <b>The one case where public-only and null-is-not-empty meet.</b> Filtering a listing of
    /// three private services down to nothing produces an empty list, and an empty list is the
    /// claim <i>this server publishes nothing</i> — which is false, and which a client believes.
    /// It has something to publish and cannot say what, so it says so.
    /// </remarks>
    [Fact]
    public async Task A_blind_listing_with_nothing_public_refuses()
    {
        (CatalogFallback catalog, Store store, _) = Build();

        store.Services = [Service("one", SharingScope.Private), Service("two", SharingScope.Private)];

        Assert.Equal(2, (await catalog.ListServicesAsync(default)).Services!.Count);

        store.Failure = Unreachable();

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.True(listing.Blind);
        Assert.Null(listing.Services);
    }

    /// <summary>The healthy listing is not filtered.</summary>
    /// <remarks>
    /// <b>Asserted separately because filtering the healthy path would be a real defect and a
    /// quiet one.</b> Every face's own sharing evaluation is what decides who sees what while
    /// the store answers; a fallback that pre-filtered would hide private services from their
    /// own owners.
    /// </remarks>
    [Fact]
    public async Task A_healthy_listing_carries_every_scope()
    {
        (CatalogFallback catalog, Store store, _) = Build();

        store.Services = [Service("open"), Service("private", SharingScope.Private)];

        CatalogListing listing = await catalog.ListServicesAsync(default);

        Assert.False(listing.Blind);
        Assert.Equal(2, listing.Services!.Count);
    }

    /// <summary>Past the window, the memory stops being served.</summary>
    /// <remarks>
    /// <b>The same bound the resolve has, for the same reason.</b> An outage longer than the
    /// window is one somebody is handling, and a directory that keeps listing a
    /// decommissioned service for a week is how degraded serving becomes lying.
    /// </remarks>
    [Fact]
    public async Task The_memory_expires_with_the_window()
    {
        (CatalogFallback catalog, Store store, FakeTimeProvider clock) =
            Build(TimeSpan.FromMinutes(15), "parcels");

        await catalog.ListServicesAsync(default);

        store.Failure = Unreachable();
        clock.Advance(TimeSpan.FromMinutes(14));

        Assert.NotNull((await catalog.ListServicesAsync(default)).Services);

        clock.Advance(TimeSpan.FromMinutes(2));

        CatalogListing expired = await catalog.ListServicesAsync(default);

        Assert.True(expired.Blind);
        Assert.Null(expired.Services);
        Assert.True(expired.Age > TimeSpan.FromMinutes(15));
    }

    /// <summary>A failure the server answered is a bug, and is rethrown.</summary>
    /// <remarks>
    /// <b>The safety of the whole class.</b> Falling back on any exception would mean a typo in
    /// our own SQL quietly switches the server into a mode that serves a remembered listing —
    /// and it would look like a designed degradation rather than the defect it is.
    /// </remarks>
    [Fact]
    public async Task A_failure_the_server_answered_is_not_a_fallback()
    {
        (CatalogFallback catalog, Store store, _) = Build(null, "parcels");

        await catalog.ListServicesAsync(default);

        store.Failure = new PostgresException(
            "column does not exist", "ERROR", "ERROR", "42703");

        await Assert.ThrowsAsync<PostgresException>(
            () => catalog.ListServicesAsync(default));
    }

    /// <summary>
    /// A listing does not refresh what the resolve remembers.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because it is a decision rather than an oversight.</b> Feeding every listing
    /// into the per-service memory would be one dictionary write per service on every
    /// `/rest/services` — a thousand at the scale target, on a request the console makes on
    /// every refresh — to make an outage slightly better at remembering services nobody asked
    /// for by name. If that trade is ever taken, this test is where it is recorded that it was
    /// taken deliberately.
    /// </remarks>
    [Fact]
    public async Task A_listing_does_not_fill_the_per_service_memory()
    {
        (CatalogFallback catalog, Store store, _) = Build(null, "parcels");

        await catalog.ListServicesAsync(default);

        store.Failure = Unreachable();

        CatalogAnswer answer = await catalog.FindServiceAsync(null, "parcels", default);

        Assert.True(answer.Blind);
        Assert.Null(answer.Service);
    }

    /// <summary>A fallback with no listing read says so rather than pretending.</summary>
    /// <remarks>
    /// <b>The delegate constructor's listing is optional</b>, so that a test of the remembering
    /// policy does not have to write a listing it never calls. Calling one that was never
    /// supplied is a wiring mistake and reads as one.
    /// </remarks>
    [Fact]
    public async Task A_fallback_built_without_a_listing_refuses_to_list()
    {
        Store store = new();
        CatalogFallback catalog = new(store.ReadAsync, new FakeTimeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ListServicesAsync(default));
    }
}
