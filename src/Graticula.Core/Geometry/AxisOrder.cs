namespace Graticula.Geometries;

/// <summary>
/// Which coordinate comes first, for the protocols that care.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule, asked by every surface that needs it</b>
/// ([ADR-041](../../../docs/adr/ADR-041-the-map-renderer.md) §5.4). It lived inside
/// the WFS adapter from 2026-08-19, which was correct while there was one surface;
/// a second protocol with the same trap and its own copy of the answer is how the
/// two come to disagree, and a client cannot tell which of them is wrong.
/// </para>
/// <para>
/// <b>The trap, restated because it is the most expensive one in OGC protocols.</b>
/// <c>urn:ogc:def:crs:EPSG::4326</c> in GML and <c>CRS=EPSG:4326</c> in WMS 1.3.0
/// are latitude first. The same code in WMS 1.1.1's <c>SRS</c>, in GeoJSON
/// (RFC 7946) and in almost every tutorial is longitude first. Getting it backwards
/// raises no error anywhere: the data is simply in the sea off Somalia, which looks
/// exactly like an unknown extent and is why it is so often misdiagnosed.
/// </para>
/// <para>
/// <b>The rule is *geographic*, not *4326*, and that correction cost a gate.</b> This
/// answered true for 4326 alone until 2026-08-20. EPSG:4258 (ETRS89) — the standard
/// geographic system across most of Europe — has the identical authoritative axis
/// order and was being written longitude-first, on WFS and on WMS 1.3.0, with no
/// error anywhere: a valid 200 with every coordinate transposed. The correctness gate
/// found it by asking for one feature in both systems and reading the two answers side
/// by side.
/// </para>
/// <para>
/// <b>What replaces it is a range, and it is a heuristic stated as one.</b> EPSG
/// numbers its geographic 2D systems in 4000–4999, and the authority defines those
/// latitude-first; projected systems live elsewhere and are easting-first. That gets
/// 4326, 4258, 4269 and 4267 right — every geographic system a client of this server
/// has asked for — and it is not the general answer.
/// </para>
/// <para>
/// <b>What it still does not cover, precisely.</b> Geographic systems numbered outside
/// the block, and the handful of *projected* systems the authority defines
/// northing-first — EPSG:2180 and several Nordic grids among them — which this still
/// writes easting-first. The general answer is a per-CRS lookup, and it is not
/// available here: this deployment's <c>spatial_ref_sys.srtext</c> carries **no AXIS
/// clauses at all**, so the database cannot be asked. That is
/// [Q-123](../../../docs/open-questions.md), still open and now narrower.
/// </para>
/// </remarks>
public static class AxisOrder
{
    /// <summary>WGS 84, the code the trap is about.</summary>
    public const int Wgs84 = 4326;

    /// <summary>The first EPSG code of the geographic 2D block.</summary>
    private const int FirstGeographic = 4000;

    /// <summary>The last EPSG code of the geographic 2D block.</summary>
    private const int LastGeographic = 4999;

    /// <summary>Whether a CRS puts latitude first when named authoritatively.</summary>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether latitude comes first.</returns>
    public static bool IsLatitudeFirst(int srid) =>
        srid is >= FirstGeographic and <= LastGeographic;

    /// <summary>
    /// Whether a CRS measures in degrees.
    /// </summary>
    /// <remarks>
    /// <b>The same block, asked a different question.</b> Scale denominators, zoom
    /// levels and the metres-per-degree conversion all need this, and three faces were
    /// each carrying their own two- or three-code list of it.
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether it is a geographic system.</returns>
    public static bool IsGeographic(int srid) =>
        srid is >= FirstGeographic and <= LastGeographic;
}
