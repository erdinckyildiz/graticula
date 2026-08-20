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
/// <b>What this knows is one code, and it says so rather than implying more.</b> The
/// general answer is in <c>spatial_ref_sys</c> — a CRS is latitude-first when its
/// definition says so, which covers every geographic system and no projected one —
/// and reading it costs a round trip per layer in documents meant to be cheap. 4326
/// is what nearly every client asks for and the one that has to be right today;
/// everything else is written easting first, correct for projected systems and wrong
/// for any other geographic one. That gap is
/// [Q-123](../../../docs/open-questions.md), and it is a gap rather than a guess.
/// </para>
/// </remarks>
public static class AxisOrder
{
    /// <summary>WGS 84, the code the trap is about.</summary>
    public const int Wgs84 = 4326;

    /// <summary>Whether a CRS puts latitude first when named authoritatively.</summary>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether latitude comes first.</returns>
    public static bool IsLatitudeFirst(int srid) => srid == Wgs84;
}
