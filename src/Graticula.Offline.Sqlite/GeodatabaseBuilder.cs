using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Microsoft.Data.Sqlite;

namespace Graticula.Offline.Sqlite;

/// <summary>An item of <c>GDB_Items</c>, held until the file is finished.</summary>
internal sealed record GeodatabaseItem(
    string Uuid,
    string Type,
    string Name,
    string PhysicalName,
    string Path,
    string? Url,
    int Properties,
    int? Subtype1,
    int? Subtype2,
    string? Info1,
    string? Info2,
    string? Definition,
    byte[]? Shape,
    Envelope ShapeBounds);

/// <summary>
/// One mobile geodatabase being written: the system tables, the catalogue, the registries, and the statements that
/// have to wait until the rows are in — ADR-288, research note §1 and §3.
/// </summary>
/// <remarks>
/// <para>
/// Both a whole copy and a delta are one of these: a delta is a small geodatabase of its own with the change tables
/// registered in it (research note §5). The writer of each adds its tables and items, then calls <see cref="Finish"/>.
/// </para>
/// <para>
/// <b>Order is part of the format.</b> Rows go in before the spatial-index and archive triggers exist, because those
/// call functions only Esri's library defines; the R*Tree is filled directly instead. The two virtual tables whose
/// modules only Esri has are written last, as rows of <c>sqlite_master</c>, and the connection is closed straight after,
/// because this SQLite cannot open them.
/// </para>
/// </remarks>
internal sealed class GeodatabaseBuilder : IDisposable
{
    private static readonly HashSet<string> NotInDelta = new(StringComparer.Ordinal) { "GDB_TriggerWork", "GDB_ServiceItems" };

    private readonly SqliteConnection _db;
    private readonly bool _delta;
    private readonly Dictionary<string, string> _itemTypes;
    private readonly Dictionary<string, string> _relationshipTypes;
    private readonly List<(string Type, string Origin, string Destination)> _relationships = [];
    private readonly List<object?[]> _columnRegistry = [];
    private readonly List<string> _afterRows = [];
    private SqliteTransaction? _transaction;
    private int _registration;
    private int _geometryColumn = 1;
    private int _layer = 1;
    private bool _finished;

    private GeodatabaseBuilder(SqliteConnection db, bool delta, DateTimeOffset now)
    {
        _db = db;
        _delta = delta;
        Now = now;
        _itemTypes = MobileGeodatabaseSchema.ItemTypes.ToDictionary(static t => t.Name, static t => t.Uuid, StringComparer.Ordinal);
        _relationshipTypes = MobileGeodatabaseSchema.ItemRelationshipTypes.ToDictionary(static t => t.Name, static t => t.Uuid, StringComparer.Ordinal);
    }

    /// <summary>When the file is being written.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>The same moment in epoch seconds, as the registries date things.</summary>
    public long NowSeconds => Now.ToUnixTimeSeconds();

    /// <summary>The items, in object id order.</summary>
    public List<GeodatabaseItem> Items { get; } = [];

    /// <summary>The root folder's UUID.</summary>
    public string Root { get; private set; } = string.Empty;

    /// <summary>The connection, for the writer's own statements.</summary>
    public SqliteConnection Connection => _db;

    /// <summary>The open transaction.</summary>
    public SqliteTransaction Transaction => _transaction ?? throw new InvalidOperationException("The file is finished.");

    /// <summary>Creates the file and writes everything a geodatabase holds before its first table.</summary>
    /// <param name="path">The file; it must not exist.</param>
    /// <param name="delta">Whether this is a delta, which has the change tables and neither service items nor archive work table.</param>
    /// <param name="workspaceUser">The user the workspace names.</param>
    /// <exception cref="IOException">The file exists.</exception>
    public static GeodatabaseBuilder Create(string path, bool delta, string workspaceUser)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (File.Exists(path))
        {
            throw new IOException($"The offline copy '{path}' exists already; a writer creates a new file.");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        GeodatabaseBuilder? builder = null;
        try
        {
            connection.Open();
            RegisterCollation(connection);
            builder = new GeodatabaseBuilder(connection, delta, DateTimeOffset.UtcNow);
            builder.Begin(workspaceUser);
            return builder;
        }
        catch
        {
            if (builder is not null)
            {
                builder.Dispose();
            }
            else
            {
                connection.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Registers <c>ICUFOLDCASE</c>, the collation an index on <c>GDB_Items.Name</c> is declared with. Case folding by
    /// ordinal comparison is what the index needs to be consistent; ICU's exact order is the device's business.
    /// </summary>
    public static void RegisterCollation(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateCollation("ICUFOLDCASE", static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
    }

    private void Begin(string workspaceUser)
    {
        Execute("PRAGMA page_size=4096");
        Execute("PRAGMA application_id=1165193833");
        _transaction = _db.BeginTransaction();

        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string sql) in MobileGeodatabaseSchema.SystemTables)
        {
            if (_delta && NotInDelta.Contains(name))
            {
                continue;
            }

            Execute(sql);
            created.Add(name);
        }

        foreach ((string table, string sql) in MobileGeodatabaseSchema.SystemIndexes)
        {
            if (created.Contains(table))
            {
                Execute(sql);
            }
        }

        Execute(MobileGeodatabaseSchema.ItemsSpatialIndex);

        foreach ((int objectId, string uuid, string name, string parent) in MobileGeodatabaseSchema.ItemTypes)
        {
            Execute("insert into GDB_ItemTypes values ($p0,$p1,$p2,$p3)", objectId, uuid, name, parent);
        }

        foreach (var t in MobileGeodatabaseSchema.ItemRelationshipTypes)
        {
            Execute(
                "insert into GDB_ItemRelationshipTypes values ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
                t.ObjectId, t.Uuid, t.Name, t.ForwardLabel, t.BackwardLabel, t.OrigItemTypeId, t.DestItemTypeId, t.IsContainment);
        }

        Execute("insert into GDB_Version values (12, 1, 0, 'Graticula', 109504, 93001)");
        Execute("insert into st_aux_spatial_reference_systems values (300000, 'ESRI', 300000, 'UNKNOWN', -450359962737.05, -450359962737.05, 10000.0, -100000.0, 10000.0, -100000.0, 10000.0, 1e-06, 0.001, 0.001, 1)");

        foreach ((string table, string? rowid, int flags) in new (string, string?, int)[]
        {
            ("GDB_Tables_Last_Modified", null, 0), ("GDB_Items", "ObjectID", 4210695), ("GDB_ItemRelationships", "ObjectID", 4194307),
            ("GDB_ItemTypes", "ObjectID", 3), ("GDB_ItemRelationshipTypes", "ObjectID", 3), ("GDB_ReplicaLog", "ID", 3),
            ("GDB_EditingTemplates", "ObjectID", 3), ("GDB_EditingTemplateRelationships", "ObjectID", 3),
        })
        {
            Register(table, rowid, flags);
        }

        foreach (string table in new[] { "GDB_ITEMS", "GDB_ITEMRELATIONSHIPS", "GDB_ITEMTYPES", "GDB_ITEMRELATIONSHIPTYPES", "GDB_REPLICALOG" })
        {
            Execute("insert into GDB_Tables_Last_Modified values ($p0, 0)", table);
        }

        if (_delta)
        {
            foreach ((string name, string sql) in MobileGeodatabaseSchema.DeltaTables)
            {
                Execute(sql);
            }

            // Registration ids 9–13, as the server's deltas have them; GDB_DataChangesTopologies has no row id.
            Register("GDB_DataChanges", "ID", 3);
            Register("GDB_DataChangesDatasets", "ID", 3);
            Register("GDB_DataChangesDeltas", "ID", 3);
            Register("GDB_DataChangesTopologies", null, 0);
            Register("GDB_RelatedDatasets", "ID", 3);
        }

        Root = Item("Folder", string.Empty, "\\", definition: null, url: null, physicalName: string.Empty).Uuid;
        Item("Workspace", "Workspace", string.Empty, ItemXml.Workspace(workspaceUser), properties: 0, subtype1: 1, physicalName: "WORKSPACE");
        Item("Historical Marker", "DEFAULT", string.Empty, ItemXml.HistoricalMarker(Now));

        Execute("insert into st_geometry_columns (gcid, f_table_name, f_geometry_column, geometry_type, coord_dimension, srid, spatial_index_type) values (1, 'GDB_Items', 'Shape', 6, 2, 4326, 'RTREEXY')");
        Execute("insert into GDB_Layers values (1, NULL, 'GDB_Items', 'Shape', 71565457, 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL, $p0, 1, 4326, 0)", NowSeconds);
    }

    /// <summary>Registers a table in <c>GDB_TableRegistry</c> and returns its registration id.</summary>
    public int Register(string table, string? rowidColumn, int flags)
    {
        _registration++;
        Execute(
            "insert into GDB_TableRegistry values ($p0,$p1,$p2,NULL,$p3,0,$p4,1,NULL)",
            _registration, table, rowidColumn, flags, NowSeconds);
        return _registration;
    }

    /// <summary>Adds an item and returns it with its object id.</summary>
    public (string Uuid, int ObjectId) Item(
        string type, string name, string path, string? definition, string? url = "", int properties = 1,
        int? subtype1 = null, int? subtype2 = null, string? info1 = null, string? info2 = null,
        byte[]? shape = null, Envelope shapeBounds = default, string? physicalName = null, string? uuid = null)
    {
        if (!_itemTypes.TryGetValue(type, out string? typeUuid))
        {
            throw new InvalidOperationException($"'{type}' is not an item type of the format.");
        }

        uuid ??= FileValues.NewGuid();
        Items.Add(new GeodatabaseItem(
            uuid, typeUuid, name, physicalName ?? name.ToUpperInvariant(), path, url, properties, subtype1, subtype2,
            info1, info2, definition, shape, shapeBounds));
        return (uuid, Items.Count);
    }

    /// <summary>The object id the next item will have, which is a dataset's <c>DSID</c>.</summary>
    public int NextItemId => Items.Count + 1;

    /// <summary>Relates two items.</summary>
    public void Relate(string type, string origin, string destination) => _relationships.Add((type, origin, destination));

    /// <summary>Queues a statement that must run after every row is in.</summary>
    public void AfterRows(string sql) => _afterRows.Add(sql);

    /// <summary>Queues a <c>GDB_ColumnRegistry</c> row.</summary>
    public void ColumnRegistry(string table, string column, int sdeType, int? size, int? decimals, string? description, int flags, int? objectId) =>
        _columnRegistry.Add([table, column, sdeType, size, decimals, description, flags, 0, objectId, null]);

    /// <summary>Records a geometry column: its <c>st_geometry_columns</c> row and its <c>GDB_Layers</c> row.</summary>
    /// <returns>The layer id, which the column registry names.</returns>
    public int GeometryColumn(
        string table, string column, string geometryType, bool hasZ, bool hasM, int srid,
        (double MinX, double MinY, double MaxX, double MaxY) extent, double? zmin, double? zmax)
    {
        _geometryColumn++;
        Execute(
            "insert into st_geometry_columns (gcid, f_table_name, f_geometry_column, geometry_type, coord_dimension, srid, spatial_index_type) values ($p0,$p1,$p2,$p3,$p4,$p5,'RTREEXY')",
            _geometryColumn, table, column,
            TableStatements.ColumnType(geometryType) + (hasZ ? 1000 : 0) + (hasM ? 2000 : 0),
            2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0),
            srid);
        _layer++;
        Execute(
            "insert into GDB_Layers values ($p0, NULL, $p1, $p2, $p3, 0, $p4, $p5, $p6, $p7, $p8, NULL, $p9, NULL, $p10, 1, $p11, 0)",
            _layer, table, column, TableStatements.LayerFlags(geometryType, hasZ, hasM),
            extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, zmin, zmax, NowSeconds, srid);
        return _layer;
    }

    /// <summary>
    /// Writes the catalogue and the registries, runs the statements that waited for the rows, and writes the two
    /// Esri-module tables into the schema. The connection is closed when this returns.
    /// </summary>
    /// <param name="rowidGenerators">Further <c>GDB_RowidGenerators</c> rows for system tables, by registration id.</param>
    public void Finish(IEnumerable<(int Registration, long Base)> rowidGenerators)
    {
        ArgumentNullException.ThrowIfNull(rowidGenerators);
        if (_finished)
        {
            throw new InvalidOperationException("The file is finished.");
        }

        var itemsIndex = TableStatements.SpatialIndexName("GDB_Items", "Shape");
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        for (int i = 0; i < Items.Count; i++)
        {
            GeodatabaseItem it = Items[i];
            Execute(
                "insert into GDB_Items (ObjectID, UUID, Type, Name, PhysicalName, Path, Url, Properties, Defaults, DatasetSubtype1, DatasetSubtype2, DatasetInfo1, DatasetInfo2, Definition, Documentation, ItemInfo, ContingentValues, Shape) values ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,NULL,$p8,$p9,$p10,$p11,$p12,NULL,NULL,NULL,$p13)",
                i + 1, it.Uuid, it.Type, it.Name, it.PhysicalName, it.Path, it.Url, it.Properties, it.Subtype1, it.Subtype2,
                it.Info1, it.Info2, it.Definition, it.Shape);
            if (it.Shape is not null && !it.ShapeBounds.IsEmpty)
            {
                Envelope b = it.ShapeBounds;
                Execute($"insert into {itemsIndex} values ($p0,$p1,$p2,$p3,$p4)", i + 1, b.MinX, b.MaxX, b.MinY, b.MaxY);
                minx = Math.Min(minx, b.MinX);
                miny = Math.Min(miny, b.MinY);
                maxx = Math.Max(maxx, b.MaxX);
                maxy = Math.Max(maxy, b.MaxY);
            }
        }

        if (minx <= maxx)
        {
            Execute("update GDB_Layers set minx=$p0, miny=$p1, maxx=$p2, maxy=$p3 where layer_id=1", minx, miny, maxx, maxy);
        }

        for (int i = 0; i < _relationships.Count; i++)
        {
            (string type, string origin, string destination) = _relationships[i];
            Execute(
                "insert into GDB_ItemRelationships (ObjectID, UUID, Type, OriginID, DestID, Properties, Attributes) values ($p0,$p1,$p2,$p3,$p4,1,NULL)",
                i + 1, FileValues.NewGuid(), _relationshipTypes[type], origin, destination);
        }

        RowidGenerator(2, Items.Count + 1, 1);
        RowidGenerator(3, _relationships.Count + 1, 1);
        RowidGenerator(4, MobileGeodatabaseSchema.ItemTypes.Length + 1, 1);
        RowidGenerator(5, MobileGeodatabaseSchema.ItemRelationshipTypes.Length + 1, 1);
        foreach (int registration in new[] { 6, 7, 8 })
        {
            RowidGenerator(registration, 1, 1);
        }

        foreach ((int registration, long @base) in rowidGenerators)
        {
            RowidGenerator(registration, @base, 1);
        }

        Execute("insert into GDB_TablesModified values ('GDB_Layers', $p0)", NowSeconds);
        Execute("insert into GDB_TablesModified values ('GDB_TableRegistry', $p0)", NowSeconds);

        foreach (var r in MobileGeodatabaseSchema.SystemColumnRegistry)
        {
            if (_delta && NotInDelta.Contains(r.Table))
            {
                continue;
            }

            Execute(
                "insert into GDB_ColumnRegistry values ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)",
                r.Table, r.Column, r.SdeType, r.Size, r.DecimalDigits, r.Description, r.Flags, r.Flags2, r.ObjectId, r.Comment);
        }

        foreach (object?[] row in _columnRegistry)
        {
            Execute("insert into GDB_ColumnRegistry values ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", row);
        }

        foreach (string sql in _afterRows)
        {
            Execute(sql);
        }

        foreach (string sql in MobileGeodatabaseSchema.ItemsSpatialIndexTriggers)
        {
            Execute(sql);
        }

        _transaction!.Commit();
        _transaction.Dispose();
        _transaction = null;

        // The two tables whose modules only Esri's library has. SQLite will not execute their CREATE statements without
        // the module, so they are written as schema rows, which is what a CREATE would have left (research note §1, §6).
        // writable_schema is refused only under SQLITE_DBCONFIG_DEFENSIVE, which Microsoft.Data.Sqlite does not set.
        Execute("PRAGMA writable_schema=ON");
        foreach ((string name, string sql) in MobileGeodatabaseSchema.EsriModuleTables)
        {
            Execute("insert into sqlite_master (type, name, tbl_name, rootpage, sql) values ('table', $p0, $p0, 0, $p1)", name, sql);
        }

        Execute("PRAGMA writable_schema=OFF");
        _finished = true;
        _db.Close();
    }

    /// <summary>Adds a <c>GDB_RowidGenerators</c> row: the next id to hand out, and the last one handed.</summary>
    public void RowidGenerator(int registration, long next, long last) =>
        Execute("insert into GDB_RowidGenerators values ($p0, $p1, -1, $p2)", registration, next, last);

    /// <summary>Runs one statement with positional values bound as <c>$p0</c>, <c>$p1</c>, ….</summary>
    public void Execute(string sql, params object?[] values)
    {
        using SqliteCommand command = Command(sql, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i] ?? DBNull.Value;
        }

        command.ExecuteNonQuery();
    }

    /// <summary>A command in the open transaction with <paramref name="parameters"/> positional parameters, for reuse.</summary>
    public SqliteCommand Command(string sql, int parameters)
    {
        SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        for (int i = 0; i < parameters; i++)
        {
            command.Parameters.Add(new SqliteParameter("$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), DBNull.Value));
        }

        return command;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _transaction?.Dispose();
        _db.Dispose();
    }
}
