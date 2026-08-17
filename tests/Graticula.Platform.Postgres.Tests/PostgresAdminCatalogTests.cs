using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Platform.Secrets;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

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

    /// <summary>
    /// What was written is what is read back, field for field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There was no read at all until 2026-08-17, and that is the defect this
    /// covers.</b> The write shipped with a screen that drew every control from
    /// nothing and explained in prose that it could not show the current value — so
    /// an operator narrowing a ceiling could not see the ceiling. The console asked
    /// for it, got <c>405</c>, and reported the refusal in a corner.
    /// </para>
    /// <para>
    /// Asserted field by field rather than by comparing objects, because the fault
    /// this guards against is a read that selects eight of nine columns: the ninth
    /// then comes back unset, the screen shows it blank, and saving clears a limit
    /// nobody was shown.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Configured_capabilities_survive_the_round_trip()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "capped", service: "capped"), owner, CancellationToken.None);

        ServiceCapabilityLimits written = new ServiceCapabilityLimits(
            servesFeatures: true,
            servesTiles: false,
            ceiling: ["Query", "Extract"],
            statementTimeout: TimeSpan.FromMilliseconds(7000))
            .With(new ServiceCostCeilings(20_000, 500, 33_554_432, 1_048_576, 250));

        Assert.True(await admin.SetServiceCapabilitiesAsync(
            "capped", null, written, CancellationToken.None));

        ServiceCapabilityLimits? read = await admin
            .FindServiceCapabilitiesAsync("capped", null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.True(read!.ServesFeatures);
        Assert.False(read.ServesTiles);
        Assert.Equal(["Query", "Extract"], read.Ceiling);
        Assert.Equal(TimeSpan.FromMilliseconds(7000), read.StatementTimeout);
        Assert.Equal(20_000, read.Cost.MaximumRecordCount);
        Assert.Equal(500, read.Cost.DefaultRecordCount);
        Assert.Equal(33_554_432, read.Cost.MaximumResponseBytes);
        Assert.Equal(1_048_576, read.Cost.MaximumRequestBytes);
        Assert.Equal(250, read.Cost.MaximumEditsPerTransaction);
    }

    /// <summary>
    /// A service with nothing configured reads back unset, and one that is absent
    /// reads back null.
    /// </summary>
    /// <remarks>
    /// <b>Two different answers, and a caller acts differently on each.</b> Unset
    /// means *this service constrains nothing*, which is a valid configuration to
    /// display; null means *there is no such service*, which is a 404. Collapsing
    /// them would make a typo in a service name look like a service with no limits.
    /// </remarks>
    [Fact]
    public async Task Unconfigured_reads_back_unset_and_absent_reads_back_null()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "bare", service: "bare"), owner, CancellationToken.None);

        ServiceCapabilityLimits? bare = await admin
            .FindServiceCapabilitiesAsync("bare", null, CancellationToken.None);

        Assert.NotNull(bare);
        Assert.True(bare!.IsUnset);

        Assert.Null(await admin.FindServiceCapabilitiesAsync("nosuch", null, CancellationToken.None));
    }

    /// <summary>
    /// The read matches its service the way every other lookup does: by folder, and
    /// without regard to case.
    /// </summary>
    /// <remarks>
    /// <b>Two services may differ only by folder</b>, and one of them may differ
    /// from the request only in case — which is the fault migration 15 was written
    /// for. A read that matched case-sensitively would answer 404 for a service the
    /// write path finds, so the screen would refuse to open a service it can save.
    /// </remarks>
    [Fact]
    public async Task The_read_finds_the_service_by_folder_and_ignores_case()
    {
        (PostgresAdminCatalog admin, _, Guid owner) = await ReadyAsync();

        // Created rather than published into: LayerPublication has no folder, so a
        // service in one is made directly. Both exist, and they differ only by folder.
        await admin.CreateServiceAsync(
            "same", "shared", null, SharingScope.Private, owner, CancellationToken.None);

        await admin.CreateServiceAsync(
            "same", null, null, SharingScope.Private, owner, CancellationToken.None);

        await admin.SetServiceCapabilitiesAsync(
            "same",
            "shared",
            new ServiceCapabilityLimits(null, null, null, null)
                .With(new ServiceCostCeilings(9_000, null, null, null, null)),
            CancellationToken.None);

        ServiceCapabilityLimits? read = await admin
            .FindServiceCapabilitiesAsync("SAME", "SHARED", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(9_000, read!.Cost.MaximumRecordCount);

        // And the root is a different place, not a synonym for any folder: the
        // service of the same name there was never configured.
        ServiceCapabilityLimits? root = await admin
            .FindServiceCapabilitiesAsync("same", null, CancellationToken.None);

        Assert.NotNull(root);
        Assert.True(root!.IsUnset);
    }

    /// <summary>
    /// An empty service can be removed; one holding a layer cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-48, and the empty case is the one that matters.</b> Publishing creates a
    /// service implicitly and unpublishing the last layer leaves it behind, so an estate
    /// accumulates empty services. Nothing could remove them, and nothing could even list
    /// them.
    /// </para>
    /// <para>
    /// The occupied case asserts the count comes back, not merely that the delete
    /// refused: the refusal exists to tell an operator what is in the way, and a refusal
    /// that says only *no* leaves them guessing which layer to unpublish.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_empty_service_is_removed_and_an_occupied_one_is_refused()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.CreateServiceAsync(
            "empty", null, null, SharingScope.Private, owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "occupant", service: "busy"), owner, CancellationToken.None);

        (Removal outcome, int layers, int groups) = await admin
            .DeleteServiceAsync("busy", null, CancellationToken.None);

        Assert.Equal(Removal.Occupied, outcome);
        Assert.Equal(1, layers);
        Assert.Equal(0, groups);

        // And the layer is still there: a refused delete must not have removed half of
        // what it refused to remove.
        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));
        Assert.NotNull(await catalog.FindServiceAsync(null, "busy", CancellationToken.None));

        Assert.Equal(
            (Removal.Removed, 0, 0),
            await admin.DeleteServiceAsync("empty", null, CancellationToken.None));

        Assert.Null(await catalog.FindServiceAsync(null, "empty", CancellationToken.None));
    }

    /// <summary>
    /// A service that holds only a group layer is still occupied.
    /// </summary>
    /// <remarks>
    /// <b>The half nobody thinks of.</b> A group is not a layer and carries no data, so
    /// "empty" could plausibly mean *no feature layers* — and then deleting the service
    /// would take a structure somebody built with it. Both tables count.
    /// </remarks>
    [Fact]
    public async Task A_service_holding_only_a_group_is_not_empty()
    {
        (PostgresAdminCatalog admin, _, Guid owner) = await ReadyAsync();

        await admin.CreateServiceAsync(
            "structured", null, null, SharingScope.Private, owner, CancellationToken.None);

        await admin.CreateGroupLayerAsync(
            null, "structured", "Utilities", null, CancellationToken.None);

        (Removal outcome, int layers, int groups) = await admin
            .DeleteServiceAsync("structured", null, CancellationToken.None);

        Assert.Equal(Removal.Occupied, outcome);
        Assert.Equal(0, layers);
        Assert.Equal(1, groups);
    }

    /// <summary>
    /// Absent and occupied are different answers.
    /// </summary>
    /// <remarks>
    /// A caller acts differently on each: absent means the name may be wrong, occupied
    /// means the name is right and the order of operations is not. Collapsing them tells
    /// an operator their service does not exist while they are looking at it.
    /// </remarks>
    [Fact]
    public async Task A_service_that_does_not_exist_is_absent_rather_than_occupied()
    {
        (PostgresAdminCatalog admin, _, _) = await ReadyAsync();

        Assert.Equal(
            (Removal.Absent, 0, 0),
            await admin.DeleteServiceAsync("nosuch", null, CancellationToken.None));

        // And a folder is part of the address: the same name elsewhere is not this one.
        Assert.Equal(
            (Removal.Absent, 0, 0),
            await admin.DeleteServiceAsync("nosuch", "somewhere", CancellationToken.None));
    }

    /// <summary>
    /// A group with a layer under it is refused; an empty group goes.
    /// </summary>
    /// <remarks>
    /// <b>The children are not reparented, and the count is what makes that workable.</b>
    /// Moving a layer to the top of the service as a side effect would move it in every
    /// saved map that points at it. So the refusal says how many there are, the operator
    /// moves them, and the delete then succeeds — which is the sequence this asserts.
    /// </remarks>
    [Fact]
    public async Task A_group_is_refused_while_it_has_children_and_removed_once_it_has_none()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        // The service first: a group belongs to one, and CreateGroupLayerAsync answers
        // null rather than inventing a container.
        await admin.CreateServiceAsync(
            "tree", null, null, SharingScope.Private, owner, CancellationToken.None);

        GroupLayerAddress group = (await admin.CreateGroupLayerAsync(
            null, "tree", "Roads", null, CancellationToken.None))
            ?? throw new InvalidOperationException("the group was not created");

        await admin.PublishLayerAsync(
            Publication(source, "child", service: "tree") with
            {
                ParentLayerIndex = group.LayerIndex,
            },
            owner,
            CancellationToken.None);

        (Removal occupied, int children) = await admin
            .DeleteGroupLayerAsync("tree", null, group.LayerIndex, CancellationToken.None);

        Assert.Equal(Removal.Occupied, occupied);
        Assert.Equal(1, children);

        Assert.True(await admin.UnpublishLayerAsync("child", CancellationToken.None));

        Assert.Equal(
            (Removal.Removed, 0),
            await admin.DeleteGroupLayerAsync("tree", null, group.LayerIndex, CancellationToken.None));
    }

    /// <summary>
    /// The listing reports what each service holds, and finds the empty ones.
    /// </summary>
    /// <remarks>
    /// <b>This is the half of D-48 that was not a missing delete.</b> An empty service
    /// appeared in no listing anywhere — <c>/admin/layers</c> lists layers, and the
    /// system-services table is a different table — so the residue of publishing and
    /// unpublishing was invisible.
    /// </remarks>
    [Fact]
    public async Task The_service_listing_counts_layers_and_groups()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.CreateServiceAsync(
            "hollow", null, "nothing in it", SharingScope.Public, owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "one", service: "pair"), owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "two", service: "pair"), owner, CancellationToken.None);

        await admin.CreateGroupLayerAsync(null, "pair", "Group", null, CancellationToken.None);

        IReadOnlyList<AdminService> services =
            await admin.ListServicesAsync(CancellationToken.None);

        AdminService hollow = services.Single(s => s.Name == "hollow");
        Assert.Equal(0, hollow.Layers);
        Assert.Equal(0, hollow.Groups);
        Assert.True(hollow.IsEmpty);
        Assert.Equal(SharingScope.Public, hollow.Sharing);
        Assert.Equal("nothing in it", hollow.Description);
        Assert.Equal("publisher", hollow.OwnerName);

        AdminService pair = services.Single(s => s.Name == "pair");
        Assert.Equal(2, pair.Layers);
        Assert.Equal(1, pair.Groups);
        Assert.False(pair.IsEmpty);
    }

    /// <summary>
    /// Stopping a service is read by the path that serves it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The test that was missing, and the defect it would have caught shipped.</b> Until
    /// 2026-08-17 <c>SetStatusAsync</c> wrote <c>layer.status</c> while every read took
    /// <c>service.status</c> — so <c>POST /admin/layers/{name}/stop</c> answered 200 saying
    /// *"Requests for this service now answer 503"*, wrote a column nothing reads, and the
    /// layer went on serving. Measured against a live server before the fix: the layer document
    /// answered 200 and a count query returned rows, while one admin listing said *stopped* and
    /// another said *started* about the same service.
    /// </para>
    /// <para>
    /// <b>So this asserts through the serving catalogue rather than through the setter's own
    /// answer.</b> The setter reported the transition correctly the whole time it was writing
    /// the wrong column — its return value was never the problem, and a test that checked it
    /// would have passed. What matters is what the request path sees.
    /// </para>
    /// <para>
    /// It is the same defect as <c>l.sharing</c>, which was found and repaired on 2026-08-15;
    /// the status setter is one method away in the same file and was not looked at. Two facts
    /// moved onto the service in migration 11 and one setter followed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stopping_a_layer_is_what_the_serving_catalogue_reads()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "stoppable"), owner, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService before =
            (await catalog.FindServiceAsync(null, "stoppable", CancellationToken.None))!;

        Assert.True(before.IsRunning);

        ServiceStatus? previous = await admin
            .SetStatusAsync("stoppable", ServiceStatus.Stopped, CancellationToken.None);

        Assert.Equal(ServiceStatus.Started, previous);

        PublishedService after =
            (await catalog.FindServiceAsync(null, "stoppable", CancellationToken.None))!;

        Assert.False(
            after.IsRunning,
            "The serving catalogue still reports this service as running, so every request for "
            + "it will be answered. That is the shape of the defect found on 2026-08-17: the "
            + "setter wrote layer.status and every reader takes service.status.");

        // And back, because a stop that cannot be undone is a different fault.
        Assert.Equal(
            ServiceStatus.Stopped,
            await admin.SetStatusAsync("stoppable", ServiceStatus.Started, CancellationToken.None));

        Assert.True(
            (await catalog.FindServiceAsync(null, "stoppable", CancellationToken.None))!.IsRunning);
    }

    /// <summary>
    /// The administrative listing agrees with the serving catalogue about status and sharing.
    /// </summary>
    /// <remarks>
    /// <b>Two listings disagreeing about one service is what made the first defect visible.</b>
    /// <c>/admin/layers</c> read the layer's dead copies of sharing and status while
    /// <c>/admin/featureservices</c> and the request path read the service's, so the console
    /// showed *stopped* on one screen and *started* on another. Asserted as an invariant between
    /// the two readers rather than against a literal, because the fault is disagreement.
    /// </remarks>
    [Fact]
    public async Task The_admin_listing_and_the_serving_catalogue_agree()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "agreeable"), owner, CancellationToken.None);

        await admin.SetStatusAsync("agreeable", ServiceStatus.Stopped, CancellationToken.None);
        await admin.SetSharingAsync("agreeable", SharingScope.Public, CancellationToken.None);

        AdminLayer listed = (await admin.ListLayersAsync(CancellationToken.None))
            .Single(l => l.Name == "agreeable");

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService served =
            (await catalog.FindServiceAsync(null, "agreeable", CancellationToken.None))!;

        Assert.Equal(served.Status, listed.Status);
        Assert.Equal(served.Sharing, listed.Sharing);
    }

    /// <summary>
    /// The service listing names one of its layers, and keeps naming it after a stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Surviving a stop is the whole reason this field exists.</b> Two things about a
    /// service need one of its members — a preview has to be drawn from a layer's data, and
    /// the status route is addressed by a layer name — and the console found one by walking
    /// the services directory. A stopped service answers 503 to that walk, so the row for the
    /// one service somebody most wants to start was the row with no layer to name and
    /// therefore no Start button.
    /// </para>
    /// <para>
    /// Asserted after stopping rather than only before, because before is the easy half and
    /// the directory walk passed it too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_listing_names_a_member_whether_or_not_the_service_runs()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "first", service: "covered"), owner, CancellationToken.None);

        await admin.PublishLayerAsync(
            Publication(source, "second", service: "covered"), owner, CancellationToken.None);

        AdminService running = (await admin.ListServicesAsync(CancellationToken.None))
            .Single(s => s.Name == "covered");

        Assert.Equal(2, running.Layers);

        // The lowest-numbered layer, which is the first one published here. Ordered rather
        // than assumed, so a service whose layer 0 was unpublished still names a member.
        Assert.Equal(new AdminServiceCover("first", 0), running.Cover);

        await admin.SetStatusAsync("first", ServiceStatus.Stopped, CancellationToken.None);

        AdminService stopped = (await admin.ListServicesAsync(CancellationToken.None))
            .Single(s => s.Name == "covered");

        Assert.Equal(ServiceStatus.Stopped, stopped.Status);

        Assert.Equal(
            new AdminServiceCover("first", 0),
            stopped.Cover);
    }

    /// <summary>
    /// A service holding nothing names no member, rather than naming a missing one.
    /// </summary>
    /// <remarks>
    /// An empty service is the ordinary residue of unpublishing the last layer
    /// (<see href="../../../docs/architecture-debt.md">D-54</see>), so this is the common
    /// case rather than the edge one, and a caller that assumed a cover would break on it.
    /// </remarks>
    [Fact]
    public async Task An_empty_service_names_no_member()
    {
        (PostgresAdminCatalog admin, _, Guid owner) = await ReadyAsync();

        await admin.CreateServiceAsync(
            "hollow", null, null, SharingScope.Private, owner, CancellationToken.None);

        AdminService hollow = (await admin.ListServicesAsync(CancellationToken.None))
            .Single(s => s.Name == "hollow");

        Assert.True(hollow.IsEmpty);
        Assert.Null(hollow.Cover);
    }
}
