using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// What the service-context cache remembers, and what it refuses to remember.
/// </summary>
/// <remarks>
/// <para>
/// The second half is the point. A cache is easy to test for hits and hard to
/// test for the thing that makes it dangerous — holding an authorization
/// decision past the moment it changed. <see cref="Nothing_about_who_may_read_a_layer_is_remembered"/>
/// is the test that fails if somebody later decides to cache the whole
/// <see cref="PublishedLayer"/> because it would be faster.
/// </para>
/// <para>
/// These run against a counting fake rather than a database, because what is
/// under test is how many times the source is asked — which a real database
/// would answer correctly either way.
/// </para>
/// </remarks>
public sealed class ServiceContextsTests
{
    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("name", FieldType.Text, true, 255),
    ];

    // ---------- what it remembers ----------

    [Fact]
    public async Task A_second_request_for_the_same_layer_does_not_describe_again()
    {
        (ServiceContexts contexts, CountingConnections counts) = Build();
        PublishedLayer layer = Layer("roads", "public", "roads");

        await contexts.GetAsync(layer, CancellationToken.None);
        await contexts.GetAsync(layer, CancellationToken.None);
        await contexts.GetAsync(layer, CancellationToken.None);

        Assert.Equal(1, counts.Describes);
    }

    [Fact]
    public async Task A_described_shape_is_forgotten_once_its_lifetime_has_passed()
    {
        (ServiceContexts contexts, CountingConnections counts, FakeTimeProvider clock) = BuildWithClock();
        PublishedLayer layer = Layer("roads", "public", "roads");

        await contexts.GetAsync(layer, CancellationToken.None);
        clock.Advance(ServiceContexts.Lifetime + TimeSpan.FromSeconds(1));
        await contexts.GetAsync(layer, CancellationToken.None);

        // The backstop for a table altered underneath us. Without it the only
        // way to see a new column is a restart, which takes every other layer
        // down with it.
        Assert.Equal(2, counts.Describes);
    }

    [Fact]
    public async Task A_shape_is_still_trusted_one_tick_before_it_expires()
    {
        (ServiceContexts contexts, CountingConnections counts, FakeTimeProvider clock) = BuildWithClock();
        PublishedLayer layer = Layer("roads", "public", "roads");

        await contexts.GetAsync(layer, CancellationToken.None);
        clock.Advance(ServiceContexts.Lifetime - TimeSpan.FromMilliseconds(1));
        await contexts.GetAsync(layer, CancellationToken.None);

        Assert.Equal(1, counts.Describes);
    }

    // ---------- what the key means ----------

    [Fact]
    public async Task A_layer_republished_onto_a_different_table_does_not_inherit_the_old_columns()
    {
        // The reason the key is the table's identity rather than the layer's
        // name. Keyed by name, unpublishing 'roads' and republishing it over a
        // different table would serve the old table's field list — a wrong
        // answer that looks like a right one.
        (ServiceContexts contexts, CountingConnections counts) = Build();

        await contexts.GetAsync(Layer("roads", "public", "roads_2024"), CancellationToken.None);
        await contexts.GetAsync(Layer("roads", "public", "roads_2025"), CancellationToken.None);

        Assert.Equal(2, counts.Describes);
    }

    [Fact]
    public async Task Two_layers_over_the_same_table_share_one_described_shape()
    {
        // The other half of the same decision. Publishing the same table twice
        // under two names — a public one and a restricted one, which is how
        // sharing is expressed without groups — is one table and one shape.
        (ServiceContexts contexts, CountingConnections counts) = Build();

        await contexts.GetAsync(Layer("roads-public", "public", "roads"), CancellationToken.None);
        await contexts.GetAsync(Layer("roads-internal", "public", "roads"), CancellationToken.None);

        Assert.Equal(1, counts.Describes);
    }

    [Fact]
    public async Task The_same_table_name_in_two_databases_is_two_shapes()
    {
        (ServiceContexts contexts, CountingConnections counts) = Build();

        await contexts.GetAsync(
            Layer("a", "public", "roads", connection: "Host=one"), CancellationToken.None);
        await contexts.GetAsync(
            Layer("b", "public", "roads", connection: "Host=two"), CancellationToken.None);

        Assert.Equal(2, counts.Describes);
    }

    [Fact]
    public async Task The_same_table_name_in_two_schemas_is_two_shapes()
    {
        (ServiceContexts contexts, CountingConnections counts) = Build();

        await contexts.GetAsync(Layer("a", "staging", "roads"), CancellationToken.None);
        await contexts.GetAsync(Layer("b", "public", "roads"), CancellationToken.None);

        Assert.Equal(2, counts.Describes);
    }

    // ---------- what it refuses to remember ----------

    [Fact]
    public async Task Nothing_about_who_may_read_a_layer_is_remembered()
    {
        // <b>The test that matters.</b> Sharing, owner and started/stopped are
        // read from the catalogue on every request, and this cache must never
        // become the reason a layer made private stays readable. If somebody
        // later caches the whole PublishedLayer because it would be faster,
        // this is what tells them what they traded away.
        (ServiceContexts contexts, _) = Build();

        PublishedLayer open = Layer("roads", "public", "roads", sharing: SharingScope.Public);
        await contexts.GetAsync(open, CancellationToken.None);

        PublishedLayer shut = Layer(
            "roads", "public", "roads",
            sharing: SharingScope.Private, status: ServiceStatus.Stopped);

        // The cache hands back a shape for whatever layer it is given. It holds
        // no opinion about visibility, so it cannot hold a stale one.
        (IFeatureSource _, LayerDescription description) =
            await contexts.GetAsync(shut, CancellationToken.None);

        Assert.Equal(["objectid", "name"], description.Fields.Select(f => f.Name));

        foreach (System.Reflection.PropertyInfo property in typeof(ServiceContexts).GetProperties())
        {
            Assert.NotEqual(typeof(PublishedLayer), property.PropertyType);
        }
    }

    [Fact]
    public async Task A_failed_describe_is_not_remembered_as_a_failure()
    {
        // Caching the exception would turn one refused connection into a whole
        // lifetime of refusals for a database that recovered immediately — and
        // only the caller who happened to trigger it would see a real error.
        (ServiceContexts contexts, CountingConnections counts) = Build();
        counts.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => contexts.GetAsync(Layer("roads", "public", "roads"), CancellationToken.None));

        (IFeatureSource _, LayerDescription description) =
            await contexts.GetAsync(Layer("roads", "public", "roads"), CancellationToken.None);

        Assert.Equal(2, counts.Describes);
        Assert.Equal(2, description.Fields.Count);
    }

    // ---------- the stampede ----------

    [Fact]
    public async Task A_burst_against_a_cold_layer_describes_once_rather_than_once_each()
    {
        // The failure that makes a cache miss worse than no cache: sixty-four
        // first requests arriving together must produce one round trip and
        // sixty-three waiters, not sixty-four round trips at the moment the
        // database is least able to take them.
        //
        // <b>Genuinely parallel.</b> The first version of this test started
        // sixty-four async calls on one thread, which run synchronously to
        // their first await one after another — so the entry was always
        // published before the second caller looked, and the test would have
        // passed against an implementation with a real race in it. It caught
        // nothing.
        (ServiceContexts contexts, CountingConnections counts) = Build();
        counts.HoldUntilReleased = true;
        PublishedLayer layer = Layer("roads", "public", "roads");

        await StormAsync(contexts, layer, counts, callers: 64);

        Assert.Equal(1, counts.Describes);
    }

    [Fact]
    public async Task A_burst_on_an_expired_shape_renews_it_once()
    {
        // The same race one step along, and the one GetOrAdd alone does not
        // close: every caller sees an entry, every caller sees it expired, and
        // without TryUpdate on the entry they actually read, each starts its own
        // renewal.
        (ServiceContexts contexts, CountingConnections counts, FakeTimeProvider clock) = BuildWithClock();
        PublishedLayer layer = Layer("roads", "public", "roads");

        await contexts.GetAsync(layer, CancellationToken.None);
        clock.Advance(ServiceContexts.Lifetime + TimeSpan.FromSeconds(1));

        await StormAsync(contexts, layer, counts, callers: 32);

        Assert.Equal(2, counts.Describes);
    }

    // ---------- forgetting ----------

    [Fact]
    public async Task Forgetting_a_layer_makes_the_next_request_describe_again()
    {
        (ServiceContexts contexts, CountingConnections counts) = Build();
        PublishedLayer layer = Layer("roads", "public", "roads");

        await contexts.GetAsync(layer, CancellationToken.None);
        contexts.Forget(layer);
        await contexts.GetAsync(layer, CancellationToken.None);

        Assert.Equal(2, counts.Describes);
    }

    [Fact]
    public async Task Forgetting_one_layer_leaves_the_others_alone()
    {
        (ServiceContexts contexts, CountingConnections counts) = Build();
        PublishedLayer roads = Layer("roads", "public", "roads");
        PublishedLayer rivers = Layer("rivers", "public", "rivers");

        await contexts.GetAsync(roads, CancellationToken.None);
        await contexts.GetAsync(rivers, CancellationToken.None);
        contexts.Forget(roads);
        await contexts.GetAsync(rivers, CancellationToken.None);

        Assert.Equal(2, counts.Describes);
    }

    [Fact]
    public async Task The_count_reports_distinct_tables_rather_than_layers()
    {
        // What /admin/health shows. Two names over one table is one shape, and
        // an operator reading a count of 1 for two published layers should be
        // able to find that stated somewhere rather than think it a bug.
        (ServiceContexts contexts, _) = Build();

        await contexts.GetAsync(Layer("a", "public", "roads"), CancellationToken.None);
        await contexts.GetAsync(Layer("b", "public", "roads"), CancellationToken.None);
        await contexts.GetAsync(Layer("c", "public", "rivers"), CancellationToken.None);

        Assert.Equal(2, contexts.Count);
    }

    // ---------- fixtures ----------

    /// <summary>
    /// Sends many callers into the cache at the same instant.
    /// </summary>
    /// <remarks>
    /// <b>Dedicated threads, not <c>Task.Run</c>.</b> A barrier across N
    /// thread-pool tasks deadlocks when the pool holds fewer than N threads: the
    /// ones that got a thread block waiting for ones that never start, and the
    /// pool only adds threads slowly. That hung this test suite for two minutes
    /// before it was traced. Real threads cost more and cannot starve.
    /// </remarks>
    private static async Task StormAsync(
        ServiceContexts contexts, PublishedLayer layer, CountingConnections counts, int callers)
    {
        counts.HoldUntilReleased = true;

        using Barrier barrier = new(callers);
        Task<(IFeatureSource, LayerDescription)>[] all = new Task<(IFeatureSource, LayerDescription)>[callers];
        Thread[] threads = new Thread[callers];

        for (int i = 0; i < callers; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                all[index] = contexts.GetAsync(layer, CancellationToken.None);
            })
            { IsBackground = true };

            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "a caller never reached the cache");
        }

        counts.Release();
        await Task.WhenAll(all);
    }

    private static (ServiceContexts, CountingConnections) Build()
    {
        (ServiceContexts contexts, CountingConnections counts, FakeTimeProvider _) = BuildWithClock();
        return (contexts, counts);
    }

    private static (ServiceContexts, CountingConnections, FakeTimeProvider) BuildWithClock()
    {
        CountingConnections counts = new();
        FakeTimeProvider clock = new();
        return (new ServiceContexts(counts, clock), counts, clock);
    }

    private static PublishedLayer Layer(
        string name,
        string schema,
        string table,
        string connection = "Host=one",
        SharingScope sharing = SharingScope.Organization,
        ServiceStatus status = ServiceStatus.Started) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition(name, schema, table, "geom", 3857, "id", "objectid", false),
            "source",
            connection,
            GeometryKind.Polygon,
            null,
            sharing,
            status);

    /// <summary>A source factory that counts describes and can be made to fail or stall.</summary>
    private sealed class CountingConnections : IServiceSources
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _describes;

        public int Describes => Volatile.Read(ref _describes);

        public bool FailNext { get; set; }

        public bool HoldUntilReleased { get; set; }

        public void Release() => _gate.TrySetResult();

        public IFeatureSource SourceFor(PublishedLayer layer) => new CountingSource(this);

        private async Task<LayerDescription> DescribeAsync()
        {
            Interlocked.Increment(ref _describes);

            if (HoldUntilReleased)
            {
                await _gate.Task.ConfigureAwait(false);
            }

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("the data source refused");
            }

            return new LayerDescription(Fields, new Envelope(0, 0, 1, 1));
        }

        private sealed class CountingSource(CountingConnections owner) : IFeatureSource
        {
            public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
                owner.DescribeAsync();

            public FeatureSchema SchemaFor(FeatureQuery query) =>
                throw new NotSupportedException();

            public IAsyncEnumerable<Feature> ReadAsync(
                FeatureQuery query, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }
}
