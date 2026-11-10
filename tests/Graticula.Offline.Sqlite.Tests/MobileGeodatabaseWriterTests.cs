using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Graticula.Offline.Sqlite.Tests;

/// <summary>
/// A whole offline copy is written as the server's files are: the structure research note §3 and §4 describe — ADR-288.
/// </summary>
/// <remarks>
/// These read the file back with plain SQLite. Whether ArcGIS reads it is a different question, answered by ArcGIS Pro
/// against files <see cref="OracleFiles"/> writes (<c>experiments/mobile-geodatabase/read_arcpy.py</c>).
/// </remarks>
public sealed class MobileGeodatabaseWriterTests
{
    private static readonly string[] LayerTables =
        ["graticula_test_offline_points", "graticula_test_offline_lines", "graticula_test_offline_areas", "graticula_test_offline_inspections"];

    private static async Task<string> Write(bool registered)
    {
        string path = OfflineFixture.NewPath(registered ? "registered.geodatabase" : "snapshot.geodatabase");
        await new SqliteMobileGeodatabaseWriter().WriteAsync(OfflineFixture.Copy(registered), path, CancellationToken.None);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_file_is_a_mobile_geodatabase(bool registered)
    {
        string path = await Write(registered);
        using SqliteConnection db = OfflineFixture.Open(path);

        Assert.Equal(1165193833L, OfflineFixture.Scalar(db, "PRAGMA application_id"));
        Assert.Equal(4096L, OfflineFixture.Scalar(db, "PRAGMA page_size"));
        Assert.Equal("Graticula", OfflineFixture.Scalar(db, "select description from GDB_Version"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_system_table_and_layer_table_is_there(bool registered)
    {
        string path = await Write(registered);
        using SqliteConnection db = OfflineFixture.Open(path);
        var tables = OfflineFixture.Query(db, "select name from sqlite_master where type = 'table'").Select(static r => (string)r[0]!).ToHashSet();

        foreach ((string name, _) in MobileGeodatabaseSchema.SystemTables)
        {
            Assert.Contains(name, tables);
        }

        foreach (string table in LayerTables)
        {
            Assert.Contains(table, tables);
        }

        Assert.Contains("graticula_test_offline_points__ATTACH", tables);
        Assert.DoesNotContain("graticula_test_offline_lines__ATTACH", tables);
        Assert.DoesNotContain("GDB_DataChanges", tables);
        Assert.Contains("st_spindex__graticula_test_offline_points_shape", tables);
        Assert.DoesNotContain("st_spindex__graticula_test_offline_inspections_shape", tables);
    }

    [Fact]
    public async Task The_two_esri_module_tables_are_schema_rows()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        List<object?[]> rows = OfflineFixture.Query(db, "select name, rootpage, sql from sqlite_master where name in ('st_spatial_reference_systems', 'st_vtspindex_interface') order by name");

        Assert.Equal(2, rows.Count);
        Assert.Equal("CREATE VIRTUAL TABLE st_spatial_reference_systems USING VSRS()", rows[0][2]);
        Assert.Equal("CREATE VIRTUAL TABLE st_vtspindex_interface USING VTSpindex()", rows[1][2]);
        Assert.All(rows, static r => Assert.Equal(0L, r[1]));
    }

    [Theory]
    [InlineData(false, 11)]
    [InlineData(true, 17)]
    public async Task The_catalogue_has_one_item_per_thing(bool registered, int count)
    {
        string path = await Write(registered);
        using SqliteConnection db = OfflineFixture.Open(path);

        var types = OfflineFixture.Query(db, "select t.Name, count(*) from GDB_Items i join GDB_ItemTypes t on t.UUID = i.Type group by t.Name")
            .ToDictionary(static r => (string)r[0]!, static r => (long)r[1]!);

        Assert.Equal(count, types.Values.Sum());
        Assert.Equal(1, types["Folder"]);
        Assert.Equal(1, types["Workspace"]);
        Assert.Equal(1, types["Historical Marker"]);
        Assert.Equal(1, types["Coded Value Domain"]);
        Assert.Equal(1, types["Range Domain"]);
        Assert.Equal(3, types["Feature Class"]);
        Assert.Equal(2, types["Table"]);
        Assert.Equal(1, types["Relationship Class"]);
        Assert.Equal(registered ? 1 : 0, types.GetValueOrDefault("Sync Replica"));
        Assert.Equal(registered ? 5 : 0, types.GetValueOrDefault("Sync Dataset"));
        Assert.Equal((long)MobileGeodatabaseSchema.ItemTypes.Length, OfflineFixture.Scalar(db, "select count(*) from GDB_ItemTypes"));
        Assert.Equal((long)MobileGeodatabaseSchema.ItemRelationshipTypes.Length, OfflineFixture.Scalar(db, "select count(*) from GDB_ItemRelationshipTypes"));
    }

    [Fact]
    public async Task A_dataset_DSID_is_its_own_item_object_id()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        foreach (object?[] r in OfflineFixture.Query(db, "select ObjectID, Definition from GDB_Items where Definition like '%<DSID>%'"))
        {
            Assert.Contains($"<DSID>{r[0]}</DSID>", (string)r[1]!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Domains_are_items_and_each_field_names_its_own()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        string coded = (string)OfflineFixture.Scalar(db, $"select Definition from GDB_Items where Name = '{OfflineFixture.Kind.Name}'")!;
        Assert.Contains("<Name>Hydrant</Name><Code xsi:type='xs:short'>1</Code>", coded, StringComparison.Ordinal);
        string range = (string)OfflineFixture.Scalar(db, $"select Definition from GDB_Items where Name = '{OfflineFixture.Size.Name}'")!;
        Assert.Contains("<MaxValue xsi:type='xs:double'>2000</MaxValue><MinValue xsi:type='xs:double'>0</MinValue>", range, StringComparison.Ordinal);

        string points = (string)OfflineFixture.Scalar(db, "select Definition from GDB_Items where Name = 'main.graticula_test_offline_points'")!;
        Assert.Contains($"<Name>kind</Name><AliasName>Kind</AliasName><DomainName>{OfflineFixture.Kind.Name}</DomainName>", points, StringComparison.Ordinal);
        Assert.Equal(6L, OfflineFixture.Scalar(db, "select count(*) from GDB_ItemRelationships r join GDB_ItemRelationshipTypes t on t.UUID = r.Type where t.Name = 'DomainInDataset'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_columns_and_views_are_there_only_when_registered(bool registered)
    {
        string path = await Write(registered);
        using SqliteConnection db = OfflineFixture.Open(path);

        foreach (string table in LayerTables.Append("graticula_test_offline_points__ATTACH"))
        {
            var columns = OfflineFixture.Query(db, $"PRAGMA table_info([{table}])").Select(static r => (string)r[1]!).ToList();
            Assert.Equal(registered, columns.Contains("gdb_from_date"));
            Assert.Equal(registered, columns.Contains("gdb_to_date"));
            Assert.Equal(registered, columns.Contains("gdb_archive_oid"));
            Assert.Equal(registered ? 1L : 0L, OfflineFixture.Scalar(db, $"select count(*) from sqlite_master where type = 'view' and name = '{table}_evw'"));
            Assert.Equal(registered ? 6L : 0L, OfflineFixture.Scalar(db, $"select count(*) from sqlite_master where type = 'trigger' and name like '{table}_evw%'"));
        }

        Assert.Equal(registered ? 15L : 0L, OfflineFixture.Scalar(db, "select count(*) from GDB_ColumnRegistry where column_name like 'gdb\\_%' escape '\\'"));
        Assert.Equal(
            registered ? 0x4007 | 0x40000 : 0x4007,
            Convert.ToInt32(OfflineFixture.Scalar(db, "select object_flags from GDB_TableRegistry where table_name = 'graticula_test_offline_points'")));
    }

    [Fact]
    public async Task An_archived_row_is_open_and_began_before_any_edit()
    {
        string path = await Write(registered: true);
        using SqliteConnection db = OfflineFixture.Open(path);

        object?[] row = OfflineFixture.Query(db, "select gdb_from_date, gdb_to_date from graticula_test_offline_points where objectid = 1")[0];

        Assert.Equal(2440587.500011574, (double)row[0]!, 9);
        Assert.Equal(5373484.499988426, (double)row[1]!, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_geometry_reads_back_through_the_codec(bool registered)
    {
        string path = await Write(registered);
        using SqliteConnection db = OfflineFixture.Open(path);
        CoordinateGrid grid = CoordinateGrid.WebMercator;

        foreach ((string table, IReadOnlyList<OfflineRow> rows) in new[]
        {
            ("graticula_test_offline_points", OfflineFixture.Points),
            ("graticula_test_offline_lines", OfflineFixture.Lines),
            ("graticula_test_offline_areas", OfflineFixture.Areas),
        })
        {
            var stored = OfflineFixture.Query(db, $"select objectid, shape from {table} order by objectid");
            Assert.Equal(rows.Count, stored.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Geometry read = MobileGeodatabaseGeometry.Decode((byte[])stored[i][1]!, grid, out int srid);
                Assert.Equal(3857, srid);
                Assert.Equal(rows[i].Geometry!.Envelope, read.Envelope);
                Assert.Equal(rows[i].Geometry!.CoordinateCount, read.CoordinateCount);
            }
        }

        Geometry area = MobileGeodatabaseGeometry.Decode(
            (byte[])OfflineFixture.Scalar(db, "select shape from graticula_test_offline_areas")!, grid, out _);
        Polygon polygon = Assert.IsType<Polygon>(area);
        Assert.Single(polygon.Holes);
        Assert.True(polygon.Shell.IsCounterClockwise, "A shell is stored counter-clockwise, as OGC winds it.");
        Assert.False(polygon.Holes[0].IsCounterClockwise);

        Point point = Assert.IsAssignableFrom<Point>(MobileGeodatabaseGeometry.Decode(
            (byte[])OfflineFixture.Scalar(db, "select shape from graticula_test_offline_points where objectid = 3")!, grid, out _));
        Assert.Equal(13.5, point.Z);
    }

    [Fact]
    public async Task The_spatial_index_holds_every_feature_and_the_layer_its_extent()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        Assert.Equal(5L, OfflineFixture.Scalar(db, "select count(*) from st_spindex__graticula_test_offline_points_shape"));
        Assert.Equal(2L, OfflineFixture.Scalar(db, "select count(*) from st_spindex__graticula_test_offline_lines_shape"));
        object?[] layer = OfflineFixture.Query(db, "select minx, miny, maxx, maxy, minz, maxz, srid, eflags from GDB_Layers where table_name = 'graticula_test_offline_points'")[0];
        Assert.Equal(new object?[] { 3101000.0, 4700500.0, 3105000.0, 4702500.0, 11.5, 15.5, 3857L, (long)0x4410083 }, layer);
        Assert.Equal(1001L, OfflineFixture.Scalar(db, "select geometry_type from st_geometry_columns where f_table_name = 'graticula_test_offline_points'"));
        Assert.Equal(3L, OfflineFixture.Scalar(db, "select count(*) from st_spindex__GDB_Items_Shape"));
    }

    [Fact]
    public async Task The_item_shape_of_a_feature_class_is_its_extent_in_WGS_84()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        byte[] blob = (byte[])OfflineFixture.Scalar(db, "select Shape from GDB_Items where Name = 'main.graticula_test_offline_points'")!;
        Geometry shape = MobileGeodatabaseGeometry.Decode(blob, CoordinateGrid.Geographic, out int srid);

        Assert.Equal(4326, srid);
        Assert.InRange(shape.Envelope.MinX, 27.85, 27.86);
        Assert.InRange(shape.Envelope.MinY, 38.8, 39.0);
    }

    [Fact]
    public async Task Rows_keep_their_values_and_types()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        object?[] row = OfflineFixture.Query(db, "select globalid, name, kind, size_mm, seen, count_, typeof(seen) from graticula_test_offline_points where objectid = 2")[0];
        Assert.Equal("{00000000-0000-4000-8000-000000000002}", row[0]);
        Assert.Equal("point 2", row[1]);
        Assert.Equal(1L, row[2]);
        Assert.Equal(201.0, row[3]);
        Assert.Equal("real", row[6]);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 8, 30, 0, TimeSpan.Zero), FileValues.FromJulian((double)row[4]!));

        Assert.Null(OfflineFixture.Scalar(db, "select asset_gid from graticula_test_offline_inspections where objectid = 2"));
        Assert.Equal(6L, OfflineFixture.Scalar(db, "select base_id from GDB_RowidGenerators r join GDB_TableRegistry t on t.registration_id = r.registration_id where t.table_name = 'graticula_test_offline_points'"));
    }

    [Fact]
    public async Task Attachments_are_keyed_by_their_feature()
    {
        string path = await Write(registered: true);
        using SqliteConnection db = OfflineFixture.Open(path);

        var rows = OfflineFixture.Query(db, "select attachmentid, rel_globalid, att_name, content_type, data_size, data from graticula_test_offline_points__ATTACH order by attachmentid");
        Assert.Equal(2, rows.Count);
        Assert.Equal("{00000000-0000-4000-8000-000000000001}", rows[0][1]);
        Assert.Equal("note.txt", rows[1][2]);
        Assert.Equal(5L, rows[1][4]);
        Assert.Equal("hello"u8.ToArray(), (byte[])rows[1][5]!);
        Assert.Equal(1L, OfflineFixture.Scalar(db, "select count(*) from GDB_Items where Name = 'main.graticula_test_offline_points__ATTACHREL'"));
    }

    [Fact]
    public async Task A_registered_copy_names_its_replica()
    {
        string path = await Write(registered: true);
        using SqliteConnection db = OfflineFixture.Open(path);

        object?[] item = OfflineFixture.Query(db, "select UUID, Name, DatasetInfo1, DatasetInfo2, Definition from GDB_Items where Name = 'graticula_offline_test'")[0];
        Assert.Equal("{5B1F0C3E-6A6D-4F7B-9D7E-2B0A8F1C4E21}", item[0]);
        Assert.Equal(OfflineFixture.Replica.ServiceUrl, item[2]);
        Assert.Equal("tester", item[3]);
        Assert.Contains("<ReplicaID>5B1F0C3E-6A6D-4F7B-9D7E-2B0A8F1C4E21</ReplicaID>", (string)item[4]!, StringComparison.Ordinal);
        Assert.Contains("<ReplicaServerGen xsi:type='xs:long'>1794288316868</ReplicaServerGen>", (string)item[4]!, StringComparison.Ordinal);
        Assert.Equal(10L, OfflineFixture.Scalar(db, "select count(*) from GDB_ItemRelationships r join GDB_ItemRelationshipTypes t on t.UUID = r.Type where t.Name in ('SyncDatasetInSyncReplica', 'DatasetOfSyncDataset')"));
    }

    [Fact]
    public async Task The_service_and_each_layer_document_are_service_items()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        var rows = OfflineFixture.Query(db, "select ItemType, ItemId, DatasetName from GDB_ServiceItems order by ObjectID");
        Assert.Equal(5, rows.Count);
        Assert.Equal(new object?[] { 0L, -1L, "graticula_test_offline" }, rows[0]);
        Assert.Equal(new object?[] { 1L, 3L, "graticula_test_offline_inspections" }, rows[4]);
    }

    [Fact]
    public async Task The_spatial_index_triggers_exist_and_were_made_after_the_rows()
    {
        string path = await Write(registered: false);
        using SqliteConnection db = OfflineFixture.Open(path);

        Assert.Equal(4L, OfflineFixture.Scalar(db, "select count(*) from sqlite_master where type = 'trigger' and tbl_name = 'graticula_test_offline_points'"));
        Assert.Equal(4L, OfflineFixture.Scalar(db, "select count(*) from sqlite_master where type = 'trigger' and tbl_name = 'GDB_Items'"));
    }

    [Fact]
    public async Task An_existing_file_is_not_overwritten()
    {
        string path = OfflineFixture.NewPath("exists.geodatabase");
        await File.WriteAllTextAsync(path, "x");

        await Assert.ThrowsAsync<IOException>(() =>
            new SqliteMobileGeodatabaseWriter().WriteAsync(OfflineFixture.Copy(false), path, CancellationToken.None));
        Assert.Equal("x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_field_name_that_is_not_an_identifier_is_refused()
    {
        OfflineLayer layer = OfflineFixture.InspectionsLayer with
        {
            Fields = [.. OfflineFixture.TableFields.Take(2), new OfflineField("name); drop table x;--", "esriFieldTypeString", null, 10, true, null)],
        };
        var copy = new OfflineCopy(OfflineFixture.Service, "{}", [layer], null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SqliteMobileGeodatabaseWriter().WriteAsync(copy, OfflineFixture.NewPath("bad.geodatabase"), CancellationToken.None));
    }

    /// <summary>
    /// Writes the fixture where ArcGIS Pro can read it, when <c>GRATICULA_OFFLINE_CHECK_DIR</c> names a folder: the
    /// oracle check of ADR-288 §4 is Pro, run by hand, on these files.
    /// </summary>
    [Fact]
    public async Task OracleFiles()
    {
        string? folder = Environment.GetEnvironmentVariable("GRATICULA_OFFLINE_CHECK_DIR");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var writer = new SqliteMobileGeodatabaseWriter();
        foreach ((string name, bool registered) in new[] { ("snapshot.geodatabase", false), ("registered.geodatabase", true) })
        {
            string path = Path.Combine(folder, name);
            File.Delete(path);
            await writer.WriteAsync(OfflineFixture.Copy(registered), path, CancellationToken.None);
        }

        string delta = Path.Combine(folder, "delta.geodatabase");
        File.Delete(delta);
        await writer.WriteDeltaAsync(DeltaTests.Delta(), delta, CancellationToken.None);
    }
}
