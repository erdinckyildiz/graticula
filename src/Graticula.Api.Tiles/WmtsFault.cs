using System.IO;
using System.Text;
using System.Xml;

namespace Graticula.Api.Tiles;

/// <summary>
/// A WMTS refusal: an OWS Common 1.1 <c>ows:ExceptionReport</c> — WMTS 1.0.0 §8.2 (07-057r7), Table 28.
/// </summary>
/// <param name="Code">The exception code — one of the constants here.</param>
/// <param name="Locator">The parameter at fault, or null.</param>
/// <param name="Message">What is wrong, for the operator.</param>
/// <remarks>
/// <para>
/// <b>The HTTP status is the one Table 28 pairs with the code</b> — 400 for a request the client got
/// wrong (a layer or tile matrix that is not there included), 501 for an operation not offered, 500 for
/// the server's own failure — and not WMS's 200. WMTS is the OGC service that put the outcome in the status,
/// and a tile client that treats any 200 as a tile would draw an exception document as one.
/// </para>
/// <para>
/// <b>A service the caller may not see is <c>InvalidParameterValue</c> on <c>LAYER</c> with the wording
/// of one that does not exist</b> — ADR-018: absent and forbidden are the same answer, so the refusal
/// cannot be used to learn what exists.
/// </para>
/// </remarks>
public sealed record WmtsFault(string Code, string? Locator, string Message)
{
    /// <summary>OWS Common 1.1's namespace.</summary>
    public const string Ows = "http://www.opengis.net/ows/1.1";

    /// <summary>The media type of the report.</summary>
    public const string MediaType = "application/xml";

    /// <summary>A required parameter is missing — 400.</summary>
    public const string MissingParameterValue = "MissingParameterValue";

    /// <summary>A parameter has a value this server does not offer — 400.</summary>
    public const string InvalidParameterValue = "InvalidParameterValue";

    /// <summary>The operation is not offered — 501.</summary>
    public const string OperationNotSupported = "OperationNotSupported";

    /// <summary>A row or column outside the matrix — 400.</summary>
    public const string TileOutOfRange = "TileOutOfRange";

    /// <summary>No version in common — 400.</summary>
    public const string VersionNegotiationFailed = "VersionNegotiationFailed";

    /// <summary>The server failed — 500.</summary>
    public const string NoApplicableCode = "NoApplicableCode";

    /// <summary>The HTTP status this code travels with — WMTS 1.0.0 Table 28.</summary>
    public int Status => Code switch
    {
        OperationNotSupported => 501,
        NoApplicableCode => 500,
        _ => 400,
    };

    /// <summary>A missing parameter.</summary>
    /// <param name="parameter">Its name.</param>
    /// <returns>The fault.</returns>
    public static WmtsFault Missing(string parameter) =>
        new(MissingParameterValue, parameter, $"The request has no {parameter}, and this operation needs one.");

    /// <summary>A value not offered.</summary>
    /// <param name="parameter">Its name.</param>
    /// <param name="message">Why.</param>
    /// <returns>The fault.</returns>
    public static WmtsFault Invalid(string parameter, string message) => new(InvalidParameterValue, parameter, message);

    /// <summary>The report, as XML.</summary>
    /// <returns>UTF-8 bytes.</returns>
    public byte[] ToXml()
    {
        using MemoryStream stream = new();

        using (XmlWriter xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("ows", "ExceptionReport", Ows);
            xml.WriteAttributeString("version", "1.1.0");
            xml.WriteAttributeString("xml", "lang", null, "en");
            xml.WriteStartElement("ows", "Exception", Ows);
            xml.WriteAttributeString("exceptionCode", Code);

            if (Locator is { Length: > 0 })
            {
                xml.WriteAttributeString("locator", Locator);
            }

            xml.WriteElementString("ows", "ExceptionText", Ows, Message);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return stream.ToArray();
    }
}
