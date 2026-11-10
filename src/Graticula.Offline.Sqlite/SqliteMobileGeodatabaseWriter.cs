using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// Writes the mobile geodatabase ArcGIS Field Maps and Pro take a feature service offline in, and the deltas that move
/// edits between it and the server, with SQLite — ADR-288 §6.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format is observed, not published</b>; <c>docs/research/mobile-geodatabase-format.md</c> describes it and §0
/// there says how it was learned. <c>experiments/mobile-geodatabase/writer_proto.py</c> is the specification this was
/// written fresh from: ArcGIS Pro read both kinds of file the prototype wrote and edited the registered one.
/// </para>
/// <para>
/// The fixed part of the format — system tables, item types, the system column registry — is
/// <see cref="MobileGeodatabaseSchema"/>; everything per layer is generated here.
/// </para>
/// </remarks>
public sealed class SqliteMobileGeodatabaseWriter : IMobileGeodatabaseWriter
{
    private const string Shape = TableGeometry.Column;

    /// <summary>The fields of an attachment table, as the server's files declare them.</summary>
    internal static readonly OfflineField[] AttachmentFields =
    [
        new("attachmentid", "esriFieldTypeOID", null, null, false, null),
        new("rel_globalid", "esriFieldTypeGUID", null, null, false, null),
        new("content_type", "esriFieldTypeString", null, 150, false, null),
        new("att_name", "esriFieldTypeString", null, 250, false, null),
        new("data_size", "esriFieldTypeInteger", null, null, false, null),
        new("data", "esriFieldTypeBlob", null, null, true, null),
        new("globalid", "esriFieldTypeGlobalID", null, null, false, null),
    ];

    /// <summary>The fields of a delta's <c>T_n_Deletes</c> table.</summary>
    internal static readonly OfflineField[] DeleteFields =
    [
        new("OID", "esriFieldTypeOID", null, null, false, null),
        new("globalid", "esriFieldTypeGlobalID", null, null, false, null),
    ];

    /// <inheritdoc />
    public async Task WriteAsync(OfflineCopy copy, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copy);
        OfflineReplica? replica = copy.Replica;
        bool registered = replica is not null;
        string service = FileValues.TableName(copy.ServiceName);

        using GeodatabaseBuilder file = GeodatabaseBuilder.Create(path, delta: false, replica?.Owner ?? string.Empty);
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var sync = new List<ItemXml.SyncDataset>();

        foreach (OfflineLayer layer in copy.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string table = FileValues.TableName($"{service}_{layer.Name}");
            string? globalId = layer.GlobalIdField;

            // Domains first: a dataset's DSID is the object id of its own item, which follows its domains'.
            var fieldDomains = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (OfflineField field in layer.Fields)
            {
                if (field.Domain is not { } domain)
                {
                    continue;
                }

                fieldDomains[field.Name] = domain.Name;
                if (!domains.ContainsKey(domain.Name))
                {
                    domains[domain.Name] = domain switch
                    {
                        OfflineCodedDomain coded => file.Item("Coded Value Domain", coded.Name, string.Empty, ItemXml.CodedDomain(coded, field.EsriType)).Uuid,
                        OfflineRangeDomain range => file.Item("Range Domain", range.Name, string.Empty, ItemXml.RangeDomain(range, field.EsriType)).Uuid,
                        _ => throw new NotSupportedException($"The domain {domain.Name} is of a kind an offline copy does not carry."),
                    };
                }
            }

            TableGeometry? geometry = TableGeometry.Of(layer);
            using GeodatabaseTable rows = GeodatabaseTable.Create(
                file, table, layer.Fields, layer.ObjectIdField, globalId, geometry, registered,
                geometry is null ? 0x3 : 0x4007, bracketed: false);
            QueueIndexes(file, rows, registered);

            await foreach (OfflineRow row in layer.Rows(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows.Insert(row.Values, row.Geometry);
            }

            file.RowidGenerator(rows.Registration, rows.Count > 0 ? rows.MaxObjectId + 1 : 1, rows.Count > 0 ? rows.MaxObjectId : 1);

            string fields = string.Concat(layer.Fields.Select(f => ItemXml.Field(f, fieldDomains.GetValueOrDefault(f.Name))));
            int dsid = file.NextItemId;
            string itemName = $"main.{table}";
            string itemUuid;
            int? layerId = null;
            if (geometry is not null)
            {
                fields += ItemXml.Field(new OfflineField(Shape, "esriFieldTypeGeometry", null, null, true, null), null);
                (string uuid, int id) = WriteFeatureClassItem(file, rows, geometry, itemName, dsid, fields, layer.Extent, registered, "10.2");
                itemUuid = uuid;
                layerId = id;
            }
            else
            {
                string definition = ItemXml.Table(itemName, dsid, layer.ObjectIdField, fields, globalId, registered, "10.2");
                itemUuid = file.Item("Table", itemName, "\\" + itemName, definition).Uuid;
            }

            file.Relate("DatasetInFolder", file.Root, itemUuid);
            foreach (string domain in fieldDomains.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                file.Relate("DomainInDataset", itemUuid, domains[domain]);
            }

            sync.Add(new ItemXml.SyncDataset(dsid, itemName, geometry is not null, layer.Id, layer.Name, layer.HasAttachments, false, table));
            RegisterColumns(file, table, layer.Fields, geometry is null ? null : layerId, registered);

            if (layer.HasAttachments)
            {
                if (globalId is null)
                {
                    throw new NotSupportedException($"The layer {layer.Name} has attachments but no global id, which an offline copy keys them by.");
                }

                await WriteAttachmentsAsync(file, layer, table, itemName, itemUuid, globalId, registered, sync, cancellationToken).ConfigureAwait(false);
            }
        }

        // The service's REST document and each layer's, as the client reads the service's shape from them (research note §3).
        file.Execute(
            "insert into GDB_ServiceItems (ObjectID, ItemType, ItemInfo, ItemId, DatasetName, AdvancedDrawingInfo) values (1, 0, $p0, -1, $p1, NULL)",
            copy.ServiceDocument, service);
        for (int i = 0; i < copy.Layers.Count; i++)
        {
            OfflineLayer layer = copy.Layers[i];
            file.Execute(
                "insert into GDB_ServiceItems (ObjectID, ItemType, ItemInfo, ItemId, DatasetName, AdvancedDrawingInfo) values ($p0, 1, $p1, $p2, $p3, NULL)",
                i + 2, layer.LayerDocument, layer.Id, FileValues.TableName($"{service}_{layer.Name}"));
        }

        int serviceItems = file.Register("GDB_ServiceItems", "ObjectID", 3);
        file.RowidGenerator(serviceItems, copy.Layers.Count + 2, 1);

        if (replica is not null)
        {
            WriteReplicaItems(file, replica, copy.Layers, sync);
        }

        file.Finish([]);
    }

    private static void QueueIndexes(GeodatabaseBuilder file, GeodatabaseTable table, bool archived)
    {
        int r = table.Registration;
        string archive = archived ? ",gdb_to_date ASC" : string.Empty;
        if (table.GlobalId is { } globalId)
        {
            file.AfterRows($"CREATE UNIQUE INDEX [UUID_{r}] ON {table.Name} ({globalId} ASC{archive})");
        }

        if (archived)
        {
            file.AfterRows($"CREATE UNIQUE INDEX [r{r}_gdb_xpk] ON {table.Name} ({table.ObjectId} ASC,gdb_to_date ASC)");
            file.AfterRows($"CREATE INDEX [gdb_ct1_{r}] ON {table.Name} (gdb_from_date ASC,gdb_to_date ASC)");
            file.AfterRows($"CREATE UNIQUE INDEX [gdb_ct4_{r}] ON {table.Name} ({table.ObjectId} ASC,gdb_from_date ASC)");
            foreach (string sql in TableStatements.Archive(table.Name, table.ObjectId, table.Columns))
            {
                file.AfterRows(sql);
            }
        }
    }

    /// <summary>
    /// Writes a feature class's geometry registration and its item: the extent from the rows written (or the layer's
    /// when none were), and the item's shape, which is that extent as a WGS 84 polygon.
    /// </summary>
    internal static (string Uuid, int LayerId) WriteFeatureClassItem(
        GeodatabaseBuilder file, GeodatabaseTable rows, TableGeometry geometry, string itemName, int dsid, string fields,
        Envelope? layerExtent, bool changeTracked, string clientVersion)
    {
        foreach (string sql in TableStatements.SpatialIndexTriggers(rows.Name, Shape))
        {
            file.AfterRows(sql);
        }

        var extent = rows.Extent
            ?? (layerExtent is { IsEmpty: false } e ? (e.MinX, e.MinY, e.MaxX, e.MaxY) : (0d, 0d, 0d, 0d));
        (double Min, double Max)? z = rows.ZRange;
        int layerId = file.GeometryColumn(
            rows.Name, Shape, geometry.GeometryType, geometry.HasZ, geometry.HasM, geometry.Srid, extent, z?.Min, z?.Max);

        string reference = ItemXml.SpatialReference(geometry.Wkid, geometry.Geographic, geometry.Wkt, geometry.Grid);
        string extentXml = rows.Extent is null
            ? string.Empty
            : ItemXml.Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, z?.Min, z?.Max, reference);
        string definition = ItemXml.FeatureClass(
            itemName, dsid, rows.ObjectId, fields, rows.GlobalId, changeTracked, clientVersion,
            geometry.GeometryType, Shape, geometry.HasZ, geometry.HasM, extentXml, reference);

        (byte[]? shape, Envelope bounds) = rows.Extent is null ? (null, Envelope.Empty) : ItemShape(extent, geometry);
        string uuid = file.Item(
            "Feature Class", itemName, "\\" + itemName, definition,
            subtype1: 1, subtype2: TableStatements.ItemShapeType(geometry.GeometryType), info1: Shape,
            shape: shape, shapeBounds: bounds).Uuid;
        return (uuid, layerId);
    }

    /// <summary>
    /// A feature class item's shape: its extent as a polygon in WGS 84, which is what <c>GDB_Items.Shape</c> declares.
    /// Written for geographic references and Web Mercator, whose inverse is closed-form; left null for any other
    /// reference, as no projector is reachable from here.
    /// </summary>
    internal static (byte[]? Shape, Envelope Bounds) ItemShape((double MinX, double MinY, double MaxX, double MaxY) extent, TableGeometry geometry)
    {
        (double X, double Y) lo, hi;
        if (geometry.Geographic)
        {
            (lo, hi) = ((extent.MinX, extent.MinY), (extent.MaxX, extent.MaxY));
        }
        else if (geometry.Wkid is 3857 or 102100 or 102113 or 900913)
        {
            (lo, hi) = (FromWebMercator(extent.MinX, extent.MinY), FromWebMercator(extent.MaxX, extent.MaxY));
        }
        else
        {
            return (null, Envelope.Empty);
        }

        var ring = new LinearRing(XySequence.Wrap([lo.X, lo.Y, hi.X, lo.Y, hi.X, hi.Y, lo.X, hi.Y, lo.X, lo.Y]));
        byte[]? blob = MobileGeodatabaseGeometry.Encode(new Polygon(ring), 4326, CoordinateGrid.Geographic, GeometryOrdinates.None);
        return (blob, new Envelope(lo.X, lo.Y, hi.X, hi.Y));
    }

    private static (double X, double Y) FromWebMercator(double x, double y)
    {
        const double Radius = 6378137.0;
        double lon = x / Radius * 180 / Math.PI;
        double lat = ((2 * Math.Atan(Math.Exp(y / Radius))) - (Math.PI / 2)) * 180 / Math.PI;
        return (lon, lat);
    }

    /// <summary>Queues a table's column registry rows: its fields, its geometry column, its archive columns.</summary>
    internal static void RegisterColumns(GeodatabaseBuilder file, string table, IReadOnlyList<OfflineField> fields, int? layerId, bool archived)
    {
        foreach (OfflineField f in fields)
        {
            int? size = f.EsriType switch
            {
                "esriFieldTypeOID" => 10,
                "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => 38,
                "esriFieldTypeString" => TableStatements.StringLength(f),
                "esriFieldTypeBlob" => null,
                _ => 0,
            };
            int flags = f.EsriType switch
            {
                "esriFieldTypeOID" => 3,
                "esriFieldTypeGlobalID" => 256,
                _ => f.Nullable ? 4 : 0,
            };
            int? decimals = f.EsriType is "esriFieldTypeDouble" or "esriFieldTypeSingle" ? 0 : null;
            file.ColumnRegistry(table, f.Name, TableStatements.SdeType(f.EsriType), size, decimals, null, flags, null);
        }

        if (layerId is int id)
        {
            file.ColumnRegistry(table, Shape, 8, null, null, null, 32772, id);
        }

        if (archived)
        {
            file.ColumnRegistry(table, "gdb_from_date", 7, 0, null, "Archiving from date.", 536870912, null);
            file.ColumnRegistry(table, "gdb_to_date", 7, 0, null, "Archiving to date.", 536870912, null);
            file.ColumnRegistry(table, "gdb_archive_oid", 2, 0, null, "Archiving record unique id.", 536870912, null);
        }
    }

    private static async Task WriteAttachmentsAsync(
        GeodatabaseBuilder file, OfflineLayer layer, string table, string itemName, string itemUuid, string globalId,
        bool registered, List<ItemXml.SyncDataset> sync, CancellationToken cancellationToken)
    {
        string attachTable = table + "__ATTACH";
        using GeodatabaseTable attachments = GeodatabaseTable.Create(
            file, attachTable, AttachmentFields, "attachmentid", "globalid", null, registered, 0x3, bracketed: true);

        await foreach (OfflineAttachment a in layer.Attachments(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            attachments.Insert(AttachmentRow(a), null);
        }

        long max = attachments.MaxObjectId;
        file.RowidGenerator(attachments.Registration, max + 1, Math.Max(max, 1));
        int r = attachments.Registration;
        string archive = registered ? ",gdb_to_date ASC" : string.Empty;
        file.AfterRows($"CREATE UNIQUE INDEX [UUID_{r}] ON {attachTable} (globalid ASC{archive})");
        file.AfterRows($"CREATE INDEX [I{r}rel_globalid] ON {attachTable} (rel_globalid ASC)");
        if (registered)
        {
            file.AfterRows($"CREATE UNIQUE INDEX [r{r}_gdb_xpk] ON {attachTable} (attachmentid ASC,gdb_to_date ASC)");
            file.AfterRows($"CREATE INDEX [gdb_ct1_{r}] ON {attachTable} (gdb_from_date ASC,gdb_to_date ASC)");
            file.AfterRows($"CREATE UNIQUE INDEX [gdb_ct4_{r}] ON {attachTable} (attachmentid ASC,gdb_from_date ASC)");
            foreach (string sql in TableStatements.Archive(attachTable, "attachmentid", attachments.Columns))
            {
                file.AfterRows(sql);
            }
        }

        int dsid = file.NextItemId;
        string attachItemName = $"main.{attachTable}";
        string definition = ItemXml.Table(attachItemName, dsid, "attachmentid", AttachmentItemFields(), "globalid", registered, "10.2");
        string attachUuid = file.Item("Table", attachItemName, "\\" + attachItemName, definition).Uuid;
        file.Relate("DatasetInFolder", file.Root, attachUuid);
        sync.Add(new ItemXml.SyncDataset(dsid, attachItemName, false, layer.Id, null, false, true, attachTable));

        string relationship = $"main.{table}__ATTACHREL";
        string relationshipDefinition = ItemXml.AttachmentRelationship(relationship, file.NextItemId, itemName, attachItemName, globalId);
        string relationshipUuid = file.Item("Relationship Class", relationship, "\\" + relationship, relationshipDefinition, subtype1: 2, subtype2: 0).Uuid;
        file.Relate("DatasetInFolder", file.Root, relationshipUuid);
        file.Relate("DatasetsRelatedThrough", itemUuid, relationshipUuid);
        file.Relate("DatasetsRelatedThrough", attachUuid, relationshipUuid);
        RegisterColumns(file, attachTable, AttachmentFields, null, registered);
    }

    /// <summary>The attachment table's fields as its item lists them: every one but the data, without aliases.</summary>
    internal static string AttachmentItemFields() =>
        string.Concat(AttachmentFields.Where(static f => f.Name != "data").Select(static f => ItemXml.Field(f, null, alias: false)));

    /// <summary>An attachment as a row of the attachment table.</summary>
    internal static object?[] AttachmentRow(OfflineAttachment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return [a.AttachmentId, a.ParentGlobalId, a.ContentType, a.Name, a.Data.Length, a.Data.ToArray(), a.GlobalId];
    }

    private static void WriteReplicaItems(GeodatabaseBuilder file, OfflineReplica replica, IReadOnlyList<OfflineLayer> layers, List<ItemXml.SyncDataset> sync)
    {
        string queryReference = QueryReference(replica.QueryWkid, layers);
        var bodies = sync.Select(d => (Dataset: d, Body: ItemXml.SyncDatasetBody(d, replica.ServerGeneration))).ToList();
        string replicaUuid = FileValues.Guid(replica.Id);
        file.Item(
            "Sync Replica", replica.Name, replica.Name, ItemXml.SyncReplica(replica, bodies.Select(static b => b.Body), queryReference),
            info1: replica.ServiceUrl, info2: replica.Owner, uuid: replicaUuid);

        foreach ((ItemXml.SyncDataset dataset, string body) in bodies)
        {
            string datasetUuid = file.Item(
                "Sync Dataset", dataset.Table, $"{replica.Name}\\{dataset.Table}", ItemXml.SyncDatasetItem(body),
                physicalName: dataset.Name.ToUpperInvariant()).Uuid;
            string target = file.Items.First(i => i.Name == dataset.Name).Uuid;
            file.Relate("SyncDatasetInSyncReplica", replicaUuid, datasetUuid);
            file.Relate("DatasetOfSyncDataset", datasetUuid, target);
        }
    }

    /// <summary>
    /// The spatial reference of the replica's query extent: a layer's own when one shares its id, else Web Mercator or
    /// WGS 84 by id. Any other id is written with an empty WKT, since nothing here can name it.
    /// </summary>
    private static string QueryReference(int wkid, IReadOnlyList<OfflineLayer> layers)
    {
        OfflineLayer? match = layers.FirstOrDefault(l => l.Wkid == wkid || (wkid is 3857 or 102100 && l.Wkid is 3857 or 102100));
        if (match is not null)
        {
            return ItemXml.SpatialReference(match.Wkid, match.Geographic, match.ReferenceWkt, CoordinateGrid.For(match.Wkid, match.Geographic));
        }

        return wkid switch
        {
            3857 or 102100 => ItemXml.SpatialReference(wkid, false, ItemXml.WebMercatorWkt, CoordinateGrid.WebMercator),
            4326 => ItemXml.SpatialReference(wkid, true, ItemXml.Wgs84Wkt, CoordinateGrid.Geographic),
            _ => ItemXml.SpatialReference(wkid, false, string.Empty, CoordinateGrid.Projected),
        };
    }

    /// <inheritdoc />
    public Task WriteDeltaAsync(OfflineDelta delta, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        cancellationToken.ThrowIfCancellationRequested();
        DeltaFile.Write(delta, path, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layers);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeltaFile.Read(path, layers, cancellationToken));
    }
}
