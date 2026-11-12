using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The SOAP calls ArcGIS Pro's federated-server connection makes to the services root — ADR-299.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a SOAP door at all, when ADR-040 chose the REST one.</b> ADR-040 found that Pro's <i>New ArcGIS Server</i>
/// connection speaks SOAP and chose not to build a SOAP catalogue, because browsing has a REST road. Sharing a map image
/// layer that references registered data has no REST road: Pro's analyzer opens the federated server's connection,
/// which asks <c>GetMessageVersion</c> at <c>/arcgis/services</c>, and without an answer it stops at analyzer error
/// 00290 — measured 2026-11-10 against dev.graticula.com with arcpy, four POSTs and nothing after them.
/// </para>
/// <para>
/// <b>Only what has been seen asked is answered.</b> The answer below is the one two real ArcGIS Servers give to the
/// same envelope (gis.blm.gov 11.5 and esriqa.aecon.com 12.0, 2026-11-10): the server's message version and the Pro
/// release it is paired with. Anything else is recorded where Pro's publishing calls are recorded, and refused.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private const string SoapEnvelopeOpen =
        "<?xml version=\"1.0\" encoding=\"utf-8\" ?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" "
        + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" "
        + "xmlns:tns=\"http://www.esri.com/schemas/ArcGIS/9.0\"><soap:Body>";

    private const string SoapEnvelopeClose = "</soap:Body></soap:Envelope>";

    /// <summary>
    /// The Pro release this server's ArcGIS surface is paired with in a SOAP answer: 12.1 is Pro 3.7, as 12.0 answers 3_6
    /// and 11.5 answers 3_5 (measured).
    /// </summary>
    internal const string ProMessageVersion = "esriArcGISProVersion3_7";

    /// <summary>Answers or records one SOAP call to the services root.</summary>
    private static async Task<IResult> ServicesSoapAsync(HttpContext context, CancellationToken cancellation)
    {
        if (!PublishingFromPro(context.RequestServices.GetRequiredService<IConfiguration>()))
        {
            return Results.NotFound();
        }

        using StreamReader reader = new(context.Request.Body);
        string body = await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
        string? operation = SoapOperation(body);

        // <b>A measuring tool, dev only</b>: while Pro's calls are recorded, an answer written to
        // <c>{capture}/soap-answers/{Operation}.xml</c> is returned as it stands — so the next call Pro makes can be
        // learned from a real server's answer without rebuilding this one. Nothing reads that directory elsewhere.
        // It is asked first, so even the two answers below can be varied while 00147 is measured.
        if (RecordingPro(context.RequestServices.GetRequiredService<IConfiguration>())
            && operation is { Length: > 0 }
            && System.IO.Path.Combine(
                    context.RequestServices.GetRequiredService<IConfiguration>()["Graticula:ProPublishCaptureDirectory"]
                    ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture"),
                    "soap-answers", operation + ".xml") is { } canned
            && File.Exists(canned))
        {
            return Results.Text(
                await File.ReadAllTextAsync(canned, cancellation).ConfigureAwait(false), "text/xml; charset=utf-8");
        }

        if (operation == "GetMessageVersion")
        {
            return Soap(
                "<tns:GetMessageVersionResponse><MessageVersion>esriArcGISVersion108</MessageVersion>"
                + $"<ProMessageVersion>{ProMessageVersion}</ProMessageVersion></tns:GetMessageVersionResponse>");
        }

        if (operation == "GetMessageFormats")
        {
            return Soap(MessageFormatsAnswer);
        }

        if (RecordingPro(context.RequestServices.GetRequiredService<IConfiguration>()))
        {
            string directory = context.RequestServices.GetRequiredService<IConfiguration>()["Graticula:ProPublishCaptureDirectory"]
                ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture");
            Directory.CreateDirectory(directory);

            // Never a token or a password into the file; the query is not written at all.
            string kept = Regex.Replace(body, "(?i)(token|password)>[^<]*<", "$1>*<");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(directory, $"soap-{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fff}-{operation ?? "none"}.xml"),
                $"Path: {context.Request.Path}\nSOAPAction: {context.Request.Headers["SOAPAction"]}\nContent-Type: {context.Request.ContentType}\n\n{kept}",
                cancellation).ConfigureAwait(false);
        }

        return Results.Text(
            SoapEnvelopeOpen + "<soap:Fault><faultcode>soap:Client</faultcode><faultstring>"
            + System.Security.SecurityElement.Escape($"'{operation ?? "(none)"}' is not answered by this server.")
            + "</faultstring></soap:Fault>" + SoapEnvelopeClose,
            "text/xml; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    /// <summary>The local name of the first element in a SOAP body, or null.</summary>
    internal static string? SoapOperation(string envelope)
    {
        Match body = Regex.Match(envelope, @"<(?:\w+:)?Body[^>]*>\s*<(?:\w+:)?(?<op>\w+)", RegexOptions.CultureInvariant);
        return body.Success ? body.Groups["op"].Value : null;
    }

    /// <summary>The message formats this server reads and writes: SOAP only.</summary>
    /// <remarks>
    /// Two real servers answer <c>esriServiceCatalogMessageFormatSoapOrBin</c> (measured 2026-11-10). This one says
    /// <c>…Soap</c>, because the binary format is not publicly documented and building it would be reproducing an
    /// undocumented internal — CLAUDE.md §5. SOAP is the one Esri publishes a WSDL for.
    /// </remarks>
    private const string MessageFormatsAnswer =
        "<tns:GetMessageFormatsResponse><MessageFormats>esriServiceCatalogMessageFormatSoap</MessageFormats>"
        + "</tns:GetMessageFormatsResponse>";

    private static IResult Soap(string inner) =>
        Results.Text(SoapEnvelopeOpen + inner + SoapEnvelopeClose, "text/xml; charset=utf-8");
}
