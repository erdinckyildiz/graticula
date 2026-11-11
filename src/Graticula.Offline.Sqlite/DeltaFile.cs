using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Microsoft.Data.Sqlite;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// The delta file <c>synchronizeReplica</c> moves edits in: a small geodatabase of its own, holding only the changes —
/// ADR-288, research note §5.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was observed</b>, in two downloads ArcGIS Server 12.1 wrote (a delete; an insert, an update, an attachment
/// insert and a table insert): <c>GDB_DataChanges</c> names the replica and the new server generation;
/// <c>GDB_DataChangesDatasets</c> has a row per changed table — its name in the replica, its kind (1 table, 2 feature
/// class, 7 attachment table) and its layer id; <c>GDB_DataChangesDeltas</c> has a row per change table, whose
/// <c>RecordsetClassID</c> is <b>the object id of that change table's own item in <c>GDB_Items</c></b> (the item named
/// <c>main.T_n_Kind</c>), whose change type is 1 insert, 2 update or 3 delete, and whose <c>ChangedDatasetID</c> is the
/// dataset row — the <c>n</c> of <c>T_n_Kind</c>. Inserts and updates are whole rows in the layer's own columns;
/// deletes are <c>OID</c> and <c>globalid</c>.
/// </para>
/// <para>
/// <b>What was not, when this was written</b>: the file a client uploads. The reader assumed it has the same shape, so
/// it is tolerant rather than strict: a table it does not know is ignored, a change
/// table that is missing means no changes of that kind, a dataset is matched to a layer by its layer id and failing
/// that by its name, and a change table is found by its item and failing that by its <c>T_n_Kind</c> name.
/// </para>
/// <para>
/// <b>Since measured</b> (research note §5b, one Field Maps 26.2.2 upload): the same shape, with the change tables named
/// in lower case — matched here without regard to case — and the geometry in a client-local SRID whose grid
/// <c>st_aux_spatial_reference_systems</c> defines, which is the grid a blob is decoded on when the file defines it.
/// </para>
/// </remarks>
internal static partial class DeltaFile
{
    private static readonly string[] Kinds = ["Inserts", "Updates", "Deletes"];

    /// <summary>Writes a delta.</summary>
    public static void Write(OfflineDelta delta, string path, CancellationToken cancellationToken)
    {
        string service = string.IsNullOrEmpty(delta.ServiceName) ? string.Empty : FileValues.TableName(delta.ServiceName);
        string generation = delta.ServerGeneration.ToString(CultureInfo.InvariantCulture);
        using GeodatabaseBuilder file = GeodatabaseBuilder.Create(path, delta: true, string.Empty);
        file.Execute(
            "insert into GDB_DataChanges values (1, ' ', 0, $p0, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, NULL, $p1, NULL)",
            FileValues.Guid(delta.ReplicaId), generation);

        int datasets = 0, deltas = 0;
        foreach (OfflineLayerChanges changes in delta.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OfflineLayer layer = changes.Layer;
            string table = FileValues.TableName(service.Length == 0 ? layer.Name : $"{service}_{layer.Name}");
            TableGeometry? geometry = TableGeometry.Of(layer);

            if (changes.Inserts.Count + changes.Updates.Count + changes.Deletes.Count > 0)
            {
                int dataset = ++datasets;
                Dataset(file, dataset, table, geometry is null ? 1 : 2, layer.Id, generation);
                ChangeTable(file, dataset, 1, layer.Fields, layer.ObjectIdField, layer.GlobalIdField, geometry, changes.Inserts.Select(static r => (r.Values, r.Geometry)), ref deltas);
                ChangeTable(file, dataset, 2, layer.Fields, layer.ObjectIdField, layer.GlobalIdField, geometry, changes.Updates.Select(static r => (r.Values, r.Geometry)), ref deltas);
                ChangeTable(file, dataset, 3, SqliteMobileGeodatabaseWriter.DeleteFields, "OID", "globalid", null, changes.Deletes.Select(Key), ref deltas);
            }

            if (changes.AttachmentInserts.Count + changes.AttachmentUpdates.Count + changes.AttachmentDeletes.Count > 0)
            {
                int dataset = ++datasets;
                Dataset(file, dataset, table + "__ATTACH", 7, layer.Id, generation);
                OfflineField[] fields = SqliteMobileGeodatabaseWriter.AttachmentFields;
                ChangeTable(file, dataset, 1, fields, "attachmentid", "globalid", null, changes.AttachmentInserts.Select(Attachment), ref deltas);
                ChangeTable(file, dataset, 2, fields, "attachmentid", "globalid", null, changes.AttachmentUpdates.Select(Attachment), ref deltas);
                ChangeTable(file, dataset, 3, SqliteMobileGeodatabaseWriter.DeleteFields, "OID", "globalid", null, changes.AttachmentDeletes.Select(Key), ref deltas);
            }
        }

        file.Finish([(9, 2), (10, datasets + 1), (11, deltas + 1), (13, 1)]);
    }

    private static (IReadOnlyList<object?> Values, Geometry? Geometry) Key(OfflineKey key) => ([key.ObjectId, key.GlobalId], null);

    private static (IReadOnlyList<object?> Values, Geometry? Geometry) Attachment(OfflineAttachment a) =>
        (SqliteMobileGeodatabaseWriter.AttachmentRow(a), null);

    private static void Dataset(GeodatabaseBuilder file, int id, string name, int type, int layerId, string generation) =>
        file.Execute(
            "insert into GDB_DataChangesDatasets values ($p0, NULL, NULL, $p1, $p2, NULL, $p3, $p4, NULL)",
            id, name, type, layerId, generation);

    private static void ChangeTable(
        GeodatabaseBuilder file, int dataset, int changeType, IReadOnlyList<OfflineField> fields, string objectId, string? globalId,
        TableGeometry? geometry, IEnumerable<(IReadOnlyList<object?> Values, Geometry? Geometry)> rows, ref int deltas)
    {
        using IEnumerator<(IReadOnlyList<object?> Values, Geometry? Geometry)> e = rows.GetEnumerator();
        if (!e.MoveNext())
        {
            return;
        }

        string name = $"T_{dataset}_{Kinds[changeType - 1]}";
        using GeodatabaseTable table = GeodatabaseTable.Create(
            file, name, fields, objectId, globalId, geometry, archived: false, geometry is null ? 0x3 : 0x4007, bracketed: true);
        if (globalId is not null)
        {
            file.AfterRows($"CREATE UNIQUE INDEX [UUID_{table.Registration}] ON [{name}] ({globalId} ASC)");
        }

        do
        {
            table.Insert(e.Current.Values, e.Current.Geometry);
        }
        while (e.MoveNext());

        // The server's deltas start every change table's generator at 1, whatever rows it holds.
        file.RowidGenerator(table.Registration, 1, 1);

        int dsid = file.NextItemId;
        string itemName = $"main.{name}";
        bool attachments = ReferenceEquals(fields, SqliteMobileGeodatabaseWriter.AttachmentFields);
        string fieldsXml = attachments
            ? SqliteMobileGeodatabaseWriter.AttachmentItemFields()
            : string.Concat(fields.Select(static f => ItemXml.Field(f, null)));
        string uuid;
        if (geometry is not null)
        {
            fieldsXml += ItemXml.Field(new OfflineField(TableGeometry.Column, "esriFieldTypeGeometry", null, null, true, null), null);
            uuid = SqliteMobileGeodatabaseWriter.WriteFeatureClassItem(file, table, geometry, itemName, dsid, fieldsXml, null, false, "10.0").Uuid;
        }
        else
        {
            uuid = file.Item("Table", itemName, "\\" + itemName, ItemXml.Table(itemName, dsid, objectId, fieldsXml, globalId, false, "10.0")).Uuid;
        }

        file.Relate("DatasetInFolder", file.Root, uuid);
        file.Execute("insert into GDB_DataChangesDeltas values ($p0, $p1, $p2, $p3)", ++deltas, dsid, changeType, dataset);
    }

    /// <summary>Reads a delta.</summary>
    public static OfflineDelta Read(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The delta file does not exist.", path);
        }

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        GeodatabaseBuilder.RegisterCollation(db);

        var tables = new HashSet<string>(
            Query(db, "select name from sqlite_master where type = 'table'").Select(static r => (string)r[0]!),
            StringComparer.OrdinalIgnoreCase);

        Guid replica = Guid.Empty;
        long generation = 0;
        if (tables.Contains("GDB_DataChanges"))
        {
            foreach (object?[] r in Query(db, "select ReplicaGUID, ServerGen from GDB_DataChanges order by ID limit 1"))
            {
                _ = Guid.TryParse(r[0] as string, out replica);
                _ = long.TryParse(r[1]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);
            }
        }

        Dictionary<int, CoordinateGrid> grids = Grids(db, tables);

        var datasets = new Dictionary<long, (string Name, long Type, long? LayerId)>();
        if (tables.Contains("GDB_DataChangesDatasets"))
        {
            foreach (object?[] r in Query(db, "select ID, Name, Type, LayerId from GDB_DataChangesDatasets"))
            {
                datasets[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = (
                    r[1] as string ?? string.Empty,
                    r[2] is null ? 0 : Convert.ToInt64(r[2], CultureInfo.InvariantCulture),
                    r[3] is null ? null : Convert.ToInt64(r[3], CultureInfo.InvariantCulture));
            }
        }

        var changeTables = new List<(string Table, long ChangeType, long Dataset)>();
        if (tables.Contains("GDB_DataChangesDeltas"))
        {
            var items = new Dictionary<long, string>();
            if (tables.Contains("GDB_Items"))
            {
                foreach (object?[] r in Query(db, "select ObjectID, Name from GDB_Items"))
                {
                    items[Convert.ToInt64(r[0], CultureInfo.InvariantCulture)] = r[1] as string ?? string.Empty;
                }
            }

            foreach (object?[] r in Query(db, "select RecordsetClassID, ChangeType, ChangedDatasetID from GDB_DataChangesDeltas order by ID"))
            {
                long recordset = Convert.ToInt64(r[0], CultureInfo.InvariantCulture);
                long changeType = Convert.ToInt64(r[1], CultureInfo.InvariantCulture);
                long dataset = Convert.ToInt64(r[2], CultureInfo.InvariantCulture);
                string? table = items.TryGetValue(recordset, out string? itemName) ? Unqualified(itemName) : null;
                if (table is null || !tables.Contains(table))
                {
                    table = changeType is >= 1 and <= 3 ? $"T_{dataset}_{Kinds[changeType - 1]}" : null;
                }

                if (table is not null)
                {
                    changeTables.Add((table, changeType, dataset));
                }
            }
        }
        else
        {
            foreach (string table in tables)
            {
                Match m = ChangeTableName().Match(table);
                if (m.Success)
                {
                    long changeType = Array.FindIndex(Kinds, k => k.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) + 1;
                    changeTables.Add((table, changeType, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
                }
            }
        }

        var builders = layers.ToDictionary(static l => l, static l => new LayerChanges(l));
        foreach ((string table, long changeType, long datasetId) in changeTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tables.Contains(table) || !datasets.TryGetValue(datasetId, out var dataset))
            {
                continue;
            }

            bool attachment = dataset.Type == 7 || dataset.Name.EndsWith("__ATTACH", StringComparison.OrdinalIgnoreCase);
            OfflineLayer? layer = Match(layers, dataset.Name, dataset.LayerId, attachment);
            if (layer is null)
            {
                continue;
            }

            LayerChanges into = builders[layer];
            List<Dictionary<string, object?>> rows = Rows(db, table);
            switch (changeType, attachment)
            {
                case (1, false):
                    into.Inserts.AddRange(rows.Select(r => LayerRow(layer, r, grids)));
                    break;
                case (2, false):
                    into.Updates.AddRange(rows.Select(r => LayerRow(layer, r, grids)));
                    break;
                case (3, false):
                    into.Deletes.AddRange(rows.Select(r => DeleteKey(r, layer.ObjectIdField)));
                    break;
                case (1, true):
                    into.AttachmentInserts.AddRange(rows.Select(AttachmentRow));
                    break;
                case (2, true):
                    into.AttachmentUpdates.AddRange(rows.Select(AttachmentRow));
                    break;
                case (3, true):
                    into.AttachmentDeletes.AddRange(rows.Select(r => DeleteKey(r, "attachmentid")));
                    break;
            }
        }

        db.Close();
        return new OfflineDelta(
            replica,
            generation,
            [.. layers.Select(l => builders[l]).Where(static b => b.Any).Select(static b => b.Build())],
            string.Empty);
    }

    private static string Unqualified(string itemName) =>
        itemName.StartsWith("main.", StringComparison.OrdinalIgnoreCase) ? itemName[5..] : itemName;

    private static OfflineLayer? Match(IReadOnlyList<OfflineLayer> layers, string name, long? layerId, bool attachment)
    {
        if (layerId is long id && layers.FirstOrDefault(l => l.Id == id) is { } byId)
        {
            return byId;
        }

        string table = attachment && name.EndsWith("__ATTACH", StringComparison.OrdinalIgnoreCase) ? name[..^"__ATTACH".Length] : name;
        return layers.FirstOrDefault(l =>
            table.Equals(FileValues.TableName(l.Name), StringComparison.OrdinalIgnoreCase)
            || table.EndsWith("_" + FileValues.TableName(l.Name), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The grid of every reference the file defines, by SRID — research note §5b. Field Maps writes an upload's geometry
    /// in a client-local SRID (300001) that <c>st_aux_spatial_reference_systems</c> defines with its own false origins and
    /// units; a row whose units are not a positive finite number is left out, and its geometry falls back to the layer's.
    /// </summary>
    private static Dictionary<int, CoordinateGrid> Grids(SqliteConnection db, HashSet<string> tables)
    {
        var grids = new Dictionary<int, CoordinateGrid>();
        if (!tables.Contains("st_aux_spatial_reference_systems"))
        {
            return grids;
        }

        foreach (object?[] r in Query(db, "select srid, falsex, falsey, xyunits, falsez, zunits, falsem, munits from st_aux_spatial_reference_systems"))
        {
            if (r[0] is null || Number(r[1]) is not double x || Number(r[2]) is not double y || Number(r[3]) is not double xy || !(xy > 0))
            {
                continue;
            }

            double z = Number(r[4]) ?? 0, zUnits = Number(r[5]) ?? 1, m = Number(r[6]) ?? 0, mUnits = Number(r[7]) ?? 1;
            grids[Convert.ToInt32(r[0], CultureInfo.InvariantCulture)] = new CoordinateGrid(
                x, y, xy, z, zUnits > 0 ? zUnits : 1, m, mUnits > 0 ? mUnits : 1);
        }

        return grids;
    }

    private static double? Number(object? value) =>
        value is null ? null
        : Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d
        : null;

    private static OfflineRow LayerRow(OfflineLayer layer, Dictionary<string, object?> row, Dictionary<int, CoordinateGrid> grids)
    {
        var values = new object?[layer.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            OfflineField field = layer.Fields[i];
            values[i] = row.TryGetValue(field.Name, out object? v) ? GeodatabaseTable.FromFile(field, v) : null;
        }

        Geometry? geometry = null;
        if (TableGeometry.Of(layer) is { } g && row.TryGetValue(TableGeometry.Column, out object? blob) && blob is byte[] bytes)
        {
            // The grid of the SRID the blob names when the file defines it, the layer's otherwise (research note §5b).
            CoordinateGrid grid = MobileGeodatabaseGeometry.SridOf(bytes) is int srid && grids.TryGetValue(srid, out CoordinateGrid own)
                ? own
                : g.Grid;
            geometry = MobileGeodatabaseGeometry.Decode(bytes, grid, out _);
        }

        return new OfflineRow(values, geometry);
    }

    private static OfflineKey DeleteKey(Dictionary<string, object?> row, string objectIdField)
    {
        object? oid = row.TryGetValue("OID", out object? o) ? o : row.TryGetValue(objectIdField, out object? f) ? f : null;
        object? gid = row.TryGetValue("globalid", out object? g) ? g : null;
        return new OfflineKey(
            oid is null ? 0 : Convert.ToInt64(oid, CultureInfo.InvariantCulture),
            gid is string text && Guid.TryParse(text, out Guid parsed) ? parsed : Guid.Empty);
    }

    private static OfflineAttachment AttachmentRow(Dictionary<string, object?> row)
    {
        Guid Id(string column) => row.TryGetValue(column, out object? v) && v is string s && Guid.TryParse(s, out Guid g) ? g : Guid.Empty;
        string Text(string column) => row.TryGetValue(column, out object? v) ? v?.ToString() ?? string.Empty : string.Empty;
        long id = row.TryGetValue("attachmentid", out object? a) && a is not null ? Convert.ToInt64(a, CultureInfo.InvariantCulture) : 0;
        byte[] data = row.TryGetValue("data", out object? d) && d is byte[] bytes ? bytes : [];
        return new OfflineAttachment(id, Id("rel_globalid"), Id("globalid"), Text("att_name"), Text("content_type"), data);
    }

    private static List<Dictionary<string, object?>> Rows(SqliteConnection db, string table)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"select * from [{table.Replace("]", "]]", StringComparison.Ordinal)}]";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<object?[]> Query(SqliteConnection db, string sql)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    [GeneratedRegex("^T_([0-9]+)_(Inserts|Updates|Deletes)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ChangeTableName();

    private sealed class LayerChanges(OfflineLayer layer)
    {
        public List<OfflineRow> Inserts { get; } = [];

        public List<OfflineRow> Updates { get; } = [];

        public List<OfflineKey> Deletes { get; } = [];

        public List<OfflineAttachment> AttachmentInserts { get; } = [];

        public List<OfflineAttachment> AttachmentUpdates { get; } = [];

        public List<OfflineKey> AttachmentDeletes { get; } = [];

        public bool Any =>
            Inserts.Count + Updates.Count + Deletes.Count + AttachmentInserts.Count + AttachmentUpdates.Count + AttachmentDeletes.Count > 0;

        public OfflineLayerChanges Build() =>
            new(layer, Inserts, Updates, Deletes, AttachmentInserts, AttachmentUpdates, AttachmentDeletes);
    }
}
