using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Microsoft.Data.Sqlite;

namespace Graticula.Offline.Sqlite.Tests;

/// <summary>
/// A four-layer service in Web Mercator, shaped like the one ArcGIS Server replicated for research note §4: points with
/// Z and attachments, lines, areas with a hole, and a table; a coded value domain and a range domain.
/// </summary>
internal static class OfflineFixture
{
    public const string Service = "graticula_test_offline";

    public static readonly OfflineCodedDomain Kind = new(
        Service + "_KindDomain",
        [new KeyValuePair<object, string>((short)1, "Hydrant"), new KeyValuePair<object, string>((short)2, "Valve")]);

    public static readonly OfflineRangeDomain Size = new(Service + "_SizeRange", 0, 2000);

    public static readonly Guid ReplicaId = Guid.Parse("5b1f0c3e-6a6d-4f7b-9d7e-2b0a8f1c4e21");

    private static readonly DateTimeOffset Seen = new(2026, 11, 9, 8, 30, 0, TimeSpan.Zero);

    public static OfflineField[] FeatureFields =>
    [
        new("objectid", "esriFieldTypeOID", "OBJECTID", null, false, null),
        new("globalid", "esriFieldTypeGlobalID", "GlobalID", 38, false, null),
        new("name", "esriFieldTypeString", "Name", 100, true, null),
        new("kind", "esriFieldTypeSmallInteger", "Kind", null, true, Kind),
        new("size_mm", "esriFieldTypeDouble", "Size (mm)", null, true, Size),
        new("seen", "esriFieldTypeDate", "Seen", 8, true, null),
        new("count_", "esriFieldTypeInteger", "Count", null, true, null),
    ];

    public static OfflineField[] TableFields =>
    [
        new("objectid", "esriFieldTypeOID", "OBJECTID", null, false, null),
        new("globalid", "esriFieldTypeGlobalID", "GlobalID", 38, false, null),
        new("name", "esriFieldTypeString", "Name", 100, true, null),
        new("asset_gid", "esriFieldTypeGUID", "Asset", 38, true, null),
    ];

    public static Guid Gid(int layer, int oid) => new($"00000000-0000-4000-8000-{layer:D4}{oid:D8}");

    public static object?[] Values(int layer, int oid, string name) =>
        [(long)oid, Gid(layer, oid), name, (short)(1 + (oid % 2)), 100.5 * oid, Seen.AddDays(oid), oid * 3];

    public static IReadOnlyList<OfflineRow> Points =>
    [
        .. Enumerable.Range(1, 5).Select(i => new OfflineRow(
            Values(0, i, $"point {i}"),
            Point.Create(3100000 + (1000 * i), 4700000 + (500 * i), 10 + i + 0.5, null))),
    ];

    public static IReadOnlyList<OfflineRow> Lines =>
    [
        new(Values(1, 1, "line 1"), new LineString(XySequence.Wrap([3100000, 4700000, 3101000, 4701000, 3102500, 4700500]))),
        new(Values(1, 2, "line 2"), new MultiLineString(
        [
            new LineString(XySequence.Wrap([3103000, 4700000, 3104000, 4702000])),
            new LineString(XySequence.Wrap([3105000, 4700000, 3106000, 4701000])),
        ])),
    ];

    /// <summary>A square with a square hole, the shell clockwise as ArcGIS JSON would have it.</summary>
    public static Polygon Area => new(
        new LinearRing(XySequence.Wrap([3100000, 4700000, 3100000, 4702000, 3102000, 4702000, 3102000, 4700000, 3100000, 4700000])),
        [new LinearRing(XySequence.Wrap([3100500, 4700500, 3101500, 4700500, 3101500, 4701500, 3100500, 4701500, 3100500, 4700500]))]);

    public static IReadOnlyList<OfflineRow> Areas => [new(Values(2, 1, "area 1"), Area)];

    public static IReadOnlyList<OfflineRow> Inspections =>
    [
        new([1L, Gid(3, 1), "inspection 1", Gid(0, 1)], null),
        new([2L, Gid(3, 2), "inspection 2", null], null),
    ];

    public static IReadOnlyList<OfflineAttachment> PointAttachments =>
    [
        new(1, Gid(0, 1), Gid(10, 1), "photo.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }),
        new(2, Gid(0, 2), Gid(10, 2), "note.txt", "text/plain", "hello"u8.ToArray()),
    ];

    private const string MercatorWkt =
        "PROJCS[\"WGS_1984_Web_Mercator_Auxiliary_Sphere\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Mercator_Auxiliary_Sphere\"],PARAMETER[\"False_Easting\",0.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",0.0],PARAMETER[\"Standard_Parallel_1\",0.0],PARAMETER[\"Auxiliary_Sphere_Type\",0.0],UNIT[\"Meter\",1.0],AUTHORITY[\"EPSG\",3857]]";

    public static OfflineLayer PointsLayer => Layer(0, "points", "esriGeometryPoint", GeometryOrdinates.Z, FeatureFields, true, Points, PointAttachments);

    public static OfflineLayer LinesLayer => Layer(1, "lines", "esriGeometryPolyline", GeometryOrdinates.None, FeatureFields, false, Lines, []);

    public static OfflineLayer AreasLayer => Layer(2, "areas", "esriGeometryPolygon", GeometryOrdinates.None, FeatureFields, false, Areas, []);

    public static OfflineLayer InspectionsLayer => Layer(3, "inspections", null, GeometryOrdinates.None, TableFields, false, Inspections, []);

    public static IReadOnlyList<OfflineLayer> Layers => [PointsLayer, LinesLayer, AreasLayer, InspectionsLayer];

    public static OfflineReplica Replica => new(
        ReplicaId,
        "graticula_offline_test",
        "https://example.invalid/arcgis/rest/services/graticula_test_offline/FeatureServer",
        "tester",
        1794288316868,
        new DateTimeOffset(2026, 11, 10, 7, 0, 0, TimeSpan.Zero),
        new Envelope(3000000, 4500000, 3300000, 5000000),
        102100);

    public static OfflineCopy Copy(bool registered) =>
        new(Service, ServiceDocument(), Layers, registered ? Replica : null);

    private static OfflineLayer Layer(
        int id, string name, string? geometryType, GeometryOrdinates ordinates, OfflineField[] fields, bool attachments,
        IReadOnlyList<OfflineRow> rows, IReadOnlyList<OfflineAttachment> files) =>
        new(id, name, LayerDocument(id, name, geometryType, ordinates, fields, attachments), geometryType, ordinates, 102100, false, MercatorWkt,
            fields, "objectid", "globalid", attachments, new Envelope(3100000, 4700000, 3106000, 4702500),
            ct => Stream(rows, ct), ct => Stream(files, ct));

    private static async IAsyncEnumerable<T> Stream<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (T item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static string ServiceDocument() => JsonSerializer.Serialize(new
    {
        currentVersion = 11.3,
        serviceDescription = "",
        hasVersionedData = false,
        supportsDisconnectedEditing = false,
        syncEnabled = true,
        supportedQueryFormats = "JSON",
        maxRecordCount = 2000,
        capabilities = "Query,Create,Update,Delete,Editing,Sync",
        spatialReference = new { wkid = 102100, latestWkid = 3857 },
        layers = new[]
        {
            new { id = 0, name = "points", geometryType = "esriGeometryPoint" },
            new { id = 1, name = "lines", geometryType = "esriGeometryPolyline" },
            new { id = 2, name = "areas", geometryType = "esriGeometryPolygon" },
        },
        tables = new[] { new { id = 3, name = "inspections" } },
    });

    private static string LayerDocument(int id, string name, string? geometryType, GeometryOrdinates ordinates, OfflineField[] fields, bool attachments) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["currentVersion"] = 11.3,
            ["id"] = id,
            ["name"] = name,
            ["type"] = geometryType is null ? "Table" : "Feature Layer",
            ["geometryType"] = geometryType,
            ["hasZ"] = ordinates.HasFlag(GeometryOrdinates.Z),
            ["hasM"] = false,
            ["objectIdField"] = "objectid",
            ["globalIdField"] = "globalid",
            ["hasAttachments"] = attachments,
            ["extent"] = new { xmin = 3100000, ymin = 4700000, xmax = 3106000, ymax = 4702500, spatialReference = new { wkid = 102100, latestWkid = 3857 } },
            ["fields"] = fields.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["type"] = f.EsriType,
                ["alias"] = f.Alias,
                ["nullable"] = f.Nullable,
                ["length"] = f.Length,
            }).ToArray(),
            ["capabilities"] = "Query,Create,Update,Delete,Editing,Sync",
        });

    /// <summary>A fresh path in a fresh folder; the writer refuses one that exists.</summary>
    public static string NewPath(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "graticula-offline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    /// <summary>Opens a written file to look at, with the collation its index needs.</summary>
    public static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        GeodatabaseBuilder.RegisterCollation(db);
        return db;
    }

    public static List<object?[]> Query(SqliteConnection db, string sql)
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

    public static object? Scalar(SqliteConnection db, string sql) => Query(db, sql).FirstOrDefault()?[0];
}
