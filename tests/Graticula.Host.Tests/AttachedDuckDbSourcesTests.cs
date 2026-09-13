using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Registering a DuckDB database file or a MotherDuck database, and publishing on a declared reference — ADR-067 §5.3–5.4.
/// </summary>
public sealed class AttachedDuckDbSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graticula-duckroot-" + Guid.NewGuid().ToString("n")[..10]);
    private readonly GeoParquetSources _sources;

    public AttachedDuckDbSourcesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "istanbul"));

        // Degrees: points from 28.6 to 28.9 east, 41.0 north. And the same points in metres, as a
        // projected file would hold them, for the table whose declared reference is wrong.
        WriteDatabase(Path.Combine(_root, "istanbul", "places.duckdb"), i => (28.6 + (i * 0.1), 41.0));
        WriteDatabase(Path.Combine(_root, "istanbul", "metres.duckdb"), i => (3183000 + (i * 10000), 5012000));
        File.WriteAllText(Path.Combine(_root, "istanbul", "notes.txt"), "not a database");

        _sources = new GeoParquetSources(_root, "128MB", 1, motherDuckDirectory: Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        _sources.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteDatabase(string path, Func<int, (double X, double Y)> at)
    {
        using DuckDBConnection writer = new($"DataSource={path.Replace('\\', '/')}");
        writer.Open();

        using DuckDBCommand create = writer.CreateCommand();
        create.CommandText = "create table places (objectid bigint, name varchar, geom geometry)";
        create.ExecuteNonQuery();

        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = at(i);
            using DuckDBCommand insert = writer.CreateCommand();
            insert.CommandText = FormattableString.Invariant($"insert into places values ({i + 1}, 'p{i + 1}', 'POINT({x} {y})'::geometry)");
            insert.ExecuteNonQuery();
        }
    }

    private static LayerPublication Publication(int srid, string table = "places") => new(
        "places", Guid.NewGuid(), "main", table, "geom", "objectid", "objectid", srid, GeometryKind.Point, SharingScope.Private);

    [Fact]
    public void A_file_inside_the_root_is_located_with_its_reference_and_nothing_else_is()
    {
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 4326, out string? locator, out string? why), why);
        Assert.Equal(DataSourceKinds.DuckDb, GeoParquetLocator.KindOf(locator));
        Assert.True(GeoParquetLocator.IsAttached(locator));

        Graticula.Providers.DuckDb.AttachedDuckDb parsed = GeoParquetSources.ParseAttached(locator!);
        Assert.Equal(4326, parsed.DeclaredSrid);
        Assert.EndsWith("istanbul/places.duckdb", GeoParquetSources.LocationOf(locator!), StringComparison.Ordinal);

        foreach ((string path, int? srid, string because) in new (string, int?, string)[]
        {
            ("../elsewhere.duckdb", 4326, "outside"),
            ("istanbul/notes.txt", 4326, ".duckdb file"),
            ("istanbul/missing.duckdb", 4326, "no file"),
            ("istanbul/places.duckdb", 0, "EPSG code"),
            ("", 4326, "A file is required"),
        })
        {
            Assert.False(_sources.TryLocateDuckDb(path, srid, out _, out why), path);
            Assert.Contains(because, why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_probe_lists_a_file_s_tables_on_the_declared_reference()
    {
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 4326, out string? locator, out _));

        ProbeResult probe = _sources.Probe(locator!);

        Assert.Equal(ProbeOutcome.Usable, probe.Outcome);
        Assert.Contains("1 table", probe.Message, StringComparison.Ordinal);
        Assert.Empty(probe.Skipped ?? []);

        SourceTable table = Assert.Single(probe.Tables);
        Assert.Equal(("main", "places", "geom", 4326, "Point"), (table.SchemaName, table.TableName, table.GeometryColumn, table.Srid, table.GeometryType));
        Assert.Equal("objectid", table.CandidateObjectIdColumn);
    }

    [Fact]
    public async Task Coordinates_inside_the_declared_reference_s_area_publish()
    {
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 4326, out string? locator, out _));

        Assert.Null(_sources.RefusalFor(locator!, Publication(4326)));
        Assert.Null(await _sources.DeclaredReferenceRefusalAsync(locator!, Publication(4326), new AreasOfUse(), CancellationToken.None));
    }

    [Fact]
    public async Task Metres_declared_as_degrees_are_refused()
    {
        // The failure the check exists for: a projected file registered as EPSG:4326.
        Assert.True(_sources.TryLocateDuckDb("istanbul/metres.duckdb", 4326, out string? locator, out _));

        string? refused = await _sources.DeclaredReferenceRefusalAsync(locator!, Publication(4326), new AreasOfUse(), CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Contains("area of use", refused, StringComparison.Ordinal);
        Assert.Contains("EPSG:4326", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinates_that_cannot_be_moved_out_of_the_declared_reference_are_refused()
    {
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 2320, out string? locator, out _));

        string? refused = await _sources.DeclaredReferenceRefusalAsync(locator!, Publication(2320), new AreasOfUse(), CancellationToken.None);

        Assert.Contains("cannot be in EPSG:2320", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_reference_with_no_known_area_of_use_is_refused()
    {
        // A security review: the one declaration the check used to wave through.
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 32635, out string? locator, out _));

        string? refused = await _sources.DeclaredReferenceRefusalAsync(locator!, Publication(32635), new AreasOfUse(), CancellationToken.None);

        Assert.Contains("no area of use", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_publication_in_another_reference_than_the_registration_s_says_where_to_change_it()
    {
        Assert.True(_sources.TryLocateDuckDb("istanbul/places.duckdb", 4326, out string? locator, out _));

        Assert.Contains("registered with", _sources.RefusalFor(locator!, Publication(3857)), StringComparison.Ordinal);
    }

    [Fact]
    public void MotherDuck_is_located_by_name_and_token_and_never_shows_the_token()
    {
        const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJl";

        Assert.True(_sources.TryLocateMotherDuck("graticula_demo", Token, null, out string? locator, out string? why), why);
        Assert.Equal(DataSourceKinds.MotherDuck, GeoParquetLocator.KindOf(locator));
        Assert.Equal("md:graticula_demo", GeoParquetSources.LocationOf(locator!));
        Assert.DoesNotContain(Token, GeoParquetSources.ParseAttached(locator!).ToString(), StringComparison.Ordinal);
        Assert.Equal(Token, GeoParquetSources.ParseAttached(locator!).Token);

        foreach ((string? database, string? token, string because) in new (string?, string?, string)[]
        {
            ("graticula-demo", Token, "database name"),
            ("graticula_demo", "", "access token"),
            ("graticula_demo", "not a token", "access token"),
            (null, Token, "database name"),
        })
        {
            Assert.False(_sources.TryLocateMotherDuck(database, token, null, out _, out why));
            Assert.Contains(because, why, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Token, new DataSourceRequest("md", null, Database: "graticula_demo", Kind: "motherduck", Token: Token).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MotherDuck_off_says_which_setting_turns_it_on()
    {
        using GeoParquetSources off = new(_root, "128MB", 1);

        Assert.False(off.MotherDuckEnabled);
        Assert.False(off.TryLocateMotherDuck("graticula_demo", "a.b.c", null, out _, out string? why));
        Assert.Contains("Graticula:MotherDuck", why, StringComparison.Ordinal);
    }

    /// <summary>Areas of use for 4326 and 2320, and a projection that fails, as a wrong reference's would.</summary>
    private sealed class AreasOfUse : IProjector
    {
        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the coordinates are outside the projection's domain");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(srid switch
        {
            4326 => new Envelope(-180, -90, 180, 90),
            2320 => new Envelope(26, 36, 45, 42),
            _ => null,
        });

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionProvenance("test", null));
    }
}
