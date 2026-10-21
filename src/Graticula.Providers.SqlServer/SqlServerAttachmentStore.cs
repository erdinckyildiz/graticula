using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>
/// A registered SQL Server layer's attachments kept beside its table — ADR-207, ADR-210 — in <c>&lt;table&gt;__attach</c>,
/// one row per attachment, with the bytes in <c>&lt;table&gt;__attach_chunk</c> a block per row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chunked, as the MySQL and PostGIS stores are</b>: a single <c>varbinary(max)</c> read back through the driver is held
/// whole unless every reader streams it, and ADR-013 §4a rules out a client-chosen size on the heap. One chunk is one row,
/// and rows arrive one at a time, so memory is a chunk whichever way the bytes go.
/// </para>
/// <para>
/// <b>The foreign keys cascade</b>, as the other stores' do: a feature deleted by any tool takes its attachments, and an
/// attachment its chunks. A missing feature is the database's refusal (error 547), said as one. The key column is given
/// the type of the layer's own, because SQL Server will not reference an <c>int</c> from a <c>bigint</c>.
/// </para>
/// </remarks>
public sealed class SqlServerAttachmentStore : IAttachmentStore, IAttachmentTables
{
    /// <summary>The suffix the attachment table carries.</summary>
    public const string Suffix = "__attach";

    /// <summary>The further suffix the chunk table carries.</summary>
    public const string ChunkSuffix = "_chunk";

    /// <summary>How many bytes go in one chunk — the MySQL store's figure, for its reason.</summary>
    public const int ChunkBytes = 256 * 1024;

    private readonly string _connectionString;
    private readonly LayerDefinition _layer;
    private readonly long _quota;

    /// <summary>Creates the store.</summary>
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="quotaBytes">How much it may store.</param>
    public SqlServerAttachmentStore(string locator, LayerDefinition layer, long quotaBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);

        _connectionString = SqlServerConnections.For(locator, pooling: true);
        _layer = layer;
        _quota = quotaBytes;
    }

    /// <summary>The attachment table's name; its chunks are in the same name with <see cref="ChunkSuffix"/>.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified => $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TableName)}";

    private string QualifiedChunks => $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TableName + ChunkSuffix)}";

    private Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqlServerConnections.OpenAsync(_connectionString, cancellationToken);

    /// <summary>Creates the tables, when they are not there — switching attachments on (ADR-207).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="SqlException">The connected user may not create them; the caller says which right.</exception>
    public async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        string idColumn = _layer.IntegerIdentityColumn ?? _layer.IdentityColumn;

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string keyType;

        await using (SqlCommand type = new(
            "select DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @c", connection))
        {
            type.Parameters.AddWithValue("@s", _layer.SchemaName);
            type.Parameters.AddWithValue("@t", _layer.TableName);
            type.Parameters.AddWithValue("@c", idColumn);

            keyType = await type.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException($"{_layer.SchemaName}.{_layer.TableName} has no column '{idColumn}'.");
        }

        if (keyType is not ("tinyint" or "smallint" or "int" or "bigint"))
        {
            throw new InvalidOperationException(
                $"{_layer.SchemaName}.{_layer.TableName}'s key '{idColumn}' is a {keyType}, and attachments are kept against an integer key.");
        }

        string attachLiteral = "N'" + Qualified.Replace("'", "''", StringComparison.Ordinal) + "'";
        string chunkLiteral = "N'" + QualifiedChunks.Replace("'", "''", StringComparison.Ordinal) + "'";

        foreach (string statement in (string[])
                 [
                     $"""
                      if object_id({attachLiteral}, 'U') is null
                      create table {Qualified} (
                        attachmentid  int identity(1, 1) primary key,
                        rel_objectid  {keyType} not null,
                        att_name      nvarchar(250) not null,
                        content_type  nvarchar(150) not null,
                        declared_type nvarchar(150) null,
                        data_size     bigint not null,
                        uploaded_at   datetime2(6) not null default sysutcdatetime(),
                        index {LayerDefinition.Quote(TableName + "_rel")} (rel_objectid),
                        foreign key (rel_objectid) references {_layer.QuotedTable} ({LayerDefinition.Quote(idColumn)}) on delete cascade
                      )
                      """,
                     $"""
                      if object_id({chunkLiteral}, 'U') is null
                      create table {QualifiedChunks} (
                        attachmentid int not null,
                        seq          int not null,
                        data         varbinary(max) not null,
                        primary key (attachmentid, seq),
                        foreign key (attachmentid) references {Qualified} (attachmentid) on delete cascade
                      )
                      """,
                 ])
        {
            await using SqlCommand command = new(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A table of that name already there may be somebody else's: the existence check would keep it, and the first
        // upload would fail on a column it lacks.
        await using SqlCommand shape = new(
            "select count(*) from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME in ('rel_objectid', 'data_size', 'att_name')",
            connection);
        shape.Parameters.AddWithValue("@s", _layer.SchemaName);
        shape.Parameters.AddWithValue("@t", TableName);

        if (Convert.ToInt32(await shape.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 3)
        {
            throw new InvalidOperationException(
                $"{_layer.SchemaName}.{TableName} is already there and is not a table this server made. Keep this layer's "
                + "attachments in the datastore instead, or rename that table.");
        }
    }

    /// <inheritdoc/>
    public async Task<(long Count, long Bytes)> CountAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new($"select count_big(*), coalesce(sum(data_size), 0) from {Qualified}", connection);

        try
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
        }
        catch (SqlException e) when (Missing(e))
        {
            return (0, 0);
        }
    }

    /// <inheritdoc/>
    public async Task DropAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new($"drop table if exists {QualifiedChunks}; drop table if exists {Qualified};", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AttachmentInfo>> ListAsync(long featureId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new(
            $"select {InfoColumns} from {Qualified} where rel_objectid = @feature order by attachmentid", connection);
        command.Parameters.AddWithValue("@feature", featureId);

        List<AttachmentInfo> found = [];

        try
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found.Add(InfoOf(reader));
            }
        }
        catch (SqlException e) when (Missing(e))
        {
            return [];
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<OpenAttachment?> OpenAsync(int attachmentId, CancellationToken cancellationToken)
    {
        SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AttachmentInfo? info = null;

            await using (SqlCommand command = new($"select {InfoColumns} from {Qualified} where attachmentid = @id", connection))
            {
                command.Parameters.AddWithValue("@id", attachmentId);

                try
                {
                    await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        info = InfoOf(reader);
                    }
                }
                catch (SqlException e) when (Missing(e))
                {
                }
            }

            if (info is null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            ChunkStream content = new(connection, QualifiedChunks, attachmentId, info.Value.Size);

            return new OpenAttachment(info.Value, content, async () =>
            {
                await content.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            });
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        int id;

        await using (SqlCommand insert = new(
            $"declare @made table (i int); "
            + $"insert into {Qualified} (rel_objectid, att_name, content_type, declared_type, data_size) output inserted.attachmentid into @made "
            + "values (@feature, @name, @type, @declared, 0); select i from @made;",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("@feature", featureId);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@type", contentType);
            insert.Parameters.AddWithValue("@declared", (object?)declaredContentType ?? DBNull.Value);

            try
            {
                id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            catch (SqlException e) when (e.Number == 547)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new AttachmentFeatureMissingException($"There is no feature {featureId} in this layer, so nothing was attached.", e);
            }
        }

        long total = await WriteChunksAsync(connection, transaction, id, content, cancellationToken).ConfigureAwait(false);

        await SizeAndCheckAsync(connection, transaction, id, total, "Nothing was stored.", cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(
        long featureId,
        int attachmentId,
        string name,
        string contentType,
        string? declaredContentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // The feature is part of the key, for the PostGIS store's reason: an attachment id is a guessable integer.
        await using (SqlCommand claim = new(
            $"""
             update {Qualified}
                set att_name = @name, content_type = @type, declared_type = @declared, data_size = 0,
                    uploaded_at = sysutcdatetime()
              where attachmentid = @id and rel_objectid = @feature
             """,
            connection,
            transaction))
        {
            claim.Parameters.AddWithValue("@name", name);
            claim.Parameters.AddWithValue("@type", contentType);
            claim.Parameters.AddWithValue("@declared", (object?)declaredContentType ?? DBNull.Value);
            claim.Parameters.AddWithValue("@id", attachmentId);
            claim.Parameters.AddWithValue("@feature", featureId);

            int changed;

            try
            {
                changed = await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException e) when (Missing(e))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (changed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (SqlCommand clear = new($"delete from {QualifiedChunks} where attachmentid = @id", connection, transaction))
        {
            clear.Parameters.AddWithValue("@id", attachmentId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long total = await WriteChunksAsync(connection, transaction, attachmentId, content, cancellationToken).ConfigureAwait(false);

        await SizeAndCheckAsync(connection, transaction, attachmentId, total, "The attachment was left as it was.", cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> DeleteAsync(IReadOnlyList<int> attachmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        if (attachmentIds.Count == 0)
        {
            return [];
        }

        const string Ids = "(select cast(j.[value] as int) from openjson(@ids) j)";

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new($"select attachmentid from {Qualified} where attachmentid in {Ids}", connection);
        command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(attachmentIds) });

        List<int> found = [];

        try
        {
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    found.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }

            if (found.Count > 0)
            {
                await using SqlCommand delete = new($"delete from {Qualified} where attachmentid in {Ids}", connection);
                delete.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(found) });
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException e) when (Missing(e))
        {
            return [];
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<AttachmentUsage> UsageAsync(CancellationToken cancellationToken) =>
        new((await CountAsync(cancellationToken).ConfigureAwait(false)).Bytes, _quota);

    private const string InfoColumns = "attachmentid, rel_objectid, att_name, content_type, declared_type, data_size, uploaded_at";

    private static AttachmentInfo InfoOf(SqlDataReader reader) => new(
        Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),

        // Written with sysutcdatetime(), so the stored moment is UTC.
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)));

    /// <summary>Error 208: the table was never made, which means no attachments rather than an error.</summary>
    private static bool Missing(SqlException e) => e.Number == 208;

    /// <summary>Writes the upload a chunk per row, through one rented buffer.</summary>
    private async Task<long> WriteChunksAsync(
        SqlConnection connection, SqlTransaction transaction, int id, Stream content, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkBytes);
        long total = 0;

        try
        {
            for (int n = 0; ; n++)
            {
                int read = 0;

                while (read < ChunkBytes)
                {
                    int got = await content.ReadAsync(buffer.AsMemory(read, ChunkBytes - read), cancellationToken).ConfigureAwait(false);

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

                await using (SqlCommand insert = new(
                    $"insert into {QualifiedChunks} (attachmentid, seq, data) values (@id, @seq, @data)", connection, transaction))
                {
                    insert.Parameters.AddWithValue("@id", id);
                    insert.Parameters.AddWithValue("@seq", n);
                    insert.Parameters.Add(new SqlParameter("@data", SqlDbType.VarBinary, -1) { Value = buffer.AsSpan(0, read).ToArray() });
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                total += read;

                if (read < ChunkBytes)
                {
                    break;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    /// <summary>Records the size and checks the quota inside the transaction, after the write — the PostGIS store's order.</summary>
    private async Task SizeAndCheckAsync(
        SqlConnection connection, SqlTransaction transaction, int id, long total, string kept, CancellationToken cancellationToken)
    {
        await using (SqlCommand size = new($"update {Qualified} set data_size = @size where attachmentid = @id", connection, transaction))
        {
            size.Parameters.AddWithValue("@size", total);
            size.Parameters.AddWithValue("@id", id);
            await size.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqlCommand sum = new($"select coalesce(sum(data_size), 0) from {Qualified}", connection, transaction);
        long used = Convert.ToInt64(await sum.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        if (used > _quota)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new AttachmentQuotaExceededException(
                $"This layer's attachments would reach {used:N0} bytes against a quota of {_quota:N0}. {kept} "
                + "Delete some attachments or raise the quota.");
        }
    }

    /// <summary>Reads a chunked attachment back as one stream, one chunk in memory at a time.</summary>
    private sealed class ChunkStream(SqlConnection connection, string chunkTable, int attachmentId, long length) : Stream
    {
        private SqlCommand? _command;
        private SqlDataReader? _reader;
        private byte[]? _chunk;
        private int _offset;
        private long _position;
        private bool _finished;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        private async Task<bool> NextChunkAsync(CancellationToken cancellationToken)
        {
            if (_reader is null)
            {
                _command = new SqlCommand($"select data from {chunkTable} where attachmentid = @id order by seq", connection);
                _command.Parameters.AddWithValue("@id", attachmentId);
                _reader = await _command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            }

            if (!await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _chunk = (byte[])_reader[0];
            _offset = 0;
            return true;
        }

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

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
