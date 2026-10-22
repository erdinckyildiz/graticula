using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// A DuckDB table or a Parquet file with no geometry, served as a table — ADR-213 condition 7: listed as one, described
/// with no extent, read without shapes whatever filter is sent, and, in a database file, edited with its attributes alone.
/// </summary>
public sealed class DuckDbTableTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryFolder _temporary = new();
    private readonly string _file;

    public DuckDbTableTests()
    {
        _file = _temporary.File("tables.duckdb").Replace('\\', '/');

        using DuckDBConnection writer = new($"DataSource={_file}");
        writer.Open();
        Execute(writer, "create sequence visits_id start 1");
        Execute(writer, "create table visits (objectid bigint primary key default nextval('visits_id'), site_code varchar, score integer)");
        Execute(writer, "insert into visits (site_code, score) values ('A1', 4), ('A1', 2), ('B2', 5)");
        Execute(writer, "create table kept_as_bytes (objectid bigint primary key, wkb_geometry blob)");
        Execute(writer, "checkpoint");
    }

    public void Dispose() => _temporary.Dispose();

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private GeoParquetFolder Open() => new(new AttachedDuckDb(_file, null, null, 4326), new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });

    private static LayerDefinition Table(string name) =>
        new(name, "main", name, null, LayerDefinition.TableSrid, "objectid", "objectid", isHosted: false);

    private static FeatureQuery Everything() =>
        new(10, boundingBox: new Envelope(0, 0, 1, 1), includeGeometry: true, spatial: new SpatialFilter(new Point(50, 50)));

    private static async Task<List<Feature>> ReadAsync(GeoParquetFeatureSource source, FeatureQuery query)
    {
        List<Feature> rows = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            rows.Add(feature);
        }

        return rows;
    }

    [Fact]
    public void A_table_in_a_database_file_is_listed_as_a_table_and_one_keeping_geometry_as_bytes_is_not()
    {
        using GeoParquetFolder database = Open();

        GeoParquetTable visits = Assert.Single(database.List(), t => t.Name == "visits");
        Assert.True(visits.IsTable);
        Assert.Null(visits.Problem);
        Assert.Equal("objectid", visits.CandidateObjectIdColumn);
        Assert.Equal(["objectid", "site_code", "score"], visits.Columns.Select(c => c.Name));

        GeoParquetTable bytes = Assert.Single(database.List(), t => t.Name == "kept_as_bytes");
        Assert.False(bytes.IsTable);
        Assert.Contains("st_geomfromwkb", bytes.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_is_described_without_an_extent_and_read_without_shapes_whatever_the_filter()
    {
        using GeoParquetFolder database = Open();
        GeoParquetFeatureSource source = new(database, Table("visits"), new ShiftingProjector());

        LayerDescription described = await source.DescribeAsync(None);

        Assert.Null(described.Extent);
        Assert.True(described.Writable);

        // A box and a relation are ignored, as ArcGIS ignores them on a table; every row comes back with no shape.
        List<Feature> rows = await ReadAsync(source, Everything());

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Geometry));
        Assert.Equal(3, await source.CountAsync(Everything(), None));
        Assert.NotNull(await source.VersionOfAsync(1, None));
        await Assert.ThrowsAsync<QueryNotSupportedException>(() => source.ExtentAsync(new FeatureQuery(10), None));
    }

    [Fact]
    public async Task A_row_is_added_updated_and_deleted_with_its_attributes_and_refused_with_a_shape()
    {
        using GeoParquetFolder database = Open();
        GeoParquetFeatureSource source = new(database, Table("visits"), new ShiftingProjector());
        LayerDescription described = await source.DescribeAsync(None);
        DuckDbFeatureWriter writer = new(database, Table("visits"), described.Fields, new ShiftingProjector());

        EditOutcome added = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = "C3", ["score"] = 1 }, null)], [], []), None);
        EditResult row = Assert.Single(added.Adds);
        Assert.True(row.Succeeded, row.Error);

        EditOutcome changed = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(row.Identity, new Dictionary<string, object?> { ["score"] = 3 }, null)], []), None);
        Assert.True(Assert.Single(changed.Updates).Succeeded);

        EditOutcome shaped = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = "D4" }, new Point(1, 2))], [], []), None);
        EditResult refused = Assert.Single(shaped.Adds);
        Assert.False(refused.Succeeded);
        Assert.Contains("keeps no geometry", refused.Error, StringComparison.Ordinal);

        Assert.Equal(4, await source.CountAsync(new FeatureQuery(10), None));

        EditOutcome deleted = await writer.ApplyAsync(new EditBatch([], [], [row.Identity]), None);
        Assert.True(Assert.Single(deleted.Deletes).Succeeded);
        Assert.Equal(3, await source.CountAsync(new FeatureQuery(10), None));
    }

    [Fact]
    public async Task A_Parquet_file_with_no_geometry_is_read_as_a_table()
    {
        using (DuckDBConnection plain = new("DataSource=:memory:"))
        {
            plain.Open();
            Execute(plain, $"copy (select i::bigint as objectid, 'site ' || i as label from range(1, 6) t(i)) "
                + $"to '{_temporary.File("readings.parquet").Replace('\\', '/')}' (format parquet)");
        }

        using GeoParquetFolder folder = new(_temporary.Path, new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });

        GeoParquetTable readings = Assert.Single(folder.List(), t => t.Name == "readings");
        Assert.True(readings.IsTable);
        Assert.Null(readings.Problem);
        Assert.Equal("objectid", readings.CandidateObjectIdColumn);

        GeoParquetFeatureSource source = new(folder, Table("readings"), new ShiftingProjector());
        List<Feature> rows = await ReadAsync(source, Everything());

        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Geometry));
        Assert.Null((await source.DescribeAsync(None)).Extent);
    }
}
