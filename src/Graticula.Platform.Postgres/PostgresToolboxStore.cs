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
/// Toolboxes in the platform store — ADR-223, migration 112: the item, and the source and tools in <c>toolbox</c> beside
/// it. The source is read only by <see cref="SourceAsync"/>, so a listing moves names and parameter lists, not code.
/// </summary>
public sealed class PostgresToolboxStore : IToolboxStore
{
    private const string Columns =
        "replace(i.id::text, '-', ''), t.name, i.title, i.owner_principal_id, p.name, i.sharing, t.file_name, t.tools::text, "
        + "t.created_at, coalesce((select array_agg(gm.group_id) from item_group gm where gm.item_id = i.id), '{}'::uuid[])";

    private const string From =
        "from item i join toolbox t on t.item_id = i.id join principal p on p.id = i.owner_principal_id where i.kind = 'toolbox'";

    private readonly NpgsqlDataSource _root;

    private NpgsqlDataSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Over the platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresToolboxStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Toolbox>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Columns} {From} order by lower(t.name)");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<Toolbox> found = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<Toolbox?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Columns} {From} and lower(t.name) = lower(@name)");
        command.Parameters.AddWithValue("name", name);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<string?> SourceAsync(string id, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand("select source from toolbox where item_id = @id::uuid");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <inheritdoc/>
    public async Task<Toolbox> CreateAsync(
        string name, string title, Guid owner, string fileName, string source, string tools, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Guid id = Guid.NewGuid();

        // ADR-223 §5.7 (INFERRED d): private to start, as every published item here is.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "with made as (insert into item (id, kind, title, owner_principal_id, sharing) "
            + "values (@id, 'toolbox', @title, @owner, 'private') returning id) "
            + "insert into toolbox (item_id, name, file_name, source, tools) select id, @name, @file, @source, @tools from made");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("file", fileName);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("tools", NpgsqlDbType.Jsonb, tools);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (await FindByNameAsync(name, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<bool> ShareAsync(string id, SharingScope sharing, CancellationToken cancellationToken)
    {
        if (!WebMaps.IsId(id))
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update item set sharing = @sharing where id = @id::uuid and kind = 'toolbox'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("sharing", PostgresAdminCatalog.Wire(sharing));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static Toolbox Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetGuid(3),
        reader.GetString(4),
        PostgresAdminCatalog.Parse(reader.GetString(5)),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<Guid[]>(9));
}
