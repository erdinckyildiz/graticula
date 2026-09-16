using System;

namespace Graticula.Geometries;

/// <summary>
/// Which ordinates beyond x and y a stored geometry carries — ADR-074.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what the data has, not what a surface serves.</b> Every geometry this server
/// reads, writes, tiles and draws is two-dimensional (<see cref="Point"/> and the rest hold
/// x and y and nothing else), so a layer document's <c>hasZ</c> stays false however this
/// reads: it describes the answer a client will get, and a client that offers an elevation
/// control against a server that returns none is the over-claim ADR-008 §2 refuses. What this
/// type carries is the other half of the honest answer — <em>your table has elevations and
/// this server is not the thing that will give them back to you</em> — so that it can be said
/// once, in the same words, everywhere a person could be surprised by it.
/// </para>
/// <para>
/// <b>The numbers are PostGIS's <c>ST_Zmflag</c>, deliberately.</b> That function answers 0,
/// 1, 2 or 3 for none, M, Z and both, and the write path already reads it per row to refuse
/// overwriting an ordinate the client never saw. One numbering rather than two means the cast
/// is the conversion and there is no table to get backwards. The names are OGC's, which is
/// where <c>PointZM</c> comes from, so nothing PostgreSQL-specific appears in this tier.
/// </para>
/// </remarks>
[Flags]
public enum GeometryOrdinates
{
    /// <summary>x and y alone.</summary>
    None = 0,

    /// <summary>A measure.</summary>
    M = 1,

    /// <summary>An elevation.</summary>
    Z = 2,
}

/// <summary>
/// Reading and saying <see cref="GeometryOrdinates"/> — ADR-074.
/// </summary>
public static class Ordinates
{
    /// <summary>
    /// The sentence every surface uses for a geometry this server will not carry whole.
    /// </summary>
    /// <remarks>
    /// <b>One sentence because there were four, and they disagreed about what was happening.</b>
    /// The write path said the edit was refused, the shapefile import said the values were not
    /// stored, the geometry reader said the server stores two dimensions, and the geodatabase job
    /// counted what it flattened. All four are true and each was phrased as though it were the
    /// only place it happened. ADR-074 §4 makes this clause the shared half.
    /// </remarks>
    /// <remarks>
    /// <b>Said of the path, not the server, since ADR-077.</b> It read <i>this server reads, stores and returns
    /// two dimensions</i> until ArcGIS <c>query</c> and <c>applyEdits</c> began to carry Z and M; every place
    /// that still uses it is a path that does not.
    /// </remarks>
    public const string TwoDimensional =
        "this path reads, stores and returns x and y only";

    /// <summary>What to call them in a message.</summary>
    /// <param name="ordinates">The ones a geometry carries.</param>
    /// <returns>A noun phrase, or null when there is nothing to name.</returns>
    public static string? Name(GeometryOrdinates ordinates) => ordinates switch
    {
        GeometryOrdinates.M => "an M ordinate",
        GeometryOrdinates.Z => "a Z ordinate",
        GeometryOrdinates.Z | GeometryOrdinates.M => "Z and M ordinates",
        _ => null,
    };

    /// <summary>
    /// The ordinates an OGC type name declares — <c>PointZ</c>, <c>MultiLineStringZM</c>.
    /// </summary>
    /// <remarks>
    /// <b>The suffix is the whole rule, and the order is not free.</b> <c>ZM</c> is tested
    /// before <c>Z</c> and <c>M</c>, because <c>PointZM</c> ends in <c>M</c> and would
    /// otherwise report a measure and no elevation. A name that declares nothing — a column
    /// typed as bare geometry, a type this does not recognise — reports
    /// <see cref="GeometryOrdinates.None"/>, which is the same answer as two-dimensional and
    /// is deliberate: see <see cref="WithoutOrdinates"/>.
    /// </remarks>
    /// <param name="typeName">A geometry type name, in any case, or null.</param>
    /// <returns>What it declares.</returns>
    public static GeometryOrdinates OfTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return GeometryOrdinates.None;
        }

        string name = typeName.Trim();

        if (name.EndsWith("ZM", StringComparison.OrdinalIgnoreCase))
        {
            return GeometryOrdinates.Z | GeometryOrdinates.M;
        }

        if (name.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return GeometryOrdinates.Z;
        }

        if (name.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            return GeometryOrdinates.M;
        }

        return GeometryOrdinates.None;
    }

    /// <summary>Which ordinates beyond x and y a geometry carries, read from its first non-empty part.</summary>
    /// <remarks>
    /// <b>The first part speaks for all of them</b>: every reader here builds a geometry with one mask — a
    /// PostGIS column declares one dimensionality, and an ArcGIS geometry declares <c>hasZ</c> once.
    /// </remarks>
    /// <param name="geometry">The geometry.</param>
    /// <returns>What it carries.</returns>
    public static GeometryOrdinates Of(Geometry geometry) => geometry switch
    {
        Point point => point.Ordinates,
        LineString line => line.Coordinates.Ordinates,
        Polygon polygon => polygon.IsEmpty ? GeometryOrdinates.None : polygon.Shell.Coordinates.Ordinates,
        MultiPoint many => many.Parts.Count == 0 ? GeometryOrdinates.None : many.Parts[0].Ordinates,
        MultiLineString many => many.Parts.Count == 0 ? GeometryOrdinates.None : many.Parts[0].Coordinates.Ordinates,
        MultiPolygon many => many.Parts.Count == 0 || many.Parts[0].IsEmpty
            ? GeometryOrdinates.None
            : many.Parts[0].Shell.Coordinates.Ordinates,
        _ => GeometryOrdinates.None,
    };

    /// <summary>
    /// The same type name with its ordinate suffix removed — <c>PointZ</c> becomes <c>Point</c>.
    /// </summary>
    /// <remarks>
    /// <b>Because a three-dimensional table is publishable, and it was not.</b> The registration
    /// probe reports the column's declared type, the console hands it back to
    /// <c>POST /admin/layers</c>, and that parsed it into <see cref="GeometryKind"/> with
    /// <c>Enum.TryParse</c> — so a column declared <c>geometry(PointZ, 4326)</c> was refused with
    /// <i>geometryType 'POINTZ' is not one of: Point, MultiPoint, …</i>, which reads as
    /// <em>this is not a geometry type</em> rather than as <em>the elevation will not be served</em>.
    /// ADR-074 §4: the layer is published as the two-dimensional kind it is served as, and what
    /// happens to the elevation is said in words.
    /// </remarks>
    /// <param name="typeName">A geometry type name, or null.</param>
    /// <returns>The name without the suffix, or null when there was no name.</returns>
    public static string? WithoutOrdinates(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return typeName;
        }

        string name = typeName.Trim();

        return OfTypeName(name) switch
        {
            GeometryOrdinates.Z | GeometryOrdinates.M => name[..^2],
            GeometryOrdinates.None => name,
            _ => name[..^1],
        };
    }
}
