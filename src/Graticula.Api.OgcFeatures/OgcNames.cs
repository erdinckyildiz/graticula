using System;
using System.Globalization;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// The constants OGC API Features is spelled with.
/// </summary>
/// <remarks>
/// <b>One file, because every one of these is somebody else's choice.</b> A
/// conformance class URI with a typo in it is a class the server claims and no
/// validator recognises, and it reads as *not implemented* rather than as *spelled
/// wrong* — the same reason <c>WfsNames</c> and <c>WmsNames</c> exist.
/// </remarks>
public static class OgcNames
{
    /// <summary>Where this API lives.</summary>
    /// <remarks>
    /// <para>
    /// <b>Versioned in the path, and that is a decision rather than a habit.</b> OGC
    /// API is a family whose parts version independently, and a landing page is the
    /// one URL a client is given and keeps. Putting the version in leaves room for
    /// <c>/ogc/tiles/v1</c> and <c>/ogc/styles/v1</c> beside it without either of
    /// them having to be the thing that moves.
    /// </para>
    /// <para>
    /// <b>Not at the server root.</b> The root already redirects to the REST
    /// services directory, and a server whose landing page is a different protocol
    /// depending on the year is a server nobody can bookmark.
    /// </para>
    /// </remarks>
    public const string Base = "/ogc/features/v1";

    /// <summary>The media type a feature collection is written in.</summary>
    public const string GeoJson = "application/geo+json";

    /// <summary>The media type the metadata documents are written in.</summary>
    public const string Json = "application/json";

    /// <summary>The media type an OpenAPI 3.0 definition is written in.</summary>
    public const string OpenApi = "application/vnd.oai.openapi+json;version=3.0";

    /// <summary>The media type an exception is written in — RFC 7807.</summary>
    public const string Problem = "application/problem+json";

    /// <summary>HTML, for the browsable face.</summary>
    public const string Html = "text/html";

    /// <summary>
    /// The reference system a client gets when it asks for none.
    /// </summary>
    /// <remarks>
    /// <b><c>CRS84</c> is WGS 84 in longitude/latitude order</b>, which is what
    /// GeoJSON means and the one place in OGC's protocols where the axis question
    /// does not arise. Part 2 adds the others; the default never changes.
    /// </remarks>
    public const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

    /// <summary>The conformance classes this server claims.</summary>
    /// <remarks>
    /// <para>
    /// <b>Claimed only where implemented, which is the whole point of the
    /// document.</b> A server listing a class it does not honour sends every
    /// conforming client down a path that fails, and the failure looks like a broken
    /// server rather than an untrue list.
    /// </para>
    /// <para>
    /// <b>What is absent and why:</b> Part 3's <c>filter</c> and CQL2 classes, which
    /// are a query language rather than a parameter, and Part 4's transaction
    /// classes, which this read-only surface has nothing to say about.
    /// [ADR-042](../../../docs/adr/ADR-042-ogc-api-features.md) §5.
    /// </para>
    /// </remarks>
    public static readonly string[] ConformsTo =
    [
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
        "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs",
    ];

    /// <summary>An EPSG code as the URI OGC API names it by.</summary>
    /// <remarks>
    /// <b>4326 is written as <c>EPSG/0/4326</c> and means latitude first</b>, which
    /// is why <see cref="Crs84"/> exists as a separate name for the same datum in the
    /// other order. A server that answered both with the same coordinates would be
    /// wrong for one of them, silently.
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>The URI.</returns>
    public static string CrsUri(int srid) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"http://www.opengis.net/def/crs/EPSG/0/{srid}");

    /// <summary>
    /// The EPSG code a CRS URI names, or null when it is not one this server serves.
    /// </summary>
    /// <param name="uri">The URI, as a <c>crs</c> or <c>bbox-crs</c> parameter.</param>
    /// <param name="latitudeFirst">Whether that URI means latitude before longitude.</param>
    /// <returns>The code.</returns>
    public static int? SridOf(string? uri, out bool latitudeFirst)
    {
        latitudeFirst = false;

        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string text = uri.Trim();

        if (string.Equals(text, Crs84, StringComparison.Ordinal))
        {
            return Graticula.Geometries.AxisOrder.Wgs84;
        }

        const string Epsg = "http://www.opengis.net/def/crs/EPSG/";

        if (!text.StartsWith(Epsg, StringComparison.Ordinal))
        {
            return null;
        }

        int slash = text.LastIndexOf('/');

        if (slash < 0
            || !int.TryParse(
                text[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int srid)
            || srid <= 0)
        {
            return null;
        }

        // An EPSG URI carries the authority's own axis order, which for a geographic
        // system is latitude first. CRS84 above is the same datum written the other
        // way round, and telling them apart is the whole reason both names exist.
        latitudeFirst = Graticula.Geometries.AxisOrder.IsLatitudeFirst(srid);
        return srid;
    }
}
