using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Writes our geometries as GML 3.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the adapter, not in <c>Core/Formats</c>.</b> GeoJSON is a format this
/// product reads on import and writes on more than one surface; GML is what one
/// protocol is spelled in. ADR-039 §5 draws §51's line at that difference, and
/// draws it at a file so it is checkable rather than agreed.
/// </para>
/// <para>
/// <b>GML 3.2 renamed the collections and the old names are wrong here.</b> A
/// multi-linestring is a <c>gml:MultiCurve</c> of <c>gml:curveMember</c>, and a
/// multi-polygon is a <c>gml:MultiSurface</c> of <c>gml:surfaceMember</c>.
/// <c>gml:MultiLineString</c> and <c>gml:MultiPolygon</c> are GML 3.1 and earlier;
/// a 3.2 schema rejects them, and the failure surfaces at the client as an
/// unreadable layer rather than as an error here.
/// </para>
/// <para>
/// <b>Every geometry carries a <c>gml:id</c> because 3.2 requires one.</b> They
/// are derived from the feature's own identifier so they are stable across
/// requests and unique within a document, which is what the attribute is for.
/// </para>
/// </remarks>
public sealed class GmlGeometryWriter
{
    private readonly StringBuilder _coordinates = new();
    private readonly bool _latitudeFirst;
    private readonly string _srsName;

    /// <summary>Creates a writer for one coordinate reference.</summary>
    /// <param name="srid">The EPSG code the geometries are in.</param>
    public GmlGeometryWriter(int srid)
    {
        _srsName = WfsNames.CrsUrn(srid);
        _latitudeFirst = WfsNames.IsLatitudeFirst(srid);
    }

    /// <summary>Writes one geometry, including its element.</summary>
    /// <param name="xml">Where to write.</param>
    /// <param name="geometry">The shape.</param>
    /// <param name="gmlId">The identifier to give it.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(XmlWriter xml, Geometry geometry, string gmlId)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrEmpty(gmlId);

        switch (geometry)
        {
            case Point point:
                await PointAsync(xml, point, gmlId, root: true).ConfigureAwait(false);
                break;

            case LinearRing ring:
                // A ring is a LineString by inheritance and is not a geometry a
                // feature has; writing it as one keeps the switch total rather
                // than letting it fall to the refusal below by accident.
                await LineAsync(xml, ring, gmlId, root: true).ConfigureAwait(false);
                break;

            case LineString line:
                await LineAsync(xml, line, gmlId, root: true).ConfigureAwait(false);
                break;

            case Polygon polygon:
                await PolygonAsync(xml, polygon, gmlId, root: true).ConfigureAwait(false);
                break;

            case MultiPoint points:
                await StartAsync(xml, "MultiPoint", gmlId, root: true).ConfigureAwait(false);

                for (int i = 0; i < points.Parts.Count; i++)
                {
                    await xml.WriteStartElementAsync("gml", "pointMember", WfsNames.Gml)
                        .ConfigureAwait(false);

                    await PointAsync(xml, points.Parts[i], Part(gmlId, i), root: false)
                        .ConfigureAwait(false);

                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
                break;

            case MultiLineString lines:
                await StartAsync(xml, "MultiCurve", gmlId, root: true).ConfigureAwait(false);

                for (int i = 0; i < lines.Parts.Count; i++)
                {
                    await xml.WriteStartElementAsync("gml", "curveMember", WfsNames.Gml)
                        .ConfigureAwait(false);

                    await LineAsync(xml, lines.Parts[i], Part(gmlId, i), root: false)
                        .ConfigureAwait(false);

                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
                break;

            case MultiPolygon polygons:
                await StartAsync(xml, "MultiSurface", gmlId, root: true).ConfigureAwait(false);

                for (int i = 0; i < polygons.Parts.Count; i++)
                {
                    await xml.WriteStartElementAsync("gml", "surfaceMember", WfsNames.Gml)
                        .ConfigureAwait(false);

                    await PolygonAsync(xml, polygons.Parts[i], Part(gmlId, i), root: false)
                        .ConfigureAwait(false);

                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException(
                    $"'{geometry.Kind}' is not a geometry this surface can write as GML.");
        }
    }

    private static string Part(string gmlId, int index) =>
        $"{gmlId}.{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Opens a geometry element with the attributes GML 3.2 requires.
    /// </summary>
    /// <remarks>
    /// <b><c>srsName</c> on the outermost element only.</b> GML lets a member
    /// inherit it, and repeating it on every part of a multipolygon is bytes on
    /// the wire that say nothing new. Repeating it is also how the two can
    /// disagree, which is worse than verbose.
    /// </remarks>
    private async Task StartAsync(XmlWriter xml, string element, string gmlId, bool root)
    {
        await xml.WriteStartElementAsync("gml", element, WfsNames.Gml).ConfigureAwait(false);

        await xml.WriteAttributeStringAsync("gml", "id", WfsNames.Gml, gmlId).ConfigureAwait(false);

        if (root)
        {
            await xml.WriteAttributeStringAsync(null, "srsName", null, _srsName)
                .ConfigureAwait(false);
        }
    }

    private async Task PointAsync(XmlWriter xml, Point point, string gmlId, bool root)
    {
        await StartAsync(xml, "Point", gmlId, root).ConfigureAwait(false);

        if (!point.IsEmpty)
        {
            await xml.WriteStartElementAsync("gml", "pos", WfsNames.Gml).ConfigureAwait(false);

            _coordinates.Clear();
            Append(_coordinates, point.X, point.Y);

            await xml.WriteStringAsync(_coordinates.ToString()).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private async Task LineAsync(XmlWriter xml, LineString line, string gmlId, bool root)
    {
        await StartAsync(xml, "LineString", gmlId, root).ConfigureAwait(false);
        await PosListAsync(xml, line.Coordinates).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private async Task PolygonAsync(XmlWriter xml, Polygon polygon, string gmlId, bool root)
    {
        await StartAsync(xml, "Polygon", gmlId, root).ConfigureAwait(false);

        await RingAsync(xml, "exterior", polygon.Shell).ConfigureAwait(false);

        foreach (LinearRing hole in polygon.Holes)
        {
            await RingAsync(xml, "interior", hole).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private async Task RingAsync(XmlWriter xml, string role, LinearRing ring)
    {
        if (ring.IsEmpty)
        {
            return;
        }

        await xml.WriteStartElementAsync("gml", role, WfsNames.Gml).ConfigureAwait(false);
        await xml.WriteStartElementAsync("gml", "LinearRing", WfsNames.Gml).ConfigureAwait(false);
        await PosListAsync(xml, ring.Coordinates).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private async Task PosListAsync(XmlWriter xml, XySequence coordinates)
    {
        if (coordinates.IsEmpty)
        {
            return;
        }

        await xml.WriteStartElementAsync("gml", "posList", WfsNames.Gml).ConfigureAwait(false);

        await xml.WriteAttributeStringAsync(null, "srsDimension", null, "2").ConfigureAwait(false);

        _coordinates.Clear();

        for (int i = 0; i < coordinates.Count; i++)
        {
            if (i > 0)
            {
                _coordinates.Append(' ');
            }

            Append(_coordinates, coordinates.X(i), coordinates.Y(i));
        }

        await xml.WriteStringAsync(_coordinates.ToString()).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>Appends one coordinate in the order the CRS defines.</summary>
    private void Append(StringBuilder into, double x, double y)
    {
        (double first, double second) = _latitudeFirst ? (y, x) : (x, y);

        into.Append(first.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(second.ToString(CultureInfo.InvariantCulture));
    }
}
