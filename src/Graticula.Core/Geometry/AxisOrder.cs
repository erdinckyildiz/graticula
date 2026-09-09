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
/// ~~<b>What replaces it is a range, and it is a heuristic stated as one.</b> EPSG
/// numbers its geographic 2D systems in 4000–4999… That gets 4326, 4258, 4269 and 4267
/// right — every geographic system a client of this server has asked for — and it is not
/// the general answer.~~ **Replaced 2026-09-09 by the authority's own register — Q-123,
/// [ADR-060](../../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md).**
/// </para>
/// <para>
/// <b>The heuristic was wrong about two thirds of the codes it is asked about, and the
/// sentence above understated it in a way worth keeping visible.</b> It said *the handful
/// of projected systems the authority defines northing-first*. Measured against PROJ's
/// register: **2,125 EPSG codes are north/south-first and 1,444 of them are outside
/// 4000–4999** — not a handful, and among them **every Turkish national grid**
/// (5251–5259, 5263–5264, 5269–5275) together with TUREF itself, EPSG:5252, which is
/// *geographic* and outside the block.
/// </para>
/// <para>
/// <b>Measured on a running server rather than argued.</b> One feature, one second
/// apart: <c>srsName=urn:ogc:def:crs:EPSG::4326</c> gave <c>39.979061 32.858576</c> and
/// <c>EPSG::5252</c> gave <c>32.858576 39.979061</c> — transposed, with an identical axis
/// definition, because one number is inside the block and the other is not. In the read
/// direction the same asymmetry: a WFS <c>bbox</c> in EPSG:5253's authority order matched
/// **0** features and the same box easting-first matched **2**.
/// </para>
/// <para>
/// <b>And this file's own account of why it could not be fixed had been false for
/// fifteen days.</b> It said <i>this deployment's <c>spatial_ref_sys.srtext</c> carries
/// no AXIS clauses at all, so the database cannot be asked</i>; 6,732 of 8,500 rows do
/// carry one, which Q-123 recorded on 2026-08-25 and this sentence never learned. The
/// conclusion survives for a better reason — <c>srtext</c> is a *visualisation-order*
/// rendering, so its AXIS clauses are not the authority's — and the reasoning is now
/// [ADR-060](../../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md)'s
/// rather than a comment's.
/// </para>
/// </remarks>
public static class AxisOrder
{
    /// <summary>WGS 84, the code the trap is about.</summary>
    public const int Wgs84 = 4326;

    /// <summary>Whether a CRS puts latitude or northing first when named authoritatively.</summary>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether the north-south ordinate comes first.</returns>
    /// <remarks>
    /// <b>The name says *latitude* and the answer covers northing too</b>, because the
    /// question every caller is asking is *which ordinate do I write first*. Renaming it
    /// would touch every face for no gain a reader gets; what changed is that it is now
    /// right for a projected grid as well as a geographic one.
    /// </remarks>
    public static bool IsLatitudeFirst(int srid) => AxisOrderRegister.IsNorthFirst(srid);

    /// <summary>
    /// Whether a CRS measures in degrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same block, asked a different question</b> — until 2026-09-09, when the two
    /// questions stopped sharing an answer. Scale denominators, zoom levels and the
    /// metres-per-degree conversion all need this, and three faces were each carrying
    /// their own two- or three-code list of it.
    /// </para>
    /// <para>
    /// <b>They are genuinely different questions and the old expression conflated
    /// them.</b> 307 geographic codes sit outside 4000–4999 and were being served as
    /// metres — no ±180/±90 domain, <c>esriMeters</c> reported for degrees, and a scale
    /// denominator computed with the wrong metres-per-unit. And a *geocentric* code is
    /// neither degrees nor north-first, which one expression cannot say.
    /// </para>
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether it is a geographic system.</returns>
    public static bool IsGeographic(int srid) => AxisOrderRegister.IsGeographic(srid);
}
