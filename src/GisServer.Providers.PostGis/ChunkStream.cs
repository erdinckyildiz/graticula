using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace GisServer.Providers.PostGis;

/// <summary>
/// Reads a chunked attachment back as one continuous stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>One chunk is in memory at a time.</b> The rows come back in order from a
/// single sequential-access reader, and each is handed out before the next is
/// fetched — so a caller copying this to an HTTP response moves a hundred
/// megabytes through sixty-four kilobytes of memory.
/// </para>
/// <para>
/// <b>It holds a database connection for its whole life</b>, which is ADR-013
/// §4b's cost and the reason attachments have a pool of their own. Disposing the
/// handle that owns this is what gives the connection back.
/// </para>
/// </remarks>
internal sealed class ChunkStream : Stream
{
    private readonly NpgsqlConnection _connection;
    private readonly string _chunkTable;
    private readonly int _attachmentId;
    private readonly long _length;

    private NpgsqlCommand? _command;
    private NpgsqlDataReader? _reader;
    private byte[]? _chunk;
    private int _offset;
    private long _position;
    private bool _finished;

    /// <summary>Creates the stream.</summary>
    /// <param name="connection">The connection it reads through.</param>
    /// <param name="chunkTable">The qualified chunk table.</param>
    /// <param name="attachmentId">Which attachment.</param>
    /// <param name="length">Its total size, for <see cref="Length"/>.</param>
    public ChunkStream(
        NpgsqlConnection connection, string chunkTable, int attachmentId, long length)
    {
        _connection = connection;
        _chunkTable = chunkTable;
        _attachmentId = attachmentId;
        _length = length;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return 0;
        }

        if (_chunk is null || _offset == _chunk.Length)
        {
            if (!await NextChunkAsync(cancellationToken).ConfigureAwait(false))
            {
                _finished = true;
                return 0;
            }
        }

        int take = Math.Min(buffer.Length, _chunk!.Length - _offset);
        _chunk.AsMemory(_offset, take).CopyTo(buffer);
        _offset += take;
        _position += take;
        return take;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    /// <remarks>
    /// Synchronous reads block a request thread on the database, which is what
    /// Kestrel forbids on a response body for good reason. Everything that reads
    /// this uses <c>CopyToAsync</c>; anything that does not should be changed
    /// rather than accommodated.
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    private async Task<bool> NextChunkAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            _command = new NpgsqlCommand(
                $"select data from {_chunkTable} where attachmentid = @id order by seq",
                _connection);

            _command.Parameters.AddWithValue("id", _attachmentId);

            _reader = await _command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _chunk = (byte[])_reader[0];
        _offset = 0;
        return true;
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_command is not null)
        {
            await _command.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
