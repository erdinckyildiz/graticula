using System;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Graticula.Api.Wms;

/// <summary>
/// A refusal, in the shape the requested version's clients read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The exception codes are the specification's, and the wrong one is worse than
/// none.</b> A client that receives <c>InvalidCRS</c> retries in another projection;
/// one that receives a bare 400 retries nothing and reports *server unavailable*.
/// The codes here are from WMS 1.3.0 (OGC 06-042) §7.4 and the 1.1.1 equivalents.
/// </para>
/// <para>
/// <b>The status code is 200, and that is not a mistake.</b> A WMS service exception
/// is a successful HTTP response carrying an application-level refusal, which is
/// what every WMS client is written to read. Several clients treat a 4xx as a
/// transport failure and never look at the body, so the message explaining what was
/// wrong is the thing they discard. This is the same argument
/// <c>WfsFault</c> makes for OWS exception reports, and it is inherited from the
/// protocols rather than chosen.
/// </para>
/// </remarks>
/// <param name="Code">The specification's exception code, or null for a bare message.</param>
/// <param name="Message">What went wrong, in words somebody can act on.</param>
/// <param name="Locator">The parameter at fault, or null.</param>
public sealed record WmsFault(string? Code, string Message, string? Locator = null)
{
    /// <summary>The namespace a 1.3.0 exception report lives in.</summary>
    private const string Ogc = "http://www.opengis.net/ogc";

    /// <summary>A parameter is missing or unreadable.</summary>
    public const string MissingParameter = "MissingParameterValue";

    /// <summary>A parameter's value is not one this server accepts.</summary>
    public const string InvalidParameter = "InvalidParameterValue";

    /// <summary>The requested CRS is not offered for this layer.</summary>
    public const string InvalidCrs = "InvalidCRS";

    /// <summary>The requested CRS is not offered — 1.1.1's spelling.</summary>
    public const string InvalidSrs = "InvalidSRS";

    /// <summary>A named layer is not published.</summary>
    public const string LayerNotDefined = "LayerNotDefined";

    /// <summary>A named style is not defined for the layer.</summary>
    public const string StyleNotDefined = "StyleNotDefined";

    /// <summary>The layer is not queryable, but <c>GetFeatureInfo</c> asked it.</summary>
    public const string LayerNotQueryable = "LayerNotQueryable";

    /// <summary>The requested format is not one this server writes.</summary>
    public const string InvalidFormat = "InvalidFormat";

    /// <summary>The i/j or x/y pixel is outside the map.</summary>
    public const string InvalidPoint = "InvalidPoint";

    /// <summary>The requested dimension value has no data — WMS-T's own code.</summary>
    public const string InvalidDimensionValue = "InvalidDimensionValue";

    /// <summary>No version this server speaks was offered.</summary>
    public const string VersionNegotiationFailed = "VersionNegotiationFailed";

    /// <summary>An operation this server does not implement.</summary>
    public const string OperationNotSupported = "OperationNotSupported";

    /// <summary>The document, as the version's clients expect it.</summary>
    /// <param name="version">Which version's shape to write.</param>
    /// <returns>The XML.</returns>
    public string ToXml(WmsVersion version)
    {
        using Utf8Text text = new();

        XmlWriterSettings settings = new()
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        };

        using (XmlWriter writer = XmlWriter.Create(text, settings))
        {
            // <b>1.3.0 is namespaced and 1.1.1 is not</b>, and the namespace goes on
            // the start element rather than into an attribute after it. Written as
            // `WriteAttributeString("xmlns", …)` on an element already opened in no
            // namespace, `XmlWriter` refuses — *the prefix '' cannot be redefined* —
            // and **every refusal this surface produced became a 500 instead**.
            //
            // <b>Found 2026-08-20 by sending one bad TIME value at a live server</b>,
            // not by review and not by any unit test, because the writer is only
            // reached on the paths nothing was exercising. It is the reason
            // WmsConformanceTests asks for a refusal of every kind.
            if (version == WmsVersion.V130)
            {
                writer.WriteStartElement("ServiceExceptionReport", Ogc);
                writer.WriteAttributeString("version", WmsNames.Text(version));
                writer.WriteAttributeString(
                    "xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
                writer.WriteAttributeString(
                    "xsi",
                    "schemaLocation",
                    null,
                    $"{Ogc} http://schemas.opengis.net/wms/1.3.0/exceptions_1_3_0.xsd");
            }
            else
            {
                writer.WriteStartElement("ServiceExceptionReport");
                writer.WriteAttributeString("version", WmsNames.Text(version));
            }

            writer.WriteStartElement("ServiceException");

            if (Code is { Length: > 0 })
            {
                writer.WriteAttributeString("code", Code);
            }

            if (Locator is { Length: > 0 })
            {
                writer.WriteAttributeString("locator", Locator);
            }

            writer.WriteString(Message);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return text.ToString();
    }

    /// <summary>The media type this document is served as.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The media type.</returns>
    public static string MediaType(WmsVersion version) =>
        version == WmsVersion.V130
            ? WmsNames.ExceptionMediaType130
            : WmsNames.ExceptionMediaType111;

    /// <summary>A missing parameter.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The fault.</returns>
    public static WmsFault Missing(string name) =>
        new(MissingParameter,
            $"`{name}` is required and was not sent.",
            name);

    /// <summary>A parameter whose value cannot be used.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="why">Why not.</param>
    /// <returns>The fault.</returns>
    public static WmsFault Invalid(string name, string why) =>
        new(InvalidParameter, why, name);

    /// <summary>The message, for a log line.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture, $"{Code ?? "ServiceException"}: {Message}");
}
