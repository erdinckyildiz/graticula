using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// A DuckDB database file read as layers — ADR-067 §5.3 — through the same feature source as a Parquet file.
/// </summary>
/// <remarks>
/// <b>The file is written the way a user would write one</b>: a table created from a GeoParquet file.
/// Its geometry column keeps a reference while the writer has it open and loses it when the file is
/// reopened — measured on 1.5.5 — which is the reason the registration declares one.
/// </remarks>
public sealed class AttachedDuckDbTests : IDisposable
{
    private readonly TemporaryFolder _temporary = new();
    private readonly string _file;

    public AttachedDuckDbTests()
    {
        string parquet = _temporary.File("grid.parquet");
        GeoParquetFixture.Write(parquet, Shapes.GridColumns, Shapes.Grid(10), srid: 3857);

        _file = _temporary.File("cadastre.duckdb").Replace('\\', '/');

        using DuckDBConnection writer = new($"DataSource={_file}");
        writer.Open();
        Execute(writer, $"create table grid as select * exclude (geom_bbox) from read_parquet('{parquet.Replace('\\', '/')}')");
        Execute(writer, "create table blobs as select objectid, st_aswkb(geom) as wkb from grid");
        Execute(writer, "create schema other");
        Execute(writer, "create table other.hidden as select * from grid");
        Execute(writer, "create view grid_view as select * from grid");

        // A column named rowid hides DuckDB's own; one table with a unique id beside it, one without.
        Execute(writer, "create table shadowed as select objectid, objectid % 3 as rowid, geom from grid");
        Execute(writer, "create table shadowed_only as select objectid % 3 as rowid, name, geom from grid");
        Execute(writer, "checkpoint");
    }

    public void Dispose() => _temporary.Dispose();

    private static GeoParquetOptions Options() => new() { MemoryLimit = "256MB", Threads = 2 };

    private GeoParquetFolder Open(int? declared = 3857) => new(new AttachedDuckDb(_file, null, null, declared), Options());

    [Fact]
    public void The_main_schema_s_tables_are_listed_with_the_declared_reference()
    {
        using GeoParquetFolder database = Open();

        IReadOnlyList<GeoParquetTable> tables = database.List();

        // Views and other schemas are not listed; the BLOB table is, with its reason.
        Assert.Equal(["blobs", "grid", "shadowed", "shadowed_only"], tables.Select(t => t.Name));

        GeoParquetTable grid = tables.Single(t => t.Name == "grid");
        Assert.Null(grid.Problem);
        Assert.Equal(3857, grid.Geometry.Srid);
        Assert.True(grid.SridDeclared);
        Assert.Equal(100, grid.Rows);
        Assert.Equal(GeometryKind.Polygon, grid.Kind);
        Assert.Equal("objectid", grid.CandidateObjectIdColumn);
        Assert.Contains(GeoParquetFolder.RowNumberColumn, grid.IdentityCandidates);
        Assert.DoesNotContain(grid.Columns, c => c.Type.StartsWith("GEOMETRY", StringComparison.Ordinal));

        Assert.Contains("st_geomfromwkb", tables.Single(t => t.Name == "blobs").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_column_named_rowid_takes_the_row_number_off_the_table()
    {
        // A security review: the alias would name somebody's data, which need not be unique.
        using GeoParquetFolder database = Open();

        GeoParquetTable shadowed = database.Find("shadowed")!;
        Assert.Null(shadowed.Problem);
        Assert.Equal(["objectid"], shadowed.IdentityCandidates);

        GeoParquetTable only = database.Find("shadowed_only")!;
        Assert.Contains("column named rowid", only.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_declared_reference_a_table_says_why_it_cannot_be_published()
    {
        using GeoParquetFolder database = Open(declared: null);

        GeoParquetTable grid = database.Find("grid")!;

        Assert.Contains("carries no reference", grid.Problem, StringComparison.Ordinal);
        Assert.Null(grid.Geometry.Srid);
    }

    [Fact]
    public async Task A_table_answers_the_feature_source_as_a_file_does()
    {
        using GeoParquetFolder database = Open();

        GeoParquetFeatureSource source = new(
            database, new LayerDefinition("grid", "main", "grid", "geom", 3857, "objectid", "objectid", isHosted: false),
            new ShiftingProjector());

        Assert.True(WhereClause.TryParse(
            "kind = 'forest'", ["objectid", "name", "kind", "area", "day", "score"], n => $"\"{n}\"",
            out ParsedWhere forest, out string? error), error);

        Assert.Equal(100, await source.CountAsync(new FeatureQuery(1000), CancellationToken.None));
        Assert.Equal(34, await source.CountAsync(new FeatureQuery(1000, where: forest), CancellationToken.None));

        // The grid's squares are two units apart; a box over the first three columns of the first row meets three.
        Assert.Equal(3, await source.CountAsync(new FeatureQuery(1000, boundingBox: new Envelope(0, 0, 5, 0.5)), CancellationToken.None));

        LayerDescription described = await source.DescribeAsync(CancellationToken.None);
        Assert.Equal(new Envelope(0, 0, 19, 19), described.Extent);
    }

    [Fact]
    public async Task A_table_with_no_unique_column_is_identified_by_its_row_id()
    {
        using GeoParquetFolder database = Open();

        GeoParquetFeatureSource source = new(
            database,
            new LayerDefinition("grid", "main", "grid", "geom", 3857, GeoParquetFolder.RowNumberColumn, GeoParquetFolder.RowNumberColumn, isHosted: false),
            new ShiftingProjector());

        IReadOnlyList<long> ids = await source.ObjectIdsAsync(new FeatureQuery(1000, boundingBox: new Envelope(0, 0, 5, 0.5)), CancellationToken.None);

        Assert.Equal([0L, 1L, 2L], ids.Order());
    }

    [Fact]
    public void The_instance_reads_its_file_and_nothing_else_and_never_writes()
    {
        using GeoParquetFolder database = Open();
        using DuckDBConnection connection = database.Open();

        Assert.Equal(100L, Scalar(connection, "select count(*) from src.main.grid"));

        foreach (string refused in (string[])
            [
                $"select count(*) from read_parquet('{_temporary.File("grid.parquet").Replace('\\', '/')}')",
                "insert into src.main.grid select * from src.main.grid limit 1",
                $"attach '{_temporary.File("other.duckdb").Replace('\\', '/')}' as other",
                "set enable_external_access = true",
            ])
        {
            DuckDBException failure = Assert.Throws<DuckDBException>(() => Scalar(connection, refused));
            Assert.True(
                failure.Message.Contains("disabled by configuration", StringComparison.OrdinalIgnoreCase)
                || failure.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase)
                || failure.Message.Contains("locked", StringComparison.OrdinalIgnoreCase),
                $"{refused}: {failure.Message}");
        }
    }

    [Fact]
    public void MotherDuck_without_a_token_is_refused_before_DuckDB_is_asked()
    {
        // With no token the extension opens a browser and waits a minute (ADR-067 §4).
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            new GeoParquetFolder(new AttachedDuckDb(null, "graticula_demo", "", null), Options() with { MotherDuckExtension = "/nowhere/motherduck.duckdb_extension" }));

        Assert.Contains("token", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MotherDuck_s_token_is_global_set_before_the_lock_and_never_a_secret()
    {
        IReadOnlyList<string> settings = GeoParquetFolder.AttachedSettings(
            new AttachedDuckDb(null, "graticula_demo", "header.payload.signature", null),
            Options() with { MotherDuckExtension = "/ext/motherduck.duckdb_extension" });

        int token = settings.ToList().FindIndex(s => s.StartsWith("set global motherduck_token", StringComparison.Ordinal));
        int closed = settings.ToList().IndexOf("set enable_external_access = false");

        Assert.True(token >= 0 && token < closed, string.Join("\n", settings));
        Assert.Equal("set lock_configuration = true", settings[^1]);
        Assert.DoesNotContain(settings, s => s.Contains("create secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("md:graticula_demo", new AttachedDuckDb(null, "graticula_demo", "header.payload.signature", null).ToString());
    }

    [Theory]
    [InlineData("GEOMETRY", null, false)]
    [InlineData("GEOMETRY('OGC:CRS84')", 4326, false)]
    [InlineData("GEOMETRY('EPSG:3857')", 3857, false)]
    [InlineData("GEOMETRY('EPSG:abc')", null, true)]
    [InlineData("GEOMETRY('local grid')", null, true)]
    public void A_reference_is_read_from_the_column_type(string type, int? srid, bool unusable)
    {
        Assert.Equal(srid, GeoParquetFolder.SridOfType(type, out string? why));
        Assert.Equal(unusable, why is not null);
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
