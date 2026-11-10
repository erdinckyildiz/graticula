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
/// A delta is written as the server's download deltas are, and read back to the same changes — ADR-288, research
/// note §5.
/// </summary>
public sealed class DeltaTests
{
    internal static OfflineDelta Delta() => new(
        OfflineFixture.ReplicaId,
        1794288415192,
        [
            new OfflineLayerChanges(
                OfflineFixture.PointsLayer,
                Inserts: [new OfflineRow(OfflineFixture.Values(0, 6, "server_add"), Point.Create(3150000, 4750000, 42, null))],
                Updates: [new OfflineRow(OfflineFixture.Values(0, 2, "server_upd"), Point.Create(3101000, 4700500, 11.5, null))],
                Deletes: [new OfflineKey(5, OfflineFixture.Gid(0, 5))],
                AttachmentInserts: [new OfflineAttachment(3, OfflineFixture.Gid(0, 6), OfflineFixture.Gid(10, 3), "new.png", "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })],
                AttachmentUpdates: [],
                AttachmentDeletes: [new OfflineKey(1, OfflineFixture.Gid(10, 1))]),
            new OfflineLayerChanges(
                OfflineFixture.AreasLayer,
                Inserts: [],
                Updates: [new OfflineRow(OfflineFixture.Values(2, 1, "area moved"), OfflineFixture.Area)],
                Deletes: [],
                AttachmentInserts: [],
                AttachmentUpdates: [],
                AttachmentDeletes: []),
            new OfflineLayerChanges(
                OfflineFixture.InspectionsLayer,
                Inserts: [new OfflineRow([3L, OfflineFixture.Gid(3, 3), "insp_server", OfflineFixture.Gid(0, 6)], null)],
                Updates: [],
                Deletes: [],
                AttachmentInserts: [],
                AttachmentUpdates: [],
                AttachmentDeletes: []),
        ],
        OfflineFixture.Service);

    private static async Task<string> Write()
    {
        string path = OfflineFixture.NewPath("delta.geodatabase");
        await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(Delta(), path, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task A_delta_has_the_change_tables_and_neither_service_items_nor_archive_work()
    {
        string path = await Write();
        using SqliteConnection db = OfflineFixture.Open(path);
        var tables = OfflineFixture.Query(db, "select name from sqlite_master where type = 'table'").Select(static r => (string)r[0]!).ToHashSet();

        foreach ((string name, _) in MobileGeodatabaseSchema.DeltaTables)
        {
            Assert.Contains(name, tables);
        }

        Assert.DoesNotContain("GDB_ServiceItems", tables);
        Assert.DoesNotContain("GDB_TriggerWork", tables);
        Assert.Contains("st_vtspindex_interface", tables);
        Assert.Equal(1165193833L, OfflineFixture.Scalar(db, "PRAGMA application_id"));
    }

    [Fact]
    public async Task The_change_tables_are_named_and_described_as_the_server_names_them()
    {
        string path = await Write();
        using SqliteConnection db = OfflineFixture.Open(path);

        object?[] changes = OfflineFixture.Query(db, "select ReplicaGUID, ServerGen from GDB_DataChanges")[0];
        Assert.Equal("{5B1F0C3E-6A6D-4F7B-9D7E-2B0A8F1C4E21}", changes[0]);
        Assert.Equal("1794288415192", changes[1]);

        var datasets = OfflineFixture.Query(db, "select ID, Name, Type, LayerId from GDB_DataChangesDatasets order by ID");
        Assert.Equal(
            ["1|graticula_test_offline_points|2|0", "2|graticula_test_offline_points__ATTACH|7|0", "3|graticula_test_offline_areas|2|2", "4|graticula_test_offline_inspections|1|3"],
            datasets.Select(static r => string.Join("|", r)));

        // RecordsetClassID is the change table's own item, as in the server's deltas.
        var deltas = OfflineFixture.Query(db, "select d.ChangeType, d.ChangedDatasetID, i.Name from GDB_DataChangesDeltas d join GDB_Items i on i.ObjectID = d.RecordsetClassID order by d.ID");
        Assert.Equal(
            ["1|1|main.T_1_Inserts", "2|1|main.T_1_Updates", "3|1|main.T_1_Deletes", "1|2|main.T_2_Inserts", "3|2|main.T_2_Deletes", "2|3|main.T_3_Updates", "1|4|main.T_4_Inserts"],
            deltas.Select(static r => string.Join("|", r)));

        var deleteColumns = OfflineFixture.Query(db, "PRAGMA table_info([T_1_Deletes])").Select(static r => (string)r[1]!).ToList();
        Assert.Equal(["OID", "globalid"], deleteColumns);
        Assert.Equal(1L, OfflineFixture.Scalar(db, "select count(*) from st_spindex__T_1_Inserts_shape"));
        Assert.Equal(3L, OfflineFixture.Scalar(db, "select count(*) from GDB_Layers where table_name like 'T\\_%' escape '\\'"));
    }

    [Fact]
    public async Task A_delta_reads_back_to_the_changes_it_was_written_from()
    {
        string path = await Write();
        OfflineDelta written = Delta();

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        Assert.Equal(written.ReplicaId, read.ReplicaId);
        Assert.Equal(written.ServerGeneration, read.ServerGeneration);
        Assert.Equal([0, 2, 3], read.Layers.Select(static l => l.Layer.Id));

        OfflineLayerChanges points = read.Layers[0];
        OfflineRow insert = Assert.Single(points.Inserts);
        Assert.Equal(written.Layers[0].Inserts[0].Values, insert.Values);
        Point at = Assert.IsAssignableFrom<Point>(insert.Geometry);
        Assert.Equal((3150000d, 4750000d, 42d), (at.X, at.Y, at.Z!.Value));
        Assert.Equal("server_upd", Assert.Single(points.Updates).Values[2]);
        Assert.Equal(new OfflineKey(5, OfflineFixture.Gid(0, 5)), Assert.Single(points.Deletes));

        OfflineAttachment attachment = Assert.Single(points.AttachmentInserts);
        Assert.Equal((3L, OfflineFixture.Gid(0, 6), OfflineFixture.Gid(10, 3), "new.png", "image/png"), (attachment.AttachmentId, attachment.ParentGlobalId, attachment.GlobalId, attachment.Name, attachment.ContentType));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, attachment.Data.ToArray());
        Assert.Equal(new OfflineKey(1, OfflineFixture.Gid(10, 1)), Assert.Single(points.AttachmentDeletes));

        Polygon area = Assert.IsType<Polygon>(Assert.Single(read.Layers[1].Updates).Geometry);
        Assert.Equal(OfflineFixture.Area.Envelope, area.Envelope);
        Assert.Single(area.Holes);

        OfflineRow inspection = Assert.Single(read.Layers[2].Inserts);
        Assert.Equal([3L, OfflineFixture.Gid(3, 3), "insp_server", OfflineFixture.Gid(0, 6)], inspection.Values);
        Assert.Null(inspection.Geometry);
    }

    [Fact]
    public async Task Without_the_deltas_table_the_change_tables_are_found_by_name()
    {
        string path = await Write();
        Strip(path, "drop table GDB_DataChangesDeltas");

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        Assert.Single(read.Layers[0].Inserts);
        Assert.Single(read.Layers[0].Deletes);
        Assert.Single(read.Layers[2].Inserts);
    }

    [Fact]
    public async Task A_missing_change_table_means_no_changes_and_an_unknown_table_is_ignored()
    {
        string path = await Write();
        Strip(path, "drop table T_1_Updates; create table T_99_Inserts (x); create table something_else (y)");

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        Assert.Empty(read.Layers[0].Updates);
        Assert.Single(read.Layers[0].Inserts);
    }

    [Fact]
    public async Task A_file_with_none_of_the_delta_tables_is_no_changes()
    {
        string path = OfflineFixture.NewPath("empty.geodatabase");
        await new SqliteMobileGeodatabaseWriter().WriteAsync(OfflineFixture.Copy(false), path, CancellationToken.None);

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        Assert.Equal(Guid.Empty, read.ReplicaId);
        Assert.Empty(read.Layers);
    }

    [Fact]
    public async Task A_dataset_is_matched_by_name_when_it_has_no_layer_id()
    {
        string path = await Write();
        Strip(path, "update GDB_DataChangesDatasets set LayerId = NULL");

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        Assert.Equal([0, 2, 3], read.Layers.Select(static l => l.Layer.Id));
        Assert.Single(read.Layers[0].AttachmentInserts);
    }

    /// <summary>
    /// Reads a download delta ArcGIS Server wrote, when <c>GRATICULA_OFFLINE_CAPTURE_DIR</c> names the folder that holds
    /// one. The captured files stay outside the repository (research note §0), so this runs only where they are.
    /// </summary>
    [Fact]
    public async Task A_delta_the_server_wrote_is_read()
    {
        string? folder = Environment.GetEnvironmentVariable("GRATICULA_OFFLINE_CAPTURE_DIR");
        string path = Path.Combine(folder ?? string.Empty, "sync2.download.geodatabase");
        if (string.IsNullOrEmpty(folder) || !File.Exists(path))
        {
            return;
        }

        OfflineDelta read = await new SqliteMobileGeodatabaseWriter().ReadDeltaAsync(path, OfflineFixture.Layers, CancellationToken.None);

        OfflineLayerChanges points = read.Layers.Single(static l => l.Layer.Id == 0);
        Assert.Equal("server_add", Assert.Single(points.Inserts).Values[2]);
        Assert.Equal(42, Assert.IsAssignableFrom<Point>(points.Inserts[0].Geometry).Z);
        Assert.Equal("server_upd", Assert.Single(points.Updates).Values[2]);
        Assert.Single(points.AttachmentInserts);
        Assert.Equal("insp_server", Assert.Single(read.Layers.Single(static l => l.Layer.Id == 3).Inserts).Values[2]);
    }

    private static void Strip(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        GeodatabaseBuilder.RegisterCollation(db);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
