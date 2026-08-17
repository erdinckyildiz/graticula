using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Attachments in a companion table beside the features, streamed both ways.
/// </summary>
/// <remarks>
/// <para>
/// <b>A companion table, which is Esri's model and the owner's decision</b>
/// (ADR-013 §4). Bytes live in the datastore with the features: transactional
/// with the edit, backed up by whatever backs up the datastore, and
/// byte-compatible with a migrated <c>__ATTACH</c> table — which is what makes
/// somebody's existing photographs come across rather than be lost.
/// </para>
/// <para>
/// <b>Nothing here materialises an attachment</b>, and getting that true on the
/// write path forced the storage shape.
/// </para>
/// <para>
/// <b>A single <c>bytea</c> parameter cannot stream.</b> The obvious
/// implementation — hand Npgsql the request stream as a parameter value — was
/// written, and it buffers the whole attachment inside the driver:
/// <c>StreamByteaConverter.GetSize</c> calls <c>CopyTo</c>, because PostgreSQL's
/// binary protocol needs a parameter's length before its bytes. A first probe
/// missed this by testing with a <c>MemoryStream</c>, which is seekable and so
/// answers <c>Length</c> without being read. A request body is neither.
/// </para>
/// <para>
/// <b>So the bytes are chunked</b>: metadata in one row, the content in fixed
/// blocks in a companion table, written and read a block at a time through a
/// pooled buffer. Total memory is one buffer regardless of attachment size,
/// which is what ADR-013 §4a asks for. What it costs is that our storage is no
/// longer one <c>bytea</c> column — and that turns out not to matter, because
/// §4c's migration case is <em>reading somebody else's</em> <c>__ATTACH</c>
/// table, which is a different query however we store ours.
/// </para>
/// </remarks>
public sealed class PostGisAttachmentStore : IAttachmentStore
{
    /// <summary>
    /// The suffix Esri uses, so a migrated table is found where it already is.
    /// </summary>
    /// <remarks>
    /// Lower case because PostgreSQL folds unquoted identifiers that way and
    /// every table this server creates is lower case. A migrated table named
    /// <c>THING__ATTACH</c> is a case-sensitivity problem for Q-16 to solve when
    /// it reads one, not a reason to name ours differently.
    /// </remarks>
    public const string Suffix = "__attach";

    /// <summary>
    /// How many bytes go in one chunk.
    /// </summary>
    /// <remarks>
    /// <b>Under the 85 KB large-object-heap threshold, on purpose.</b> The
    /// buffer is pooled so it is allocated once either way, but a rented array
    /// above the threshold would come from the LOH and stay there — and the
    /// whole reason ADR-013 §4a exists is A-037's measured GC ceiling. 64 KB
    /// also keeps a 128 MB attachment to two thousand rows, which PostgreSQL
    /// does not notice.
    /// </remarks>
    public const int ChunkBytes = 64 * 1024;

    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;
    private readonly long _quota;

    /// <summary>Creates a store over one layer's companion table.</summary>
    /// <param name="dataSource">
    /// The attachment pool — separate and bounded, per ADR-013 §4b, because a
    /// slow reader holds a connection for as long as it takes to receive the
    /// bytes.
    /// </param>
    /// <param name="layer">The layer.</param>
    /// <param name="quotaBytes">How much this layer may store.</param>
    public PostGisAttachmentStore(NpgsqlDataSource dataSource, LayerDefinition layer, long quotaBytes)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);

        _dataSource = dataSource;
        _layer = layer;
        _quota = quotaBytes;
    }

    /// <summary>The companion table's name, derived from the layer's.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified =>
        $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TableName)}";

    private string QualifiedChunks =>
        $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TableName + "_chunk")}";

    /// <summary>
    /// Creates the companion table if it is not there.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was created now.</returns>
    /// <remarks>
    /// <para>
    /// <b>Lazily, on the first upload.</b> Creating one beside every layer at
    /// publish time would put an empty table next to a thousand services that
    /// will never have an attachment, and would need DDL rights on a registered
    /// database at the moment somebody is only trying to publish a view.
    /// </para>
    /// <para>
    /// <b>The foreign key is deliberate and it cascades.</b> An attachment whose
    /// feature has been deleted is unreachable through every interface and
    /// counts against the quota forever. Esri's <c>__ATTACH</c> tables famously
    /// do not have it, and orphan cleanup is a job somebody has to remember to
    /// run.
    /// </para>
    /// </remarks>
    public async Task<bool> EnsureTableAsync(CancellationToken cancellationToken)
    {
        string sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             create table if not exists {Qualified} (
               attachmentid   integer generated always as identity primary key,
               rel_objectid   bigint      not null
                 references {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName)}
                   ({LayerDefinition.Quote(_layer.ObjectIdColumn ?? _layer.IdentityColumn)})
                   on delete cascade,
               att_name       text        not null,
               content_type   text        not null,
               declared_type  text,
               data_size      bigint      not null,
               uploaded_at    timestamptz not null default now()
             )
             """);

        string chunkSql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             create table if not exists {QualifiedChunks} (
               attachmentid integer not null
                 references {Qualified} (attachmentid) on delete cascade,
               seq          integer not null,
               data         bytea   not null,
               primary key (attachmentid, seq)
             )
             """);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand create = new(sql, connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand chunks = new(chunkSql, connection))
        {
            await chunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Every read is by feature or by id, and without this the first is a
        // sequential scan over a table of binaries.
        await using NpgsqlCommand index = new(
            $"create index if not exists {LayerDefinition.Quote(TableName + "_rel")} "
            + $"on {Qualified} (rel_objectid)",
            connection);

        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AttachmentInfo>> ListAsync(
        long featureId, CancellationToken cancellationToken)
    {
        // <b>Every column except the bytes.</b> Selecting data here would read
        // every attachment of the feature into memory to answer a question about
        // their names.
        string sql =
            $"""
             select attachmentid, rel_objectid, att_name, content_type, declared_type,
                    data_size, uploaded_at
             from {Qualified}
             where rel_objectid = @feature
             order by attachmentid
             """;

        List<AttachmentInfo> attachments = [];

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("feature", featureId);

        try
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                attachments.Add(new AttachmentInfo(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }
        catch (PostgresException e) when (e.SqlState == "42P01")
        {
            // No companion table means no attachments, which is the answer
            // rather than an error — the table is created on first upload.
            return [];
        }

        return attachments;
    }

    /// <inheritdoc/>
    public async Task<OpenAttachment?> OpenAsync(int attachmentId, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AttachmentInfo? info =
                await FindAsync(connection, attachmentId, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new OpenAttachment(
                info.Value,
                new ChunkStream(connection, QualifiedChunks, attachmentId, info.Value.Size),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>An attachment's metadata, or null.</summary>
    private async Task<AttachmentInfo?> FindAsync(
        NpgsqlConnection connection, int attachmentId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            $"""
             select attachmentid, rel_objectid, att_name, content_type, declared_type,
                    data_size, uploaded_at
             from {Qualified}
             where attachmentid = @id
             """,
            connection);

        command.Parameters.AddWithValue("id", attachmentId);

        try
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new AttachmentInfo(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetFieldValue<DateTimeOffset>(6));
        }
        catch (PostgresException e) when (e.SqlState == "42P01")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<int> AddAsync(
        long featureId,
        string name,
        string contentType,
        string? declaredContentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int id;

        await using (NpgsqlCommand insert = new(
            $"""
             insert into {Qualified}
               (rel_objectid, att_name, content_type, declared_type, data_size)
             values (@feature, @name, @type, @declared, 0)
             returning attachmentid
             """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("feature", featureId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("type", contentType);
            insert.Parameters.Add(new NpgsqlParameter("declared", NpgsqlDbType.Text)
            {
                Value = (object?)declaredContentType ?? DBNull.Value,
            });

            id = (int)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        long total = await WriteChunksAsync(connection, id, content, cancellationToken)
            .ConfigureAwait(false);

        await using (NpgsqlCommand size = new(
            $"update {Qualified} set data_size = @size where attachmentid = @id",
            connection,
            transaction))
        {
            size.Parameters.AddWithValue("size", total);
            size.Parameters.AddWithValue("id", id);
            await size.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // <b>The quota is checked inside the transaction, after the write.</b>
        // Checking before leaves a race two concurrent uploads walk straight
        // through; checking after without a transaction leaves the bytes on
        // disk. This is the only ordering that is both exact and reversible.
        long used = await SumAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (used > _quota)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new AttachmentQuotaExceededException(
                $"This layer's attachments would reach {used:N0} bytes against a quota of "
                + $"{_quota:N0}. Nothing was stored. Delete some attachments or raise the quota.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Reads the upload a block at a time and writes each block as a row.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    /// <remarks>
    /// <para>
    /// <b>One rented buffer for the whole attachment.</b> That is the point of
    /// chunking: memory is <see cref="ChunkBytes"/> whether the upload is one
    /// kilobyte or a hundred megabytes, so an attachment cannot reproduce
    /// A-037's ceiling however large the caller makes it.
    /// </para>
    /// <para>
    /// <b>Binary COPY rather than an insert per chunk.</b> The first version
    /// issued one <c>INSERT</c> per block, which is a round trip per 64 KB —
    /// measured at 8.6 seconds for a 40 MB upload, about 4.6 MB/s, which is slow
    /// enough to matter for anything photograph-heavy. COPY is one stream and
    /// keeps the same bounded memory.
    /// </para>
    /// </remarks>
    private async Task<long> WriteChunksAsync(
        NpgsqlConnection connection,
        int attachmentId,
        Stream content,
        CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkBytes);
        long total = 0;
        int seq = 0;

        try
        {
            await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
                $"copy {QualifiedChunks} (attachmentid, seq, data) from stdin (format binary)",
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                int read = 0;

                // Fill the block before writing it, so a stream that hands over
                // a few bytes at a time does not produce a row per packet.
                while (read < ChunkBytes)
                {
                    int got = await content
                        .ReadAsync(buffer.AsMemory(read, ChunkBytes - read), cancellationToken)
                        .ConfigureAwait(false);

                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                }

                if (read == 0)
                {
                    break;
                }

                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(attachmentId, NpgsqlDbType.Integer, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteAsync(seq++, NpgsqlDbType.Integer, cancellationToken)
                    .ConfigureAwait(false);

                // A slice, because the buffer is rented and usually larger than
                // asked for — writing the whole array would store the pool's
                // leftovers as part of somebody's photograph.
                await writer.WriteAsync(
                    new ReadOnlyMemory<byte>(buffer, 0, read), NpgsqlDbType.Bytea, cancellationToken)
                    .ConfigureAwait(false);

                total += read;

                if (read < ChunkBytes)
                {
                    break;
                }
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> DeleteAsync(
        IReadOnlyList<int> attachmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        if (attachmentIds.Count == 0)
        {
            return [];
        }

        List<int> removed = [];

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"delete from {Qualified} where attachmentid = any(@ids) returning attachmentid");

        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = attachmentIds,
        });

        try
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                removed.Add(reader.GetInt32(0));
            }
        }
        catch (PostgresException e) when (e.SqlState == "42P01")
        {
            return [];
        }

        return removed;
    }

    /// <inheritdoc/>
    public async Task<AttachmentUsage> UsageAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return new AttachmentUsage(
            await SumAsync(connection, null, cancellationToken).ConfigureAwait(false), _quota);
    }

    private async Task<long> SumAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        // <b>::bigint, and without it this throws.</b> sum() over bigint returns
        // numeric in PostgreSQL — because the sum of enough bigints does not fit
        // in one — so the value arrives as a decimal and the cast fails. The
        // first upload ever attempted died here.
        await using NpgsqlCommand command = new(
            $"select coalesce(sum(data_size), 0)::bigint from {Qualified}", connection, transaction);

        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (PostgresException e) when (e.SqlState == "42P01")
        {
            return 0;
        }
    }

    private static async ValueTask DisposeAllAsync(
        NpgsqlDataReader? reader, NpgsqlCommand? command, NpgsqlConnection connection)
    {
        if (reader is not null)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (command is not null)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A layer's attachment quota would have been exceeded.</summary>
public sealed class AttachmentQuotaExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AttachmentQuotaExceededException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened.</param>
    public AttachmentQuotaExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">Why.</param>
    public AttachmentQuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
