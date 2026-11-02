using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Sends a portal answer with its length, rather than chunked, when it is small enough to hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by driving the ArcGIS API for Python against ADR-256's group writes, 2026-11-02.</b> The
/// Python API reads a JSON answer two ways: with a <c>Content-Length</c> it hands an <c>error</c> to
/// its error handler, which raises the message callers match ("does not exist or is inaccessible" is
/// how <c>gis.groups.get</c> learns a group is gone and answers <c>None</c>); chunked, it raises the raw
/// error object, and nothing downstream matches it. Every portal error here was written by
/// <c>Results.Json</c> with no length, so every one of them took the second road.
/// </para>
/// <para>
/// <b>One place rather than fifty-four.</b> That many places write a portal error, and the next door
/// would add another; the length is a property of how the answer travels, not of what it says. Held up
/// to <see cref="Limit"/> bytes: a search page or an error is far below it, and an item's data, which
/// can be anybody's file, goes on streaming once it passes the limit — or untouched, when its handler
/// set a length itself.
/// </para>
/// </remarks>
internal static class PortalContentLength
{
    /// <summary>The most an answer is held for, before it is sent on as it comes.</summary>
    internal const int Limit = 1024 * 1024;

    /// <summary>Whether a request is the portal's.</summary>
    public static bool Applies(HttpRequest request) =>
        request.Path.StartsWithSegments(PortalEndpoints.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Holds the answer and writes it with its length, unless it outgrows the limit.</summary>
    public static async Task SendAsync(HttpContext context, RequestDelegate next)
    {
        Stream original = context.Response.Body;
        await using Holding holding = new(context.Response, original);
        context.Response.Body = holding;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        await holding.FinishAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private sealed class Holding(HttpResponse response, Stream original) : Stream
    {
        private MemoryStream? _held = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // A handler that set its own length is answering with a file it measured: let it through as it is.
            if (_held is not null && (response.ContentLength is not null || _held.Length + buffer.Length > Limit))
            {
                await ReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_held is not null)
            {
                _held.Write(buffer.Span);
                return;
            }

            await original.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        // A flush asks for nothing a held answer can give before it is finished; once released, it is passed on.
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _held is null ? original.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Writes what is held with its length; nothing to do if it was already released.</summary>
        public async Task FinishAsync(CancellationToken cancellationToken)
        {
            if (_held is null)
            {
                return;
            }

            if (!response.HasStarted && response.ContentLength is null)
            {
                response.ContentLength = _held.Length;
            }

            await ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            MemoryStream held = _held!;
            _held = null;
            held.Position = 0;
            await held.CopyToAsync(original, cancellationToken).ConfigureAwait(false);
            await held.DisposeAsync().ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_held is not null)
            {
                await _held.DisposeAsync().ConfigureAwait(false);
                _held = null;
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
