using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Where a GeoParquet folder may be registered from, and what the server refuses — ADR-066 §3.
/// </summary>
/// <remarks>
/// <b>The root is the whole of the argument for reading files in the serving process</b>, so these
/// are mostly the refusals: a path that climbs out, a feature that is off, a locator written by hand,
/// a publication whose reference or identity the file does not have.
/// </remarks>
public sealed class GeoParquetSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graticula-root-" + Guid.NewGuid().ToString("n")[..10]);
    private readonly GeoParquetSources _sources;

    public GeoParquetSourcesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "istanbul"));
        Directory.CreateDirectory(_root + "-sibling");

        GeoParquetFixture.Write(
            Path.Combine(_root, "istanbul", "parcels.parquet"),
            [new("objectid", "BIGINT"), new("name", "VARCHAR")],
            Enumerable.Range(1, 4).Select(i => (new object?[] { (long)i, $"p{i}" }, (Geometry?)new Point(i, i))),
            srid: 3857);

        _sources = new GeoParquetSources(_root, "128MB", 1);
    }

    public void Dispose()
    {
        _sources.Dispose();

        foreach (string folder in new[] { _root, _root + "-sibling" })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_folder_inside_the_root_is_located_by_name_or_by_path()
    {
        Assert.True(_sources.TryLocate("istanbul", out string? byName, out string? why), why);
        Assert.True(_sources.TryLocate(Path.Combine(_root, "istanbul"), out string? byPath, out why), why);

        Assert.Equal(byName, byPath);
        Assert.True(GeoParquetLocator.Is(byName));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("istanbul/../../")]
    [InlineData("nope")]
    [InlineData("")]
    public void A_folder_outside_the_root_or_absent_is_refused(string requested)
    {
        Assert.False(_sources.TryLocate(requested, out string? locator, out string? why));
        Assert.Null(locator);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_sibling_whose_name_starts_with_the_root_is_outside_it()
    {
        // `/data/geo` must not admit `/data/geo-sibling`: a prefix test on strings would.
        Assert.False(_sources.TryLocate(_root + "-sibling", out _, out string? why));
        Assert.Contains("outside", why, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_root_every_folder_is_refused_and_the_refusal_names_the_setting()
    {
        using GeoParquetSources off = new(null);

        Assert.False(off.TryLocate("istanbul", out _, out string? why));
        Assert.Contains("GeoParquetRoot", why, StringComparison.Ordinal);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => off.FolderFor(GeoParquetLocator.For(Path.Combine(_root, "istanbul"))));
        Assert.Contains("GeoParquetRoot", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_locator_outside_the_root_is_not_read_even_after_it_was_stored()
    {
        // Stored when the root was elsewhere, or written into the catalogue by hand.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => _sources.FolderFor(GeoParquetLocator.For(_root + "-sibling")));

        Assert.Contains("outside", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_locator_that_climbs_out_of_the_root_is_not_read()
    {
        // Begins with the root as text, and is not inside it once resolved.
        string climbing = _root.Replace('\\', '/') + "/istanbul/../..";

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => _sources.FolderFor(GeoParquetLocator.For(climbing)));

        Assert.Contains("outside", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_whose_name_DuckDB_would_read_as_a_pattern_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a[1]"));

        Assert.False(_sources.TryLocate("a[1]", out _, out string? why));
        Assert.Contains("pattern", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Probing_a_folder_nothing_reads_leaves_nothing_open()
    {
        Assert.True(_sources.TryLocate("istanbul", out string? locator, out _));

        Assert.Single(_sources.Probe(locator!).Tables);

        // Nothing to close: the probe opened its own DuckDB and closed it.
        Assert.False(_sources.Close(locator!));

        _sources.FolderFor(locator!);
        Assert.Single(_sources.Probe(locator!).Tables);
        Assert.True(_sources.Close(locator!));
    }

    [Fact]
    public void The_probe_lists_the_files_as_tables_in_main()
    {
        Assert.True(_sources.TryLocate("istanbul", out string? locator, out _));

        ProbeResult probed = _sources.Probe(locator!);

        Assert.Equal(ProbeOutcome.Usable, probed.Outcome);
        SourceTable table = Assert.Single(probed.Tables);
        Assert.Equal(("main", "parcels", "geom", 3857, "Point", "objectid", false),
            (table.SchemaName, table.TableName, table.GeometryColumn, table.Srid, table.GeometryType, table.CandidateObjectIdColumn, table.Writable));
        Assert.StartsWith("DuckDB ", probed.ServerVersion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main", "parcels", "geom", 3857, "objectid", null, GeometryKind.Point, null)]
    [InlineData("public", "parcels", "geom", 3857, "objectid", null, GeometryKind.Point, "schema")]
    [InlineData("main", "absent", "geom", 3857, "objectid", null, GeometryKind.Point, "absent.parquet")]
    [InlineData("main", "parcels", "shape", 3857, "objectid", null, GeometryKind.Point, "geometry in 'geom'")]
    [InlineData("main", "parcels", "geom", 4326, "objectid", null, GeometryKind.Point, "EPSG:3857")]
    [InlineData("main", "parcels", "geom", 3857, "name", null, GeometryKind.Point, "cannot be the identity")]
    [InlineData("main", "parcels", "geom", 3857, "objectid", "file_row_number", GeometryKind.Point, "same name")]
    [InlineData("main", "parcels", "geom", 3857, "objectid", null, GeometryKind.Polygon, "not Polygon")]
    [InlineData("main", "parcels", "geom", 3857, "objectid", null, GeometryKind.MultiPoint, null)]
    public void A_publication_is_checked_against_the_file(
        string schema, string table, string geometry, int srid, string identity, string? objectId, GeometryKind kind, string? refusal)
    {
        Assert.True(_sources.TryLocate("istanbul", out string? locator, out _));

        LayerPublication publication = new(
            "layer", Guid.NewGuid(), schema, table, geometry, identity, objectId, srid, kind, SharingScope.Private);

        string? said = _sources.RefusalFor(locator!, publication);

        if (refusal is null)
        {
            Assert.Null(said);
        }
        else
        {
            Assert.NotNull(said);
            Assert.Contains(refusal, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_probe_every_endpoint_calls_sends_a_folder_to_the_folder_and_a_string_to_PostgreSQL()
    {
        Assert.True(_sources.TryLocate("istanbul", out string? locator, out _));

        RecordingProbe postgres = new();
        DataSourceProbes probes = new(postgres, _sources);

        ProbeResult folder = await probes.ProbeAsync(locator!, CancellationToken.None);
        await probes.ProbeAsync("Host=example;Database=gis", CancellationToken.None);
        DatabaseListing listing = await probes.ListDatabasesAsync(locator!, CancellationToken.None);

        Assert.Single(folder.Tables);
        Assert.Equal(["Host=example;Database=gis"], postgres.Asked);
        Assert.Empty(listing.Databases);
    }

    [Fact]
    public void A_layer_on_a_folder_refuses_what_only_a_database_can_do()
    {
        using ConnectionBudget budget = new(8, 4);
        using LayerConnections connections = new(budget, new SourceBreaker(), null, _sources, new NoProjector());

        Assert.True(_sources.TryLocate("istanbul", out string? locator, out _));

        PublishedLayer layer = new(
            Guid.NewGuid(),
            new Graticula.Catalog.LayerDefinition("parcels", "main", "parcels", "geom", 3857, "objectid", "objectid", false),
            "istanbul", locator!, GeometryKind.Point, null, SharingScope.Public, ServiceStatus.Started);

        Assert.NotNull(connections.SourceFor(layer));

        QueryNotSupportedException refused = Assert.Throws<QueryNotSupportedException>(
            () => connections.WriterFor(layer, []));
        Assert.Contains("cannot be edited", refused.Message, StringComparison.Ordinal);

        Assert.Throws<QueryNotSupportedException>(() => connections.TileSourceFor(layer, []));
        Assert.Throws<QueryNotSupportedException>(() => connections.AttachmentsFor(layer));
        Assert.Throws<QueryNotSupportedException>(() => connections.RelatedFor(layer, layer));
        Assert.True(connections.CloseSource(locator!));
    }

    private sealed class RecordingProbe : IDataSourceProbe
    {
        public System.Collections.Generic.List<string> Asked { get; } = [];

        public Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken)
        {
            Asked.Add(connectionString);
            return Task.FromResult(new ProbeResult(ProbeOutcome.CannotConnect, "recorded", null, null, []));
        }

        public Task<DatabaseListing> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseListing(ProbeOutcome.CannotConnect, "recorded", []));
    }

    private sealed class NoProjector : IProjector
    {
        /// <summary>Not asked by these tests; simplifying is the GeoParquet provider's call.</summary>
        public Task<System.Collections.Generic.IReadOnlyList<Graticula.Geometries.Geometry>> GeneralizeAsync(
            System.Collections.Generic.IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, double tolerance,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("this test does not simplify");

        public Task<(System.Collections.Generic.IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            System.Collections.Generic.IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            System.Collections.Generic.IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<System.Collections.Generic.IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionProvenance("none", null));
    }
}
