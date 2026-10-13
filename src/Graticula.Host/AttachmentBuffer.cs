using System;
using System.Threading;

namespace Graticula.Host;

/// <summary>
/// How many attachment bytes may be held in memory at once, so a small attachment's connection is given back before
/// a slow client reads it — owner decision 2026-10-13, ADR-013 condition 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> An attachment is streamed out of PostgreSQL in chunks, and the connection is held until the
/// last chunk is written to the client. benchmarks/attachment-pool measured what that costs: eight slow readers take
/// all eight of the attachment pool's connections, the ninth waits fifteen seconds and is answered 504, and a one
/// megabyte attachment held its connection for six of the ten seconds a slow phone took to read it. A phone's photo
/// over a phone's connection is the common case.
/// </para>
/// <para>
/// <b>Why it is bounded.</b> A buffered download stops waiting for one of the pool's eight connections, so the pool
/// no longer bounds how many are in flight. The total held is bounded here instead. Over the bound a download is not
/// refused: it streams, as every download did before this.
/// </para>
/// </remarks>
internal sealed class AttachmentBuffer
{
    /// <summary>Eight megabytes per attachment, by default.</summary>
    public const long DefaultFileBytes = 8L * 1024 * 1024;

    /// <summary>128 megabytes held at once, by default.</summary>
    public const long DefaultTotalBytes = 128L * 1024 * 1024;

    private readonly long _file;
    private readonly long _total;
    private long _held;

    /// <summary>Creates the bound.</summary>
    /// <param name="fileBytes">The largest attachment buffered; zero turns buffering off.</param>
    /// <param name="totalBytes">The most held at once across every download.</param>
    public AttachmentBuffer(long fileBytes, long totalBytes)
    {
        _file = Math.Max(0, fileBytes);
        _total = Math.Max(0, totalBytes);
    }

    /// <summary>How many bytes are held now.</summary>
    public long Held => Interlocked.Read(ref _held);

    /// <summary>
    /// Reserves room for an attachment of <paramref name="size"/> bytes, or answers null when it is too large to
    /// buffer or the total would pass the bound — in which case it streams.
    /// </summary>
    /// <param name="size">The attachment's size.</param>
    /// <returns>What gives the room back, or null.</returns>
    public IDisposable? TryReserve(long size)
    {
        if (size <= 0 || size > _file)
        {
            return null;
        }

        while (true)
        {
            long held = Interlocked.Read(ref _held);

            if (held + size > _total)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _held, held + size, held) == held)
            {
                return new Reservation(this, size);
            }
        }
    }

    private sealed class Reservation(AttachmentBuffer owner, long size) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Add(ref owner._held, -size);
            }
        }
    }
}
