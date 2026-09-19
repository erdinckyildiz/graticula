using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Saved web maps in the platform store — ADR-079, migration 52.
/// </summary>
public sealed class PostgresWebMapStore : IWebMapStore
{
    /// <summary>The columns <see cref="Read"/> reads, without the document.</summary>
    private const string Listed =
        "m.id, m.title, m.snippet, m.owner_principal_id, p.name, m.sharing, null::text, m.created_at, m.modified_at";

    /// <summary>The columns <see cref="Read"/> reads, with the document.</summary>
    private const string Whole =
        "m.id, m.title, m.snippet, m.owner_principal_id, p.name, m.sharing, m.document::text, m.created_at, m.modified_at";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresWebMapStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The document is left behind.</b> A listing draws titles and scopes; reading every map's
    /// document to draw them would move up to a megabyte per row for nothing.
    /// </remarks>
    public async Task<IReadOnlyList<WebMap>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Listed} "
            + "from web_map m join principal p on p.id = m.owner_principal_id "
            + "order by m.modified_at desc, m.id");

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<WebMap> maps = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            maps.Add(Read(reader));
        }

        return maps;
    }

    /// <inheritdoc/>
    public async Task<WebMap?> FindAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Whole} "
            + "from web_map m join principal p on p.id = m.owner_principal_id where m.id = @id");

        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<WebMap> CreateAsync(
        string title,
        string? snippet,
        Guid owner,
        SharingScope sharing,
        string document,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(document);
        Require(sharing);

        string id = Guid.NewGuid().ToString("N");

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "insert into web_map (id, title, snippet, owner_principal_id, sharing, document) "
            + "values (@id, @title, @snippet, @owner, @sharing, @document)");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("snippet", (object?)snippet ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("sharing", PostgresAdminCatalog.Wire(sharing));
        command.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, document);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (await FindAsync(id, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<WebMap?> UpdateAsync(
        string id,
        string title,
        string? snippet,
        SharingScope sharing,
        string document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(document);
        Require(sharing);

        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update web_map set title = @title, snippet = @snippet, sharing = @sharing, "
            + "document = @document, modified_at = now() where id = @id");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("snippet", (object?)snippet ?? DBNull.Value);
        command.Parameters.AddWithValue("sharing", PostgresAdminCatalog.Wire(sharing));
        command.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, document);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return null;
        }

        return await FindAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!WebMaps.IsId(id))
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from web_map where id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void Require(SharingScope sharing)
    {
        if (!WebMaps.Allows(sharing))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sharing), sharing, "A web map is private, organisation or public (ADR-079 condition 4).");
        }
    }

    private static WebMap Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetGuid(3),
        reader.GetString(4),
        PostgresAdminCatalog.Parse(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8));
}
