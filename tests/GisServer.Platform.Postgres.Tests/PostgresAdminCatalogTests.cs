using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Admin;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Platform.Secrets;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Publishing, and mostly the index allocation underneath it.
/// </summary>
/// <remarks>
/// <b>Written after a defect that shipped and hid.</b> Publishing a layer into a
/// service that did not yet exist created the service, created no layer, and
/// answered 201. It survived because every test written after it published into
/// a service that already existed — the one path nobody exercised was the first
/// one a real user takes.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresAdminCatalogTests : PostgresFixture
{
    private async Task<(PostgresAdminCatalog Admin, Guid Source, Guid Owner)> ReadyAsync()
    {
        await MigrateAsync();

        SecretProtector secrets = new(1, new byte[32]);
        PostgresAdminCatalog admin = new(DataSource, secrets);

        Guid source = await admin.RegisterDataSourceAsync(
            "test-source", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        // A principal to own what gets published. The layer table has a foreign
        // key to it, so publishing without one fails for the wrong reason.
        Guid owner = Guid.NewGuid();

        await using NpgsqlCommand principal = DataSource.CreateCommand(
            "insert into principal (id, name, kind, user_type) values (@id, 'publisher', 'user', "
            + "'unrestricted')");

        principal.Parameters.AddWithValue("id", owner);
        await principal.ExecuteNonQueryAsync(CancellationToken.None);

        return (admin, source, owner);
    }

    private static LayerPublication Publication(
        Guid source, string name, string? service = null, int? cacheSeconds = null) =>
        new(
            name,
            source,
            "public",
            name,
            "geom",
            "objectid",
            "objectid",
            3857,
            GeometryKind.Point,
            SharingScope.Private,
            service,
            null,
            cacheSeconds);

    /// <summary>
    /// A layer published into a service that does not exist yet actually lands.
    /// </summary>
    /// <remarks>
    /// <b>This is the regression.</b> The allocation statement created the
    /// service in one data-modifying CTE and tried to bump its counter in
    /// another — and PostgreSQL gives every CTE the same snapshot, so the update
    /// matched no row, the layer insert selected from an empty result, and
    /// nothing was inserted. The service existed, the response said 201, and the
    /// service document listed no layers.
    /// </remarks>
    [Fact]
    public async Task Publishing_into_a_new_service_creates_the_layer_as_well()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        PublishedLayerAddress address = await admin.PublishLayerAsync(
            Publication(source, "first"), owner, CancellationToken.None);

        Assert.Equal("first", address.ServiceName);
        Assert.Equal(0, address.LayerIndex);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService? service = await catalog
            .FindServiceAsync(null, "first", CancellationToken.None);

        Assert.NotNull(service);

        PublishedLayer only = Assert.Single(service!.Layers);

        Assert.Equal("first", only.Definition.Name);
        Assert.Equal(0, only.LayerIndex);
    }

    [Fact]
    public async Task The_new_service_counter_is_left_ready_for_the_next_layer()
    {
        // A service created with its first layer at 0 must have its counter at 1,
        // or the second layer collides with the first.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "one", service: "shared"), owner, CancellationToken.None);

        PublishedLayerAddress second = await admin.PublishLayerAsync(
            Publication(source, "two", service: "shared"), owner, CancellationToken.None);

        Assert.Equal(1, second.LayerIndex);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "shared", CancellationToken.None))!;

        Assert.Equal(2, service.Layers.Count);
        Assert.Equal([0, 1], service.Layers.Select(l => l.LayerIndex).Order());
    }

    [Fact]
    public async Task An_index_is_never_reused_after_a_layer_is_removed()
    {
        // A saved web map stores this integer. Handing it out again after a
        // removal would silently repoint somebody's map at different data.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "gone", service: "svc"), owner, CancellationToken.None);

        await using NpgsqlCommand remove = DataSource.CreateCommand(
            "delete from layer where name = 'gone'");

        await remove.ExecuteNonQueryAsync(CancellationToken.None);

        PublishedLayerAddress next = await admin.PublishLayerAsync(
            Publication(source, "next", service: "svc"), owner, CancellationToken.None);

        Assert.Equal(1, next.LayerIndex);
    }

    [Fact]
    public async Task A_publish_time_cache_lifetime_is_stored_and_read_back()
    {
        // D-25: volatility is known at publish time by whoever publishes, and
        // A-028 says nobody else knows it.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "fast", cacheSeconds: 15), owner, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "fast", CancellationToken.None))!;

        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task A_layer_with_no_cache_lifetime_reads_back_as_null_rather_than_zero()
    {
        // Null means "nobody has said" and takes the server default; zero means
        // "never cache", which is a different answer somebody may want.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "unsaid"), owner, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "unsaid", CancellationToken.None))!;

        Assert.Null(Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task Setting_the_cache_lifetime_afterwards_takes_effect()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "later"), owner, CancellationToken.None);

        Assert.True(await admin.SetCacheLifetimeAsync("later", 0, CancellationToken.None));

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "later", CancellationToken.None))!;

        // Zero, not null: "never cache" survived the round trip as itself.
        Assert.Equal(TimeSpan.Zero, Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task Setting_it_on_a_layer_that_does_not_exist_says_so()
    {
        (PostgresAdminCatalog admin, _, _) = await ReadyAsync();

        Assert.False(await admin.SetCacheLifetimeAsync("nosuch", 60, CancellationToken.None));
    }

    /// <summary>
    /// A change of sharing is visible to the code that decides who may read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This failed until 2026-08-15, and it had shipped.</b>
    /// <c>SetSharingAsync</c> wrote <c>layer.sharing</c>; the serving path reads
    /// the owning <em>service</em>. So making a layer private returned 200 with
    /// <c>{"from":"public","to":"private"}</c>, updated a column nothing
    /// reads, and left the layer readable by anybody.
    /// </para>
    /// <para>
    /// <b>Nothing caught it because both sides were tested and neither round
    /// trip was.</b> The write side asserted that the column changed; the read
    /// side asserted that the service scope was honoured. The bug lived exactly
    /// in the gap, which is where this test now sits — write with the admin
    /// catalogue, read with the serving catalogue, and require them to agree.
    /// </para>
    /// <para>
    /// It was found by accident, testing the Q-95 outage path: a service that
    /// had just been made private answered a request it should have refused.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(SharingScope.Private)]
    [InlineData(SharingScope.Organization)]
    [InlineData(SharingScope.Public)]
    public async Task Sharing_written_by_an_administrator_is_what_serving_reads(SharingScope scope)
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "shared"), owner, CancellationToken.None);

        Assert.NotNull(await admin.SetSharingAsync("shared", scope, CancellationToken.None));

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "shared", CancellationToken.None))!;

        Assert.Equal(scope, service.Sharing);
    }

    /// <summary>
    /// Sharing set through one layer covers every layer in its service.
    /// </summary>
    /// <remarks>
    /// <b>A consequence worth asserting rather than discovering.</b> Since
    /// migration 11 sharing belongs to the service, so an endpoint addressed by
    /// layer name necessarily moves its siblings too. That is the model working
    /// as designed — and it is exactly the kind of thing an administrator should
    /// not learn from a support call.
    /// </remarks>
    [Fact]
    public async Task Sharing_set_through_one_layer_moves_the_whole_service()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "first", service: "together"), owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "second", service: "together"), owner, CancellationToken.None);

        await admin.SetSharingAsync("first", SharingScope.Public, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "together", CancellationToken.None))!;

        Assert.Equal(2, service.Layers.Count);
        Assert.Equal(SharingScope.Public, service.Sharing);
    }

    /// <summary>
    /// One service's groups do not arrive attached to another's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test does not prove the thing it was written to prove, and
    /// saying so is the point.</b> It was added beside a fix: the catalogue's
    /// group query ran with no <c>where</c> clause, so resolving one service
    /// read <em>every</em> group layer in the catalogue and discarded all but
    /// one service's — O(all services) on the most-used path in the product,
    /// against a stated scale target of 100 to 1,000 services. The claim written
    /// here first was that this test pinned that. **It was checked by reverting
    /// the filter, and the test stayed green.** Of course it did: the extra rows
    /// were read and thrown away, so the answer was always correct and only the
    /// work differed.
    /// </para>
    /// <para>
    /// <b>What it does pin is correctness</b>, which is worth pinning: a
    /// service carries its own groups and only its own. The query <em>shape</em>
    /// is pinned by the signature instead — <c>GroupsAsync</c> takes the list of
    /// services the caller found and has no "everything" case to fall back to,
    /// so restoring the old behaviour means editing SQL to ignore its own
    /// parameter rather than passing a null by accident.
    /// </para>
    /// <para>
    /// <b>Nor could the fix be measured.</b> The D-30 instrumentation is what
    /// found the problem — the catalogue read was 1.8 ms against 0.7 ms for the
    /// data query beside it — but the re-measurement afterwards was worthless:
    /// the machine had picked up other work and every phase moved by the same
    /// factor, including ones this change cannot touch. Two things a stopwatch
    /// could not deliver and a plan could: the query went from a sequential scan
    /// returning every row to a filter on an indexed column returning one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_service_does_not_carry_another_services_groups()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "mine", service: "ours"), owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "theirs", service: "yours"), owner, CancellationToken.None);

        await admin.CreateGroupLayerAsync(
            null, "ours", "Ours", null, CancellationToken.None);

        await admin.CreateGroupLayerAsync(
            null, "yours", "Theirs A", null, CancellationToken.None);

        await admin.CreateGroupLayerAsync(
            null, "yours", "Theirs B", null, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService ours =
            (await catalog.FindServiceAsync(null, "ours", CancellationToken.None))!;

        string only = Assert.Single(ours.Groups).Name;

        Assert.Equal("Ours", only);

        // The other service keeps both of its own, so the filter is a filter and
        // not an accident of ordering.
        PublishedService yours =
            (await catalog.FindServiceAsync(null, "yours", CancellationToken.None))!;

        Assert.Equal(
            ["Theirs A", "Theirs B"],
            yours.Groups.Select(group => group.Name).OrderBy(name => name).ToArray());
    }

    /// <summary>
    /// A lookup that finds nothing does not go back for groups.
    /// </summary>
    /// <remarks>
    /// <b>A 404 used to cost two round trips.</b> The second one asked for the
    /// group layers of a service that had just been established not to exist.
    /// <b>This test cannot see that either</b>, for the same reason as the one
    /// above: the answer was always null and only the work differed. It is kept
    /// as the correctness guard it is, and the round trip is removed by
    /// <c>GroupsAsync</c> returning early on an empty list — visible in the code
    /// rather than in a test, and recorded here so nobody mistakes green for
    /// proof.
    /// </remarks>
    [Fact]
    public async Task A_service_that_does_not_exist_is_null_rather_than_empty()
    {
        _ = await ReadyAsync();

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        Assert.Null(
            await catalog.FindServiceAsync(null, "no-such-service", CancellationToken.None));
    }

    /// <summary>
    /// A style is stored, read back byte for byte, and can be cleared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The clear path had its own bug and only a database could find it.</b>
    /// The update sets <c>style_updated_at</c> from a CASE over the same
    /// parameter, and a null parameter inside a CASE gives Postgres nothing to
    /// infer a type from: <c>42P08, could not determine data type of parameter
    /// $1</c>. Setting a style worked; clearing one returned a 500. A cast fixes
    /// it, and this test is why the cast will stay.
    /// </para>
    /// <para>
    /// <b>Byte for byte matters.</b> The column is text rather than jsonb
    /// precisely so a cartographer gets back the file they sent — same
    /// whitespace, same key order — and a normalising round trip would be a
    /// silent edit of somebody's work.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_style_survives_the_round_trip_and_can_be_cleared()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "styled"), owner, CancellationToken.None);

        // Deliberately odd formatting: two spaces, an unusual key order, a
        // trailing newline. All of it must come back.
        const string Style = """
            {
              "version":  8,
              "layers": [],
              "name": "İstanbul"
            }
            """;

        Assert.True(await admin.SetStyleAsync("styled", Style, CancellationToken.None));

        StyledService stored =
            (await admin.FindServiceForStyleAsync("styled", CancellationToken.None))!.Value;

        Assert.Equal(Style, stored.Style);
        Assert.Equal(["styled"], stored.SourceLayers);

        Assert.True(await admin.SetStyleAsync("styled", null, CancellationToken.None));

        StyledService cleared =
            (await admin.FindServiceForStyleAsync("styled", CancellationToken.None))!.Value;

        Assert.Null(cleared.Style);
    }

    /// <summary>
    /// The layer list a style is checked against is the service's own.
    /// </summary>
    /// <remarks>
    /// A service with two layers must offer both, or the validator refuses a
    /// style that is correct. The aggregate is a left join, so a service with no
    /// layers must come back with an empty list rather than a list holding one
    /// null.
    /// </remarks>
    [Fact]
    public async Task The_source_layers_offered_are_the_ones_the_service_has()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "first", service: "pair"), owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "second", service: "pair"), owner, CancellationToken.None);

        StyledService pair =
            (await admin.FindServiceForStyleAsync("pair", CancellationToken.None))!.Value;

        Assert.Equal(["first", "second"], pair.SourceLayers.OrderBy(n => n));
        Assert.Null(pair.Style);
    }

    [Fact]
    public async Task Styling_a_service_that_does_not_exist_says_so()
    {
        (PostgresAdminCatalog admin, _, _) = await ReadyAsync();

        Assert.Null(await admin.FindServiceForStyleAsync("nosuch", CancellationToken.None));
        Assert.False(await admin.SetStyleAsync("nosuch", "{}", CancellationToken.None));
    }
}
