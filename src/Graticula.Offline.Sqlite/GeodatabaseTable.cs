using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Microsoft.Data.Sqlite;

namespace Graticula.Offline.Sqlite;

/// <summary>The geometry column of a feature class, and how its geometries are stored.</summary>
/// <param name="GeometryType">The ArcGIS geometry type.</param>
/// <param name="Ordinates">The ordinates beyond x and y.</param>
/// <param name="Wkid">The reference's well-known id as the service gives it.</param>
/// <param name="Geographic">Whether the reference is geographic.</param>
/// <param name="Wkt">The reference's WKT.</param>
internal sealed record TableGeometry(string GeometryType, GeometryOrdinates Ordinates, int Wkid, bool Geographic, string Wkt)
{
    /// <summary>The column's name, as the server's files name it.</summary>
    public const string Column = "shape";

    /// <summary>The SRID the column declares: Web Mercator's EPSG code for its Esri one.</summary>
    public int Srid => Wkid == 102100 ? 3857 : Wkid;

    /// <summary>The grid the geometries are stored on.</summary>
    public CoordinateGrid Grid => CoordinateGrid.For(Wkid, Geographic);

    /// <summary>Whether the column carries Z.</summary>
    public bool HasZ => Ordinates.HasFlag(GeometryOrdinates.Z);

    /// <summary>Whether the column carries M.</summary>
    public bool HasM => Ordinates.HasFlag(GeometryOrdinates.M);

    /// <summary>The geometry of a layer, or null for a table.</summary>
    public static TableGeometry? Of(OfflineLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.GeometryType is null
            ? null
            : new TableGeometry(layer.GeometryType, layer.Ordinates, layer.Wkid, layer.Geographic, layer.ReferenceWkt);
    }
}

/// <summary>
/// One table of a mobile geodatabase: created, filled row by row with its R*Tree beside it, then registered — ADR-288.
/// </summary>
internal sealed class GeodatabaseTable : IDisposable
{
    private readonly GeodatabaseBuilder _file;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand? _index;
    private readonly int _objectIdOrdinal;
    private double _minX = double.MaxValue, _minY = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue;
    private double _minZ = double.MaxValue, _maxZ = double.MinValue;

    private GeodatabaseTable(
        GeodatabaseBuilder file, string name, IReadOnlyList<OfflineField> fields, string objectId, string? globalId,
        TableGeometry? geometry, bool archived, int registration)
    {
        _file = file;
        Name = name;
        Fields = fields;
        ObjectId = objectId;
        GlobalId = globalId;
        Geometry = geometry;
        Archived = archived;
        Registration = registration;
        Columns = [.. fields.Select(static f => f.Name), .. geometry is null ? Array.Empty<string>() : [TableGeometry.Column]];
        _objectIdOrdinal = fields.Select(static f => f.Name).ToList().FindIndex(n => string.Equals(n, objectId, StringComparison.OrdinalIgnoreCase));
        if (_objectIdOrdinal < 0)
        {
            throw new ArgumentException($"The object id field '{objectId}' is not among the fields of {name}.", nameof(objectId));
        }

        int count = Columns.Count + (archived ? 2 : 0);
        string columnList = string.Join(",", Columns) + (archived ? ",gdb_from_date,gdb_to_date" : string.Empty);
        string values = string.Join(",", Enumerable.Range(0, count).Select(static i => "$p" + i.ToString(CultureInfo.InvariantCulture)));
        _insert = file.Command($"insert into [{name}] ({columnList}) values ({values}) returning _rowid_", count);
        if (geometry is not null)
        {
            _index = file.Command($"insert into [{TableStatements.SpatialIndexName(name, TableGeometry.Column)}] values ($p0,$p1,$p2,$p3,$p4)", 5);
        }
    }

    /// <summary>The table's name.</summary>
    public string Name { get; }

    /// <summary>Its fields, geometry excluded.</summary>
    public IReadOnlyList<OfflineField> Fields { get; }

    /// <summary>Its object id field.</summary>
    public string ObjectId { get; }

    /// <summary>Its global id field, or null.</summary>
    public string? GlobalId { get; }

    /// <summary>Its geometry column, or null for a table.</summary>
    public TableGeometry? Geometry { get; }

    /// <summary>Whether it is archived, as a registered replica's tables are.</summary>
    public bool Archived { get; }

    /// <summary>Its registration id.</summary>
    public int Registration { get; }

    /// <summary>Its columns, object id first if the fields put it first, geometry last, archive columns excluded.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>How many rows were written.</summary>
    public int Count { get; private set; }

    /// <summary>The largest object id written, or zero.</summary>
    public long MaxObjectId { get; private set; }

    /// <summary>The extent of the geometries written, or null when none was.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Extent =>
        _minX <= _maxX ? (_minX, _minY, _maxX, _maxY) : null;

    /// <summary>The Z range of the geometries written, when the column has Z and a geometry was written.</summary>
    public (double Min, double Max)? ZRange => _minZ <= _maxZ ? (_minZ, _maxZ) : null;

    /// <summary>Creates and registers a table.</summary>
    /// <param name="file">The file.</param>
    /// <param name="name">The table's name.</param>
    /// <param name="fields">Its fields, geometry excluded.</param>
    /// <param name="objectId">Its object id field.</param>
    /// <param name="globalId">Its global id field, or null.</param>
    /// <param name="geometry">Its geometry column, or null.</param>
    /// <param name="archived">Whether it is archived.</param>
    /// <param name="registrationFlags">
    /// Its <c>GDB_TableRegistry.object_flags</c>; the archive bit is added here when <paramref name="archived"/>.
    /// </param>
    /// <param name="bracketed">Whether the server writes the CREATE with the name in brackets, as it does for some tables.</param>
    public static GeodatabaseTable Create(
        GeodatabaseBuilder file, string name, IReadOnlyList<OfflineField> fields, string objectId, string? globalId,
        TableGeometry? geometry, bool archived, int registrationFlags, bool bracketed)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fields);
        var columns = fields.Select(f => TableStatements.Column(f, archived)).ToList();
        if (geometry is not null)
        {
            columns.Add(TableStatements.Shape(TableGeometry.Column));
        }

        file.Execute($"CREATE TABLE {(bracketed ? $"[{name}]" : name)} ({string.Join(", ", columns)}{(archived ? TableStatements.ArchiveColumns : string.Empty)})");
        int registration = file.Register(name, objectId, registrationFlags | (archived ? 0x40000 : 0));
        if (geometry is not null)
        {
            file.Execute(TableStatements.SpatialIndex(name, TableGeometry.Column));
        }

        return new GeodatabaseTable(file, name, fields, objectId, globalId, geometry, archived, registration);
    }

    /// <summary>Writes one row.</summary>
    /// <param name="values">One value per field, in field order.</param>
    /// <param name="geometry">The geometry, or null.</param>
    public void Insert(IReadOnlyList<object?> values, Geometry? geometry)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != Fields.Count)
        {
            throw new ArgumentException($"A row of {Name} has {values.Count} values for {Fields.Count} fields.", nameof(values));
        }

        for (int i = 0; i < Fields.Count; i++)
        {
            _insert.Parameters[i].Value = ToFile(Fields[i], values[i]) ?? DBNull.Value;
        }

        int next = Fields.Count;
        Envelope bounds = Envelope.Empty;
        if (Geometry is not null)
        {
            byte[]? blob = null;
            if (geometry is not null && !geometry.IsEmpty)
            {
                blob = MobileGeodatabaseGeometry.Encode(geometry, Geometry.Srid, Geometry.Grid, Geometry.Ordinates);
                bounds = geometry.Envelope;
                if (Geometry.HasZ)
                {
                    foreach (double z in Elevations(geometry))
                    {
                        _minZ = Math.Min(_minZ, z);
                        _maxZ = Math.Max(_maxZ, z);
                    }
                }
            }

            _insert.Parameters[next++].Value = (object?)blob ?? DBNull.Value;
        }

        if (Archived)
        {
            _insert.Parameters[next++].Value = FileValues.ServerRowStart;
            _insert.Parameters[next].Value = FileValues.OpenEnd;
        }

        long rowid = Convert.ToInt64(_insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        Count++;
        if (values[_objectIdOrdinal] is { } oid)
        {
            MaxObjectId = Math.Max(MaxObjectId, Convert.ToInt64(oid, CultureInfo.InvariantCulture));
        }

        if (_index is not null && !bounds.IsEmpty)
        {
            _index.Parameters[0].Value = rowid;
            _index.Parameters[1].Value = bounds.MinX;
            _index.Parameters[2].Value = bounds.MaxX;
            _index.Parameters[3].Value = bounds.MinY;
            _index.Parameters[4].Value = bounds.MaxY;
            _index.ExecuteNonQuery();
            _minX = Math.Min(_minX, bounds.MinX);
            _minY = Math.Min(_minY, bounds.MinY);
            _maxX = Math.Max(_maxX, bounds.MaxX);
            _maxY = Math.Max(_maxY, bounds.MaxY);
        }
    }

    /// <summary>A value as the file holds it for a field.</summary>
    public static object? ToFile(OfflineField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (value is null or DBNull)
        {
            return null;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        return field.EsriType switch
        {
            "esriFieldTypeOID" or "esriFieldTypeSmallInteger" or "esriFieldTypeInteger" => Convert.ToInt64(value, c),
            "esriFieldTypeDouble" or "esriFieldTypeSingle" => Convert.ToDouble(value, c),
            "esriFieldTypeString" => Convert.ToString(value, c),
            "esriFieldTypeDate" => value switch
            {
                DateTimeOffset moment => FileValues.Julian(moment),
                DateTime moment => FileValues.Julian(new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc))),
                long epochMilliseconds => FileValues.Julian(DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds)),
                _ => Convert.ToDouble(value, c),
            },
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => value switch
            {
                Guid g => FileValues.Guid(g),
                _ => FileValues.Guid(System.Guid.Parse(Convert.ToString(value, c)!)),
            },
            "esriFieldTypeBlob" => value switch
            {
                byte[] bytes => bytes,
                ReadOnlyMemory<byte> memory => memory.ToArray(),
                Memory<byte> memory => memory.ToArray(),
                _ => throw new ArgumentException($"A blob field takes bytes, not {value.GetType().Name}.", nameof(value)),
            },
            _ => throw new NotSupportedException($"'{field.EsriType}' cannot be written to an offline copy."),
        };
    }

    /// <summary>A value the file holds, as the port's records carry it.</summary>
    public static object? FromFile(OfflineField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (value is null or DBNull)
        {
            return null;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        return field.EsriType switch
        {
            "esriFieldTypeOID" => Convert.ToInt64(value, c),
            "esriFieldTypeSmallInteger" => Convert.ToInt16(value, c),
            "esriFieldTypeInteger" => Convert.ToInt32(value, c),
            "esriFieldTypeDouble" or "esriFieldTypeSingle" => Convert.ToDouble(value, c),
            "esriFieldTypeString" => Convert.ToString(value, c),
            "esriFieldTypeDate" => FileValues.FromJulian(Convert.ToDouble(value, c)),
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => System.Guid.Parse(Convert.ToString(value, c)!),
            "esriFieldTypeBlob" => value as byte[],
            _ => value,
        };
    }

    /// <summary>Every elevation a geometry carries.</summary>
    public static IEnumerable<double> Elevations(Geometry geometry)
    {
        switch (geometry)
        {
            case Point p when p.Z is double z:
                yield return z;
                break;
            case LineString line when line.Coordinates.HasZ:
                for (int i = 0; i < line.Coordinates.Count; i++)
                {
                    yield return line.Coordinates.Z(i);
                }

                break;
            case Polygon polygon:
                foreach (LineString ring in polygon.Holes.Prepend(polygon.Shell))
                {
                    foreach (double z in Elevations(ring))
                    {
                        yield return z;
                    }
                }

                break;
            case MultiPoint parts:
                foreach (double z in parts.Parts.SelectMany(Elevations))
                {
                    yield return z;
                }

                break;
            case MultiLineString parts:
                foreach (double z in parts.Parts.SelectMany(Elevations))
                {
                    yield return z;
                }

                break;
            case MultiPolygon parts:
                foreach (double z in parts.Parts.SelectMany(Elevations))
                {
                    yield return z;
                }

                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _insert.Dispose();
        _index?.Dispose();
    }
}
