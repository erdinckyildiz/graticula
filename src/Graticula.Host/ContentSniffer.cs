using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// Decides what an uploaded file actually is, from its first bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client's <c>Content-Type</c> is not trusted</b> —
/// <see href="../../docs/security.md">security.md</see>'s upload rules and
/// ADR-013 §4d. A caller who says <c>image/png</c> and sends HTML is describing
/// what they would like the browser to do, not what they sent. What they claimed
/// is stored beside what we determined, because the difference is the
/// interesting part when something goes wrong.
/// </para>
/// <para>
/// <b>An allow-list, and everything else is <c>application/octet-stream</c>.</b>
/// A sniffer that guesses widely will eventually guess <c>text/html</c> or
/// <c>image/svg+xml</c> for something, and both execute in a browser. The list
/// holds formats that are inert when served with
/// <c>Content-Disposition: attachment</c>, which is how all of them are served
/// anyway — so the sniffed type is a label for the person reading it, never a
/// permission.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not malware scanning, and it is not a
/// guarantee about content. ADR-013 §4d leaves virus scanning open and this does
/// not close it. It answers one question — <em>what shall we call these
/// bytes</em> — and the security comes from how they are served, not from the
/// answer.
/// </para>
/// </remarks>
internal static class ContentSniffer
{
    /// <summary>How many bytes are enough to recognise anything on the list.</summary>
    /// <remarks>
    /// The longest signature checked is twelve bytes (WebP's RIFF header). The
    /// buffer is larger so the prefix can be read in one go and handed on
    /// intact.
    /// </remarks>
    public const int PrefixBytes = 64;

    /// <summary>What we serve when we do not recognise the bytes.</summary>
    public const string Unknown = "application/octet-stream";

    private static readonly (byte?[] Signature, string Type)[] Signatures =
    [
        (B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), "image/png"),
        (B(0xFF, 0xD8, 0xFF), "image/jpeg"),
        (B(0x47, 0x49, 0x46, 0x38), "image/gif"),
        (B(0x25, 0x50, 0x44, 0x46, 0x2D), "application/pdf"),
        (B(0x49, 0x49, 0x2A, 0x00), "image/tiff"),
        (B(0x4D, 0x4D, 0x00, 0x2A), "image/tiff"),

        // RIFF????WEBP — the four size bytes are whatever the file is long.
        ([0x52, 0x49, 0x46, 0x46, null, null, null, null, 0x57, 0x45, 0x42, 0x50], "image/webp"),

        // A ZIP header, which is also every Office document, every ODF document
        // and every shapefile bundle. Called what it is rather than guessed at:
        // telling them apart needs the central directory, and this server does
        // not open archives (security.md).
        (B(0x50, 0x4B, 0x03, 0x04), "application/zip"),
    ];

    /// <summary>What these bytes look like.</summary>
    /// <param name="prefix">The first bytes of the file.</param>
    /// <returns>A media type, or <see cref="Unknown"/>.</returns>
    public static string Sniff(ReadOnlySpan<byte> prefix)
    {
        foreach ((byte?[] signature, string type) in Signatures)
        {
            if (Matches(prefix, signature))
            {
                return type;
            }
        }

        return Unknown;
    }

    private static bool Matches(ReadOnlySpan<byte> prefix, byte?[] signature)
    {
        if (prefix.Length < signature.Length)
        {
            return false;
        }

        for (int i = 0; i < signature.Length; i++)
        {
            if (signature[i] is { } expected && prefix[i] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static byte?[] B(params byte[] bytes)
    {
        byte?[] signature = new byte?[bytes.Length];

        for (int i = 0; i < bytes.Length; i++)
        {
            signature[i] = bytes[i];
        }

        return signature;
    }

    /// <summary>
    /// Reads enough of a stream to sniff it, and gives back a stream that still
    /// starts at the beginning.
    /// </summary>
    /// <param name="source">The upload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What it looks like, and a stream over all of it.</returns>
    /// <remarks>
    /// <b>Only the prefix is held.</b> Sniffing needs the first bytes and the
    /// database needs all of them, and the obvious way to have both is to buffer
    /// the file — which is exactly what ADR-013 §4a forbids. The returned stream
    /// replays 64 bytes and then reads through to the original.
    /// </remarks>
    public static async Task<(string ContentType, Stream Content)> SniffAsync(
        Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        byte[] prefix = new byte[PrefixBytes];
        int read = 0;

        while (read < prefix.Length)
        {
            int got = await source
                .ReadAsync(prefix.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return (Sniff(prefix.AsSpan(0, read)), new PrefixedStream(prefix, read, source));
    }

    /// <summary>A stream that replays a prefix and then continues into another.</summary>
    private sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefixLength)
            {
                int take = Math.Min(buffer.Length, prefixLength - _offset);
                prefix.AsSpan(_offset, take).CopyTo(buffer);
                _offset += take;
                return take;
            }

            return rest.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefixLength)
            {
                int take = Math.Min(buffer.Length, prefixLength - _offset);
                prefix.AsMemory(_offset, take).CopyTo(buffer);
                _offset += take;
                return take;
            }

            return await rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
