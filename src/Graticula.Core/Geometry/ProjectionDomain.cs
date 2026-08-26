namespace Graticula.Geometries;

/// <summary>
/// How much of the world a coordinate reference can actually represent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the same question as an area of use, and the difference is the whole point —
/// [D-164](../../../docs/architecture-debt.md), 2026-08-26.</b> An area of use says where a
/// reference is *meant* to be used; this says what it can hold at all. EPSG:3857 is used for
/// the whole world and cannot represent a latitude of 90°, because the projection sends the
/// pole to infinity. Asking it to is not a niche request: <c>bbox=-180,-90,180,90</c> is how
/// an OGC API Features client asks for everything, and it is what the conformance suite
/// sends.
/// </para>
/// <para>
/// <b>Two references are known and the rest are not, deliberately.</b> A geographic
/// reference holds ±180° and ±90° by definition, and Web Mercator's ±85.05112878° is a
/// defining property of the projection — it is the latitude that makes the map square, not a
/// number from a register. Everything else answers null, which callers must read as *no
/// clamp*, never as *nothing is representable*. The general case needs a per-reference domain
/// this server cannot look up (<see href="../../../docs/open-questions.md">Q-123</see>
/// measured why the obvious routes do not carry it), so D-164 stays open for it rather than
/// being closed by a table that would go stale silently.
/// </para>
/// <para>
/// <b>It lives in Core rather than in the endpoint that needed it first.</b> Two surfaces
/// already transform a caller's filter into a layer's reference, and a projection limit
/// written into one of them is [D-46](../../../docs/architecture-debt.md)'s shape waiting to
/// happen.
/// </para>
/// </remarks>
public static class ProjectionDomain
{
    /// <summary>
    /// The latitude beyond which Web Mercator cannot go.
    /// </summary>
    /// <remarks>
    /// <b>arctan(sinh(π)) in degrees.</b> It is where the projection's y ordinate equals half
    /// the equator's circumference, which is what makes the world square and the tile scheme
    /// work. Every web mapping stack uses the same number for the same reason.
    /// </remarks>
    public const double WebMercatorLatitude = 85.05112877980659;

    /// <summary>Web Mercator's EPSG code.</summary>
    private const int WebMercator = 3857;

    /// <summary>Esri's code for the same reference, which clients still send.</summary>
    private const int WebMercatorEsri = 102100;

    /// <summary>
    /// What a reference can represent, as longitude and latitude in WGS 84, or null when
    /// this server does not know.
    /// </summary>
    /// <param name="srid">The EPSG code of the reference being projected *into*.</param>
    /// <returns>
    /// The representable region in degrees, or null — which means *do not clamp*, not *the
    /// reference holds nothing*.
    /// </returns>
    public static Envelope? Of(int srid) => srid switch
    {
        WebMercator or WebMercatorEsri =>
            new Envelope(-180, -WebMercatorLatitude, 180, WebMercatorLatitude),

        _ when AxisOrder.IsGeographic(srid) => new Envelope(-180, -90, 180, 90),

        _ => null,
    };
}
