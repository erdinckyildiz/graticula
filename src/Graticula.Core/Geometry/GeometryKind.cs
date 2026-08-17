namespace Graticula.Geometries;

/// <summary>
/// The geometry types this server models, using OGC Simple Features names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Simple Features rather than ArcGIS names</b>, given that v1's API is
/// ArcGIS (<c>docs/v1-scope.md</c>): Esri's model is lossier. A <c>Polyline</c>
/// conflates <see cref="LineString"/> and <see cref="MultiLineString"/>, and a
/// <c>Polygon</c> is a flat bag of rings where winding order carries the part
/// structure that Simple Features makes explicit.
/// </para>
/// <para>
/// Converting Simple Features to ArcGIS is mechanical. Converting back requires
/// classifying rings into shells and holes. PostGIS is the only provider (Q-88)
/// and OGC API Features arrives in v2, so a domain speaking Esri's dialect would
/// make both storage <em>and</em> the future API into translation layers instead
/// of one. Review finding A10 — the compatibility layer becoming the product —
/// is a reason to keep that dialect at the edge.
/// </para>
/// <para>
/// The tag exists so hot loops can branch without a type check.
/// </para>
/// </remarks>
public enum GeometryKind
{
    /// <summary>A single position.</summary>
    Point = 1,

    /// <summary>An ordered set of positions.</summary>
    MultiPoint = 2,

    /// <summary>A connected sequence of segments.</summary>
    LineString = 3,

    /// <summary>One or more <see cref="LineString"/> parts.</summary>
    MultiLineString = 4,

    /// <summary>An area: one exterior ring and zero or more interior rings.</summary>
    Polygon = 5,

    /// <summary>One or more <see cref="Polygon"/> parts.</summary>
    MultiPolygon = 6,
}
