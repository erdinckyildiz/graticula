using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// How a document's bytes leave this server, for Studio and for the portal alike — ADR-188 §5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always a download, never a page.</b> A document is bytes somebody else chose, served from this server's own
/// origin to everybody it is shared with; rendered here, an HTML file that slipped past recognition would run as this
/// site. So the four headers are written on every answer, whatever the kind: <c>attachment</c>, the media type this
/// server decided rather than the one the uploader sent, <c>nosniff</c> so a browser does not decide differently, and
/// a sandbox so that even a browser that renders it anyway runs nothing.
/// </para>
/// <para>
/// <b>One method, because two callers.</b> The headers are the security of this feature, and a second copy of them on
/// the portal face is how one face would come to lack one.
/// </para>
/// </remarks>
internal static class DocumentDownload
{
    /// <summary>Writes a document's bytes as a download.</summary>
    /// <param name="context">The request.</param>
    /// <param name="document">The document.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        HttpContext context, Document document, byte[] content, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(content);

        HttpResponse response = context.Response;

        ContentDispositionHeaderValue disposition = new("attachment");
        disposition.SetHttpFileName(document.FileName);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = document.Kind.MediaType;
        response.ContentLength = content.Length;
        response.Headers.ContentDisposition = disposition.ToString();
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";

        // The item's sharing decides who may read it, and it can change; a shared cache must not keep it.
        response.Headers.CacheControl = "private, no-cache";

        await response.Body.WriteAsync(content, cancellation).ConfigureAwait(false);
    }

    /// <summary>The same download as an <see cref="IResult"/>, for a handler that answers with one.</summary>
    /// <param name="document">The document.</param>
    /// <param name="content">Its bytes.</param>
    /// <returns>The result.</returns>
    public static IResult Result(Document document, byte[] content) => new Download(document, content);

    private sealed class Download(Document document, byte[] content) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            WriteAsync(httpContext, document, content, httpContext.RequestAborted);
    }
}
