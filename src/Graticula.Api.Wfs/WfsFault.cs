using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Graticula.Api.Wfs;

/// <summary>The exception codes OWS Common defines that this surface uses.</summary>
public enum WfsFaultCode
{
    /// <summary>A parameter was given a value the server cannot honour.</summary>
    InvalidParameterValue,

    /// <summary>A parameter the operation requires was not given.</summary>
    MissingParameterValue,

    /// <summary>The operation itself is not one this server offers.</summary>
    OperationNotSupported,

    /// <summary>No version in common. See <see cref="WfsNames.Version"/>.</summary>
    VersionNegotiationFailed,

    /// <summary>The request was understood and could not be carried out.</summary>
    OperationProcessingFailed,
}

/// <summary>
/// A refusal, in the shape OWS Common gives one.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal is a document, not a status code.</b> A WFS client reads
/// <c>ows:ExceptionReport</c> and shows the operator what is wrong with their
/// request; an HTTP 400 with a JSON body is invisible to it. So every path out
/// of this surface that is not a result is one of these.
/// </para>
/// <para>
/// <b>The status code is 400 for all of them, including the ones that are the
/// server's fault.</b> OWS says the report is returned with a 4xx or 5xx, and a
/// client that treats 5xx as *retry later* would hammer a server refusing a
/// malformed filter. What distinguishes them is the code inside, which is what a
/// client can act on.
/// </para>
/// </remarks>
/// <param name="Code">Which kind of refusal.</param>
/// <param name="Locator">The parameter at fault, or null.</param>
/// <param name="Text">What is wrong, in words an operator can act on.</param>
public sealed record WfsFault(WfsFaultCode Code, string? Locator, string Text)
{
    /// <summary>A missing parameter.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The fault.</returns>
    public static WfsFault Missing(string name) => new(
        WfsFaultCode.MissingParameterValue,
        name,
        $"The '{name}' parameter is required and was not supplied.");

    /// <summary>A parameter this server cannot honour.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="why">What is wrong with the value.</param>
    /// <returns>The fault.</returns>
    public static WfsFault Invalid(string name, string why) => new(
        WfsFaultCode.InvalidParameterValue, name, why);

    /// <summary>Writes the report.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(Stream stream, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("ows", "ExceptionReport", WfsNames.Ows)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "version", null, WfsNames.Version)
                .ConfigureAwait(false);

            await xml.WriteStartElementAsync("ows", "Exception", WfsNames.Ows)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "exceptionCode", null, Code.ToString())
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(Locator))
            {
                await xml.WriteAttributeStringAsync(null, "locator", null, Locator)
                    .ConfigureAwait(false);
            }

            await xml.WriteElementStringAsync("ows", "ExceptionText", WfsNames.Ows, Text)
                .ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }
}
