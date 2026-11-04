using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// A PDF document's first page as its picture, when its owner gave it none — ADR-268, D-321.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawn in the import reader's process, never here</b> (ADR-009 §2.2): PDFium parses a stranger's file, and the
/// reader is the process the host kills and counts when a parser fails. This class only hands the file over and keeps
/// the PNG that comes back.
/// </para>
/// <para>
/// <b>A picture is never the reason an upload fails.</b> No reader, a password, a damaged file, a deadline passed: the
/// document is kept as it was sent and has no picture, as it had none before ADR-268, and the reason goes to the log.
/// </para>
/// <para>
/// <b>An owner's picture wins.</b> <see cref="IDocumentStore.SetDrawnThumbnailAsync"/> writes only an empty slot or one
/// this server drew, so an upload, a replacement or a portal <c>update</c> never takes away what an owner chose.
/// </para>
/// </remarks>
internal static partial class DocumentPictures
{
    /// <summary>The width a first page is drawn at — ArcGIS's own thumbnail width, and its height at most twice that.</summary>
    public const int Width = 600;

    /// <summary>How long the reader has to draw one page; PDFium takes milliseconds, so this only bounds a hostile file.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Draws <paramref name="document"/>'s first page when it is a PDF, or removes a picture this server drew for an
    /// earlier file when it no longer is one.
    /// </summary>
    /// <param name="services">The request's services.</param>
    /// <param name="document">The document as kept.</param>
    /// <param name="content">Its file.</param>
    /// <param name="cancellation">Cancellation.</param>
    public static async Task DrawAsync(IServiceProvider services, Document document, byte[] content, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(content);

        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        ILogger log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DocumentPictures));

        if (!string.Equals(document.Kind.Name, "pdf", StringComparison.Ordinal))
        {
            await documents.SetDrawnThumbnailAsync(document.Id, null, cancellation).ConfigureAwait(false);
            return;
        }

        GeodatabaseReader reader = services.GetRequiredService<GeodatabaseReader>();

        if (!reader.Available)
        {
            LogNotDrawn(log, document.Id, "the import reader is not installed beside the server");
            return;
        }

        string path = Path.Combine(services.GetRequiredService<ImportScratch>().Directory, $"{Guid.NewGuid():N}.pdf");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, cancellation).ConfigureAwait(false);

            using JsonDocument answer = await reader.AskAsync(
                new { op = "pdfpage", path, width = Width, maximumBytes = (long)content.Length },
                Deadline, cancellation).ConfigureAwait(false);
            JsonElement root = answer.RootElement;

            if (!root.TryGetProperty("png", out JsonElement png) || png.GetString() is not { Length: > 0 } encoded)
            {
                string why = root.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? "no picture" : "no picture";
                LogNotDrawn(log, document.Id, why);
                return;
            }

            byte[] picture = Convert.FromBase64String(encoded);

            if (picture.Length > Documents.MaximumThumbnailBytes)
            {
                LogTooLarge(log, document.Id, picture.Length);
                return;
            }

            await documents.SetDrawnThumbnailAsync(document.Id, picture, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException
            or JsonException or FormatException or TimeoutException)
        {
            LogNotDrawn(log, document.Id, e.Message);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The scratch sweep takes a file a reader still held.
            }
        }
    }

    [LoggerMessage(EventId = 2610, Level = LogLevel.Information, Message = "Document {Document} was given no picture of its first page: {Why}")]
    private static partial void LogNotDrawn(ILogger log, string document, string why);

    [LoggerMessage(EventId = 2611, Level = LogLevel.Information, Message = "Document {Document} was given no picture of its first page: it drew to {Bytes} bytes, above an item picture's limit")]
    private static partial void LogTooLarge(ILogger log, string document, int bytes);
}
