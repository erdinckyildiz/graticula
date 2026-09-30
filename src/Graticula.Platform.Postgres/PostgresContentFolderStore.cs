using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>Content folders in the platform store — ADR-114, migration 72.</summary>
public sealed class PostgresContentFolderStore : IContentFolderStore
{
    private const string Counted = """
        f.id, f.owner_principal_id, f.title, f.created_at,
        (select count(*) from service s where s.content_folder_id = f.id)
          + (select count(*) from web_map m where m.content_folder_id = f.id)
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresContentFolderStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ContentFolder>> ListAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Counted} from content_folder f where f.owner_principal_id = @owner order by lower(f.title)");
        command.Parameters.AddWithValue("owner", owner);

        List<ContentFolder> folders = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(Read(reader));
        }

        return folders;
    }

    /// <inheritdoc/>
    public async Task<ContentFolder?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Counted} from content_folder f where f.id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<ContentFolder?> CreateAsync(Guid owner, string title, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            insert into content_folder (id, owner_principal_id, title) values (@id, @owner, @title)
            on conflict do nothing
            returning id, owner_principal_id, title, created_at, 0
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("title", title);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<ContentFolderWrite> RenameAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("update content_folder set title = @title where id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
                ? ContentFolderWrite.Done
                : ContentFolderWrite.Missing;
        }
        catch (PostgresException taken) when (taken.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ContentFolderWrite.Taken;
        }
    }

    /// <inheritdoc/>
    public async Task<ContentFolderWrite> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from content_folder where id = @id");
        command.Parameters.AddWithValue("id", id);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
                ? ContentFolderWrite.Done
                : ContentFolderWrite.Missing;
        }
        catch (PostgresException held) when (held.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // `on delete restrict` is the rule, and the refusal is its sentence (ADR-114 §5.2).
            return ContentFolderWrite.NotEmpty;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> MoveServiceAsync(Guid serviceId, Guid? folder, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update service set content_folder_id = @folder where id = @id");
        command.Parameters.AddWithValue("id", serviceId);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> MoveMapAsync(string mapId, Guid? folder, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update web_map set content_folder_id = @folder where id = @id");
        command.Parameters.AddWithValue("id", mapId);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static ContentFolder Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        (int)reader.GetInt64(4));
}
