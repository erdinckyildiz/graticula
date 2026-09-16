using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis.Pbf;

/// <summary>
/// Writes a FeatureServer <c>query</c> response as <c>f=pbf</c>: Esri's published FeatureCollection
/// Protocol Buffers message — ADR-073.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same answer as <see cref="FeatureServerQueryWriter"/>, in another encoding.</b> The header
/// fields, the types and aliases, the object id, the GlobalID, the value rules — a 64-bit integer as
/// text, a date as epoch milliseconds, a GUID braced — and the <c>exceededTransferLimit</c> rule are
/// the JSON writer's, so a client that switches format gets the same rows and the same columns. The
/// field numbers are the specification's
/// (<c>github.com/Esri/arcgis-pbf/proto/FeatureCollection</c>, Apache 2.0), and nothing else is.
/// </para>
/// <para>
/// <b>Buffered, and bounded by the response ceiling rather than streamed.</b> A length-delimited
/// message states its length before its bytes, and the features are inside one. So this writer
/// holds the response until the last feature, which the JSON face deliberately does not
/// (ADR-062); the byte ceiling (Q-113) is what keeps that finite, and it is checked after every
/// feature exactly as the JSON writer checks it. The encoding is several times smaller than the
/// JSON it replaces, so the same ceiling holds more rows.
/// </para>
/// </remarks>
public sealed class FeatureCollectionPbfWriter
{
    /// <summary>The media type of a PBF response.</summary>
    public const string ContentType = "application/x-protobuf";

    private readonly LayerDefinition _layer;
    private readonly long _maximumBytes;
    private readonly Dictionary<string, FieldDescription> _fields;

    /// <summary>Creates a writer for one layer.</summary>
    /// <param name="layer">The layer being written.</param>
    /// <param name="maximumBytes">The most bytes the body may reach before truncation, or 0 for no ceiling.</param>
    /// <param name="fields">The layer's columns as its layer document describes them.</param>
    public FeatureCollectionPbfWriter(
        LayerDefinition layer, long maximumBytes = 0, IReadOnlyList<FieldDescription>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        if (!layer.HasIntegerIdentity)
        {
            throw new ArgumentException(
                $"Layer '{layer.Name}' has no integer object-id column, so it cannot be served "
                + "through the ArcGIS surface (ADR-013 §2a).",
                nameof(layer));
        }

        _layer = layer;
        _maximumBytes = maximumBytes;
        _fields = (fields ?? []).ToDictionary(field => field.Name, StringComparer.Ordinal);
    }

    /// <summary>Writes a feature query's answer.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="source">Where features come from.</param>
    /// <param name="query">What to read.</param>
    /// <param name="geometryType">The layer's declared geometry type.</param>
    /// <param name="quantization">The integer grid coordinates are written on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="ordinates">
    /// Which of Z and M every geometry carries — what the column declares and the query asked for. The
    /// header says it before the features, and each vertex is written with exactly that many numbers.
    /// </param>
    /// <returns>How many features were written.</returns>
    public async Task<int> WriteAsync(
        Stream output,
        IFeatureSource source,
        FeatureQuery query,
        GeometryKind geometryType,
        PbfQuantization quantization,
        CancellationToken cancellationToken,
        GeometryOrdinates ordinates = GeometryOrdinates.None)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(quantization);

        FeatureSchema schema = source.SchemaFor(query);
        int objectIdIndex = schema.IndexOf(_layer.IntegerIdentityColumn!);

        if (objectIdIndex < 0 && !query.Distinct)
        {
            throw new ArgumentException(
                $"The query must request '{_layer.IntegerIdentityColumn}' so it can be written as the object id.",
                nameof(query));
        }

        string? globalId = GlobalIds.FieldOf([.. _fields.Values]);
        int srid = query.OutSrid ?? _layer.Srid;

        ProtoBuffer result = new();
        result.String(1, query.Distinct ? string.Empty : _layer.IntegerIdentityColumn!);

        if (globalId is not null)
        {
            result.String(3, globalId);
        }

        result.UInt(7, (ulong)GeometryTypeOf(geometryType));
        result.Message(8, Reference(srid, query.OutWkt));

        // <b>`hasZ` and `hasM` — ADR-077 §9.</b> A reader takes the stride of every vertex from these two,
        // so they are the promise each geometry below keeps.
        if ((ordinates & GeometryOrdinates.Z) != 0)
        {
            result.Bool(10, true);
        }

        if ((ordinates & GeometryOrdinates.M) != 0)
        {
            result.Bool(11, true);
        }

        ProtoBuffer transform = new();
        transform.UInt(1, quantization.UpperLeft ? 0UL : 1UL);

        // The specification numbers them x, y, m, z — M is field 3 and Z is field 4, in both messages.
        ProtoBuffer scale = new();
        scale.Double(1, quantization.Tolerance);
        scale.Double(2, quantization.Tolerance);

        if (ordinates != GeometryOrdinates.None)
        {
            scale.Double(3, quantization.OrdinateScale);
            scale.Double(4, quantization.OrdinateScale);
        }

        transform.Message(2, scale);

        ProtoBuffer translate = new();
        translate.Double(1, quantization.OriginX);
        translate.Double(2, quantization.OriginY);

        if (ordinates != GeometryOrdinates.None)
        {
            translate.Double(3, 0);
            translate.Double(4, 0);
        }

        transform.Message(3, translate);

        result.Message(12, transform);

        int[] kinds = new int[schema.Count];

        for (int i = 0; i < schema.Count; i++)
        {
            string name = schema.Names[i];
            ProtoBuffer field = new();
            field.String(1, name);

            int type;
            string alias;

            if (i == objectIdIndex)
            {
                type = 6;
                alias = _fields.TryGetValue(name, out FieldDescription id) ? id.Label : name;
            }
            else if (_fields.TryGetValue(name, out FieldDescription described))
            {
                type = string.Equals(name, globalId, StringComparison.Ordinal) ? 11 : FieldTypeOf(described.Type);
                alias = described.Label;
            }
            else
            {
                type = 4;
                alias = name;
            }

            kinds[i] = type;
            field.UInt(2, (ulong)type);
            field.String(3, alias);
            result.Message(13, field);
        }

        int written = 0;
        bool truncatedBySize = false;

        await foreach (Feature feature in source.ReadAsync(query, cancellationToken).ConfigureAwait(false))
        {
            result.Message(15, FeatureMessage(feature, schema, objectIdIndex, kinds, quantization, ordinates));
            written++;

            // After writing, so one feature is always returned and a paging loop advances — the
            // JSON writer's rule, for the JSON writer's reason.
            if (_maximumBytes > 0 && result.Length >= _maximumBytes)
            {
                truncatedBySize = true;
                break;
            }
        }

        result.Bool(9, truncatedBySize || written >= query.Limit);

        ProtoBuffer queryResult = new();
        queryResult.Message(1, result);

        await WriteRootAsync(output, queryResult, cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>Writes a <c>returnCountOnly</c> answer.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="count">The count.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task WriteCountAsync(Stream output, long count, CancellationToken cancellationToken)
    {
        ProtoBuffer counted = new();
        counted.UInt(1, (ulong)Math.Max(0, count));

        ProtoBuffer queryResult = new();
        queryResult.Message(2, counted);

        return WriteRootAsync(output, queryResult, cancellationToken);
    }

    /// <summary>Writes a <c>returnIdsOnly</c> answer.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="objectIdFieldName">The object id column.</param>
    /// <param name="ids">The ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task WriteIdsAsync(
        Stream output, string objectIdFieldName, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        ProtoBuffer result = new();
        result.String(1, objectIdFieldName);
        result.PackedUInt64(3, [.. ids]);

        ProtoBuffer queryResult = new();
        queryResult.Message(3, result);

        return WriteRootAsync(output, queryResult, cancellationToken);
    }

    /// <summary>Writes a <c>returnExtentOnly</c> answer.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="extent">The extent, or null when nothing matched.</param>
    /// <param name="count">How many features matched.</param>
    /// <param name="srid">The reference the extent is in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task WriteExtentAsync(
        Stream output, Envelope? extent, long count, int srid, CancellationToken cancellationToken)
    {
        ProtoBuffer result = new();

        if (extent is { } box)
        {
            ProtoBuffer envelope = new();
            envelope.Double(1, box.MinX);
            envelope.Double(2, box.MinY);
            envelope.Double(3, box.MaxX);
            envelope.Double(4, box.MaxY);
            envelope.Message(5, Reference(srid, null));
            result.Message(1, envelope);
        }

        result.UInt(2, (ulong)Math.Max(0, count));

        ProtoBuffer queryResult = new();
        queryResult.Message(4, result);

        return WriteRootAsync(output, queryResult, cancellationToken);
    }

    private static async Task WriteRootAsync(Stream output, ProtoBuffer queryResult, CancellationToken cancellationToken)
    {
        ProtoBuffer root = new();
        root.String(1, "1");
        root.Message(2, queryResult);

        await output.WriteAsync(root.Span.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProtoBuffer Reference(int srid, string? wkt)
    {
        ProtoBuffer reference = new();

        if (wkt is { Length: > 0 })
        {
            reference.String(5, wkt);
        }
        else
        {
            reference.UInt(1, (ulong)Math.Max(0, srid));
            reference.UInt(2, (ulong)Math.Max(0, srid));
        }

        return reference;
    }

    /// <summary>The specification's <c>GeometryType</c> number.</summary>
    internal static int GeometryTypeOf(GeometryKind kind) => kind switch
    {
        GeometryKind.Point => 0,
        GeometryKind.MultiPoint => 1,
        GeometryKind.LineString or GeometryKind.MultiLineString => 2,
        GeometryKind.Polygon or GeometryKind.MultiPolygon => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No ArcGIS equivalent."),
    };

    /// <summary>
    /// The specification's <c>FieldType</c> number, following <see cref="FeatureServerMetadataWriter.TypeName"/>
    /// so the two documents never disagree about a column.
    /// </summary>
    internal static int FieldTypeOf(FieldType type) => type switch
    {
        FieldType.SmallInteger or FieldType.Boolean => 0,
        FieldType.Integer => 1,
        FieldType.Single => 2,
        FieldType.Double => 3,
        FieldType.Date => 5,
        FieldType.Guid => 10,
        FieldType.Binary => 8,
        _ => 4,
    };

    private static ProtoBuffer FeatureMessage(
        Feature feature,
        FeatureSchema schema,
        int objectIdIndex,
        int[] kinds,
        PbfQuantization quantization,
        GeometryOrdinates ordinates)
    {
        ProtoBuffer message = new();

        for (int i = 0; i < schema.Count; i++)
        {
            message.Message(1, Value(feature[i], i == objectIdIndex, kinds[i]));
        }

        if (feature.Geometry is { IsEmpty: false } geometry && Geometry(geometry, quantization, ordinates) is { } encoded)
        {
            message.Message(2, encoded);
        }

        return message;
    }

    private static ProtoBuffer Value(object? value, bool objectId, int kind)
    {
        ProtoBuffer message = new();

        switch (value)
        {
            case null:
                message.Bool(10, true);
                break;

            case int or long or short when objectId:
                long id = Convert.ToInt64(value, CultureInfo.InvariantCulture);

                if (id is >= 0 and <= uint.MaxValue)
                {
                    message.UInt(5, (ulong)id);
                }
                else
                {
                    message.SInt(8, id);
                }

                break;

            case string text:
                message.String(1, text);
                break;
            case Guid guid:
                message.String(1, GlobalIds.Braced(guid));
                break;
            case bool flag:
                message.SInt(4, flag ? 1 : 0);
                break;
            case int number:
                message.SInt(4, number);
                break;
            case short number:
                message.SInt(4, number);
                break;

            // Text, as the JSON writer sends it: a 64-bit integer has no ArcGIS field type.
            case long number:
                message.String(1, number.ToString(CultureInfo.InvariantCulture));
                break;

            case float number when kind == 2:
                message.Float(2, number);
                break;
            case float number:
                message.Double(3, number);
                break;
            case double number:
                message.Double(3, number);
                break;
            case decimal number:
                message.Double(3, (double)number);
                break;

            case DateTime timestamp:
                message.SInt(8, new DateTimeOffset(timestamp.ToUniversalTime()).ToUnixTimeMilliseconds());
                break;
            case DateTimeOffset timestamp:
                message.SInt(8, timestamp.ToUnixTimeMilliseconds());
                break;

            default:
                message.String(1, value.ToString() ?? string.Empty);
                break;
        }

        return message;
    }

    /// <summary>
    /// Encodes a geometry on the grid, or returns null when view mode left nothing of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each part starts from an absolute vertex and the rest are differences</b> — the
    /// specification's polygon example starts its second ring at <c>56, 56</c>, not at the difference
    /// from the first ring's last vertex.
    /// </para>
    /// <para>
    /// <b>Rings are wound the ArcGIS way, as the JSON writer winds them</b> — shell clockwise, holes
    /// counter-clockwise, in world coordinates, which an upper-left origin does not change.
    /// </para>
    /// </remarks>
    internal static ProtoBuffer? Geometry(
        Geometry geometry, PbfQuantization quantization, GeometryOrdinates ordinates = GeometryOrdinates.None)
    {
        List<uint> lengths = [];
        List<long> coords = [];
        bool withZ = (ordinates & GeometryOrdinates.Z) != 0;
        bool withM = (ordinates & GeometryOrdinates.M) != 0;

        switch (geometry)
        {
            case Point point:
                coords.Add(quantization.X(point.X));
                coords.Add(quantization.Y(point.Y));
                Ordinates(point.Z, point.M);
                break;

            case MultiPoint multiPoint:
            {
                int count = multiPoint.Parts.Count;
                double[] flat = new double[count * 2];
                double[]? zs = withZ ? new double[count] : null;
                double[]? ms = withM ? new double[count] : null;

                for (int i = 0; i < count; i++)
                {
                    Point part = multiPoint.Parts[i];
                    flat[i * 2] = part.X;
                    flat[(i * 2) + 1] = part.Y;

                    if (zs is not null)
                    {
                        zs[i] = part.Z ?? throw Unkept(GeometryOrdinates.Z);
                    }

                    if (ms is not null)
                    {
                        ms[i] = part.M ?? throw Unkept(GeometryOrdinates.M);
                    }
                }

                // Every point is kept, in either mode: two points on one cell are still two points.
                Part(XySequence.Wrap(flat, zs, ms), reversed: false, minimum: 1, keepDuplicates: true);
                break;
            }

            case MultiLineString lines:
                foreach (LineString line in lines.Parts)
                {
                    Part(line.Coordinates, reversed: false, minimum: 2);
                }

                break;

            case LineString line:
                Part(line.Coordinates, reversed: false, minimum: 2);
                break;

            case MultiPolygon polygons:
                foreach (Polygon polygon in polygons.Parts)
                {
                    Rings(polygon);
                }

                break;

            case Polygon polygon:
                Rings(polygon);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(geometry), geometry.Kind, "No ArcGIS equivalent.");
        }

        if (coords.Count == 0)
        {
            return null;
        }

        ProtoBuffer message = new();

        if (geometry is not Point)
        {
            message.PackedUInt(2, [.. lengths]);
        }

        message.PackedSInt(3, [.. coords]);
        return message;

        void Rings(Polygon polygon)
        {
            if (!Part(polygon.Shell.Coordinates, polygon.Shell.IsCounterClockwise, minimum: 4))
            {
                // A shell that collapsed takes its holes with it: a hole with no shell is a shell.
                return;
            }

            foreach (LinearRing hole in polygon.Holes)
            {
                Part(hole.Coordinates, !hole.IsCounterClockwise, minimum: 4);
            }
        }

        bool Part(XySequence sequence, bool reversed, int minimum, bool keepDuplicates = false)
        {
            int start = coords.Count;
            long previousX = 0;
            long previousY = 0;
            long lastX = 0;
            long lastY = 0;
            int kept = 0;

            if ((withZ && !sequence.HasZ) || (withM && !sequence.HasM))
            {
                throw Unkept(withZ && !sequence.HasZ ? GeometryOrdinates.Z : GeometryOrdinates.M);
            }

            for (int n = 0; n < sequence.Count; n++)
            {
                int i = reversed ? sequence.Count - 1 - n : n;
                long x = quantization.X(sequence.X(i));
                long y = quantization.Y(sequence.Y(i));

                if (kept > 0 && quantization.View && !keepDuplicates && x == lastX && y == lastY)
                {
                    continue;
                }

                if (kept == 0)
                {
                    coords.Add(x);
                    coords.Add(y);
                }
                else
                {
                    coords.Add(x - previousX);
                    coords.Add(y - previousY);
                }

                Ordinates(withZ ? sequence.Z(i) : null, withM ? sequence.M(i) : null);

                previousX = x;
                previousY = y;
                lastX = x;
                lastY = y;
                kept++;
            }

            // Edit mode keeps whatever it was given; view mode lets a part go once it has
            // collapsed below what its kind needs.
            if (kept == 0 || (quantization.View && kept < minimum))
            {
                coords.RemoveRange(start, coords.Count - start);
                return false;
            }

            lengths.Add((uint)kept);
            return true;
        }

        // <b>Z and M are absolute, not differences — measured, because the specification does not say.</b>
        // The published proto has `zScale`, `mScale` and their translations and no word on how the numbers
        // in `coords` use them. The ArcGIS Maps SDK for JavaScript 4.30, given one polyline written both
        // ways on 2026-09-17, decoded x and y as differences and Z and M as `translate + scale * value`
        // from each vertex's own number: the delta-encoded file came back with every vertex at the first
        // vertex's elevation. So each vertex carries its own.
        void Ordinates(double? z, double? m)
        {
            if (withZ)
            {
                coords.Add(quantization.Ordinate(z ?? throw Unkept(GeometryOrdinates.Z)));
            }

            if (withM)
            {
                coords.Add(quantization.Ordinate(m ?? throw Unkept(GeometryOrdinates.M)));
            }
        }

        // <b>A geometry short of what the header promised is an error, not a zero.</b> The stride of every
        // vertex after it comes from the header, so writing it short would misread the rest of the answer,
        // and writing a zero would invent an elevation. The column's declaration and the reader's mask make
        // this unreachable; it throws so that it stays so.
        static InvalidOperationException Unkept(GeometryOrdinates missing) => new(
            $"The answer's header promised {Graticula.Geometries.Ordinates.Name(missing)} on every geometry and one was read without it.");
    }
}
