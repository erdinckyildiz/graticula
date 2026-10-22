using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Documents in the platform store — ADR-188, migration 89: the item, and the bytes in <c>document</c> beside it.
/// </summary>
/// <remarks>
/// <b>The bytes are read only by <see cref="ContentAsync"/>.</b> Every other read is the item and the file's name,
/// kind and size, so a listing of a thousand documents moves a thousand rows of text rather than their files.
/// </remarks>
public sealed class PostgresDocumentStore : IDocumentStore
{
    private const string Columns =
        "replace(i.id::text, '-', ''), i.title, i.snippet, i.owner_principal_id, p.name, i.sharing, d.file_name, d.kind, d.size, "
        + "d.created_at, d.modified_at, "
        + "coalesce((select array_agg(gm.group_id) from item_group gm where gm.item_id = i.id), '{}'::uuid[]), "
        + "coalesce((select array_agg(g.name order by g.name) from item_group gm join sharing_group g on g.id = gm.group_id where gm.item_id = i.id), '{}'::text[]), "
        + "i.delete_protected, i.tags, i.content_folder_id, i.description";

    private const string From =
        "from item i join document d on d.item_id = i.id join principal p on p.id = i.owner_principal_id "
        + "where i.kind = 'document'";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private NpgsqlDataSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresDocumentStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} order by d.modified_at desc, i.id");

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<Document> documents = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Read(reader) is { } document)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    /// <inheritdoc/>
    public async Task<Document?> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Columns} {From} and i.id = @id::uuid");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ContentAsync(string id, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select d.content from document d join item i on i.id = d.item_id where i.kind = 'document' and d.item_id = @id::uuid");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
    }

    /// <inheritdoc/>
    public async Task<Document> CreateAsync(
        string title,
        string? snippet,
        Guid owner,
        SharingScope sharing,
        string fileName,
        DocumentKind kind,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(content);

        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            // The item and its file in one statement, so neither exists without the other.
            "with made as (insert into item (id, kind, title, snippet, owner_principal_id, sharing) "
            + "values (@id, 'document', @title, @snippet, @owner, @sharing) returning id) "
            + "insert into document (item_id, file_name, kind, size, content) "
            + "select id, @file, @kind, @size, @content from made");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("snippet", (object?)snippet ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("sharing", PostgresAdminCatalog.Wire(sharing));
        command.Parameters.AddWithValue("file", fileName);
        command.Parameters.AddWithValue("kind", kind.Name);
        command.Parameters.AddWithValue("size", (long)content.Length);
        command.Parameters.AddWithValue("content", content);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (await FindAsync(id.ToString("N"), cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<Document?> UpdateAsync(
        string id, string title, string? snippet, SharingScope sharing, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "with touched as (update document set modified_at = now() where item_id = @id::uuid returning item_id) "
            + "update item set title = @title, snippet = @snippet, sharing = @sharing "
            + "where id in (select item_id from touched) and kind = 'document'");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("snippet", (object?)snippet ?? DBNull.Value);
        command.Parameters.AddWithValue("sharing", PostgresAdminCatalog.Wire(sharing));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? await FindAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> ReplaceAsync(
        string id, string fileName, DocumentKind kind, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(content);

        if (!WebMaps.IsId(id))
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update document set file_name = @file, kind = @kind, size = @size, content = @content, modified_at = now() "
            + "where item_id = @id::uuid");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("file", fileName);
        command.Parameters.AddWithValue("kind", kind.Name);
        command.Parameters.AddWithValue("size", (long)content.Length);
        command.Parameters.AddWithValue("content", content);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public Task<bool> SetTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return TouchAsync(id, "tags = @value", System.Linq.Enumerable.ToArray(tags), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> SetDescriptionAsync(string id, string? description, CancellationToken cancellationToken) =>
        TouchAsync(id, "description = @value", (object?)description ?? DBNull.Value, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> SetDeleteProtectedAsync(string id, bool protectedFromDeletion, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update item set delete_protected = @protected where id = @id::uuid and kind = 'document'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("protected", protectedFromDeletion);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<OwnerChange> ChangeOwnerAsync(string id, string receiver, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiver);

        if (!WebMaps.IsId(id))
        {
            return OwnerChange.NoItem;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "with taker as (select id from principal where id = graticula_principal_named(@receiver)), "
            + "moved as (update item set owner_principal_id = (select id from taker), content_folder_id = null "
            + "where id = @id::uuid and kind = 'document' and exists (select 1 from taker) returning id), "
            + "touched as (update document set modified_at = now() where item_id in (select id from moved) returning 1) "
            + "select (select count(*) from taker), (select count(*) from moved)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("receiver", receiver);

        await using NpgsqlDataReader row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await row.ReadAsync(cancellationToken).ConfigureAwait(false);

        return row.GetInt64(0) == 0 ? OwnerChange.NoMember
            : row.GetInt64(1) == 0 ? OwnerChange.NoItem
            : OwnerChange.Changed;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return false;
        }

        // The item, and the file with it by cascade.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from item where id = @id::uuid and kind = 'document'");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<bool> TouchAsync(string id, string assignment, object value, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "with touched as (update document set modified_at = now() where item_id = @id::uuid returning item_id) "
            + $"update item set {assignment} where id in (select item_id from touched) and kind = 'document'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("value", value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>A row, or null for a kind this build does not know — a later build's, read by an earlier one.</summary>
    private static Document? Read(NpgsqlDataReader reader)
    {
        if (Documents.Named(reader.GetString(7)) is not { } kind)
        {
            return null;
        }

        return new Document(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetGuid(3),
            reader.GetString(4),
            PostgresAdminCatalog.Parse(reader.GetString(5)),
            reader.GetString(6),
            kind,
            reader.GetInt64(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<Guid[]>(11),
            reader.GetFieldValue<string[]>(12),
            reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<string[]>(14),
            reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }
}
