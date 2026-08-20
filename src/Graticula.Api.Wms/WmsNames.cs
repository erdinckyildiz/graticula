using System;
using Graticula.Cartography;

namespace Graticula.Api.Wms;

/// <summary>Which WMS a request speaks.</summary>
/// <remarks>
/// <b>Two, by owner decision 2026-08-20</b>
/// ([ADR-041](../../../docs/adr/ADR-041-the-map-renderer.md) §5.4). They differ in
/// the parameter names, the exception document and — the one that produces a map of
/// the sea off Somalia — the axis order of <c>EPSG:4326</c>.
/// </remarks>
public enum WmsVersion
{
    /// <summary>1.1.1: <c>SRS</c>, longitude first, always.</summary>
    V111 = 111,

    /// <summary>1.3.0: <c>CRS</c>, and geographic systems are latitude first.</summary>
    V130 = 130,
}

/// <summary>What a request asks for.</summary>
public enum WmsOperation
{
    /// <summary>What this server publishes.</summary>
    GetCapabilities = 1,

    /// <summary>An image.</summary>
    GetMap = 2,

    /// <summary>What is at a pixel.</summary>
    GetFeatureInfo = 3,

    /// <summary>A legend swatch.</summary>
    GetLegendGraphic = 4,
}

/// <summary>
/// The names WMS is spelled with, and the two versions' differences in one place.
/// </summary>
/// <remarks>
/// <b>One file, because every one of these is a constant somebody else chose.</b>
/// Scattering them through the writers is how a namespace ends up spelled two ways,
/// which produces a document that validates in one client and not the next — the
/// same reason <c>WfsNames</c> exists.
/// </remarks>
public static class WmsNames
{
    /// <summary>The service name every request must carry.</summary>
    public const string Service = "WMS";

    /// <summary>The WMS 1.3.0 XML namespace.</summary>
    public const string Wms = "http://www.opengis.net/wms";

    /// <summary>XLink, which capabilities uses for its own address.</summary>
    public const string Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>XML Schema instance, for schemaLocation.</summary>
    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Where the 1.3.0 schema lives.</summary>
    public const string SchemaLocation130 = "http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd";

    /// <summary>The 1.1.1 capabilities DTD, which 1.1.1 uses instead of a schema.</summary>
    public const string Dtd111 = "http://schemas.opengis.net/wms/1.1.1/WMS_MS_Capabilities.dtd";

    /// <summary>The media type a 1.3.0 exception is served as.</summary>
    public const string ExceptionMediaType130 = "text/xml";

    /// <summary>The media type a 1.1.1 exception is served as.</summary>
    public const string ExceptionMediaType111 = "application/vnd.ogc.se_xml";

    /// <summary>The media type a capabilities document is served as.</summary>
    public const string CapabilitiesMediaType130 = "text/xml";

    /// <summary>The 1.1.1 capabilities media type, which is its own.</summary>
    public const string CapabilitiesMediaType111 = "application/vnd.ogc.wms_xml";

    /// <summary>How a version is written in a document and a parameter.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The text.</returns>
    public static string Text(WmsVersion version) => version switch
    {
        WmsVersion.V111 => "1.1.1",
        WmsVersion.V130 => "1.3.0",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    /// <summary>The parameter naming the coordinate reference system.</summary>
    /// <remarks>
    /// <b>Renamed between the versions, and it is not cosmetic.</b> 1.1.1's
    /// <c>SRS</c> is always longitude first; 1.3.0's <c>CRS</c> follows the
    /// authority's own axis order. A server accepting either name for either version
    /// would be accepting a parameter whose meaning it cannot then determine.
    /// </remarks>
    /// <param name="version">The version.</param>
    /// <returns>The parameter name.</returns>
    public static string CrsParameter(WmsVersion version) =>
        version == WmsVersion.V130 ? "CRS" : "SRS";

    /// <summary>Whether a bounding box in this version and CRS is latitude first.</summary>
    /// <remarks>
    /// <b>1.1.1 is always longitude first, whatever the CRS says</b>, because 1.1.1
    /// predates the correction. 1.3.0 defers to the authority, which is what
    /// <see cref="Graticula.Geometries.AxisOrder"/> answers for both this surface
    /// and WFS.
    /// </remarks>
    /// <param name="version">The version.</param>
    /// <param name="srid">The EPSG code.</param>
    /// <returns>Whether latitude comes first.</returns>
    public static bool IsLatitudeFirst(WmsVersion version, int srid) =>
        version == WmsVersion.V130 && Graticula.Geometries.AxisOrder.IsLatitudeFirst(srid);

    /// <summary>The image format a media type names, or null when it is not one we write.</summary>
    /// <param name="mediaType">The <c>FORMAT</c> parameter.</param>
    /// <returns>The format.</returns>
    public static MapImageFormat? FormatOf(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        string value = mediaType.Trim();

        if (string.Equals(value, "image/png", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("image/png;", StringComparison.OrdinalIgnoreCase))
        {
            return MapImageFormat.Png;
        }

        return string.Equals(value, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "image/jpg", StringComparison.OrdinalIgnoreCase)
                ? MapImageFormat.Jpeg
                : null;
    }

    /// <summary>The media type for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The media type.</returns>
    public static string MediaTypeOf(MapImageFormat format) => format switch
    {
        MapImageFormat.Png => "image/png",
        MapImageFormat.Jpeg => "image/jpeg",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
