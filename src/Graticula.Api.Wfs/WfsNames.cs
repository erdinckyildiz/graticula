using System;
using System.Globalization;

namespace Graticula.Api.Wfs;

/// <summary>
/// The names WFS is spelled with: XML namespaces, prefixes and CRS identifiers.
/// </summary>
/// <remarks>
/// <b>One file, because every one of these is a constant somebody else chose.</b>
/// Scattering them through the writers is how a namespace ends up spelled two
/// ways, which produces a document that validates in one parser and not in the
/// next.
/// </remarks>
public static class WfsNames
{
    /// <summary>The only version this server speaks.</summary>
    /// <remarks>
    /// ADR-039 §5: 1.1.0 and 1.0.0 are refused by version negotiation rather than
    /// answered approximately. A client that receives a 2.0.0 document for a
    /// 1.1.0 request cannot tell it apart from a server that is simply wrong.
    /// </remarks>
    public const string Version = "2.0.0";

    /// <summary>The service name every request must carry.</summary>
    public const string Service = "WFS";

    /// <summary>WFS 2.0.</summary>
    public const string Wfs = "http://www.opengis.net/wfs/2.0";

    /// <summary>OWS Common 1.1, which carries capabilities and exception reports.</summary>
    public const string Ows = "http://www.opengis.net/ows/1.1";

    /// <summary>GML 3.2.</summary>
    public const string Gml = "http://www.opengis.net/gml/3.2";

    /// <summary>Filter Encoding 2.0.</summary>
    public const string Fes = "http://www.opengis.net/fes/2.0";

    /// <summary>XLink, which GML uses for references.</summary>
    public const string Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>XML Schema, for DescribeFeatureType.</summary>
    public const string Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>XML Schema instance, for schemaLocation.</summary>
    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The media type WFS 2.0 makes the default.</summary>
    public const string GmlMediaType = "application/gml+xml; version=3.2";

    /// <summary>The media type this server offers beside GML.</summary>
    public const string GeoJsonMediaType = "application/geo+json";

    /// <summary>The one prefix every feature type is published under.</summary>
    /// <remarks>
    /// <b>One namespace for the whole server, and this reversed a decision.</b>
    /// [ADR-039](../../../docs/adr/ADR-039-wfs-is-the-first-surface-after-v1.md) §5
    /// made each folder its own namespace, because <c>hosted:tr_yol</c> reads
    /// better than a flat name. Nothing in WFS requires that, and it has a cost
    /// that only the OGC conformance suite exposed: a DescribeFeatureType naming
    /// no types has to answer for every type at once, and across two namespaces
    /// that answer is a schema of <c>xsd:import</c> elements rather than a schema
    /// of declarations. The suite builds its model from exactly that response, so
    /// it found no feature types at all and 264 of its tests could not run.
    /// One namespace makes that response an ordinary schema.
    /// </remarks>
    public const string Prefix = "graticula";

    /// <summary>The XML namespace every feature type lives in.</summary>
    /// <remarks>
    /// <b>A URN rather than an http URI, deliberately.</b> The obvious choice is a
    /// URL on this server, which is what several products do — and it makes the
    /// namespace vary with the host header, so the same layer served from two
    /// deployments has two identities and a client's cached schema stops matching.
    /// A URN is stable, is ours, and is a legal namespace name. The
    /// <c>schemaLocation</c> hint still points at this server's own
    /// DescribeFeatureType, which is where a client goes to resolve it.
    /// </remarks>
    public const string Namespace = "urn:graticula:ns";

    /// <summary>A CRS as WFS 2.0 identifies one.</summary>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>The URN.</returns>
    public static string CrsUrn(int srid) =>
        "urn:ogc:def:crs:EPSG::" + srid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a CRS puts latitude first, which decides coordinate order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the trap WFS is best known for.</b> Under
    /// <c>urn:ogc:def:crs:EPSG::4326</c> the axis order is the one EPSG defines —
    /// latitude, then longitude — and a server that writes longitude first
    /// produces coordinates a conforming client silently transposes. There is no
    /// error anywhere; the features are simply in the sea off Somalia, which is
    /// the same symptom as an unknown extent and is why it is so often
    /// misdiagnosed.
    /// </para>
    /// <para>
    /// <b>What this knows is one code, and it says so rather than implying
    /// more.</b> The general answer lives in <c>spatial_ref_sys</c> — a CRS is
    /// latitude-first when its definition says so, which covers every geographic
    /// system and none of the projected ones — and reading it means a round trip
    /// per layer in a document that is meant to be cheap. 4326 is the default
    /// this surface advertises and the one nearly every client asks for, so it is
    /// the one that must be right today. Everything else is written easting
    /// first, which is correct for projected systems and wrong for any other
    /// geographic one. That gap is [Q-123](../../../docs/open-questions.md), and
    /// it is a gap rather than a guess.
    /// </para>
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether latitude comes first.</returns>
    public static bool IsLatitudeFirst(int srid) => srid == 4326;
}
