using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Platform.Admin;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Shared domains in the platform store's <c>field_domain</c> table — migration 55, ADR-087.
/// </summary>
/// <remarks>
/// <b>Where a domain is used is read from the layers, not kept beside it.</b> A second record of the same
/// fact is how the two would come to disagree; the overrides are the only place a field says which domain it
/// points at, so the count is a query over them.
/// </remarks>
public sealed class PostgresFieldDomainStore : IFieldDomainStore
{
    private const string Select = """
        select fd.id, fd.definition::text, fd.owner_principal_id, p.name, fd.updated_at
          from field_domain fd
          left join principal p on p.id = fd.owner_principal_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes shared domains in a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresFieldDomainStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SharedDomain>> ListAsync(CancellationToken cancellationToken) =>
        await ReadAsync($"{Select} order by lower(fd.name)", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<SharedDomain?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync($"{Select} where fd.id = @value", id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

    /// <inheritdoc/>
    public async Task<SharedDomain?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return (await ReadAsync(
                $"{Select} where lower(fd.name) = lower(@value)", name.Trim(), cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<SharedDomain?> CreateAsync(FieldDomain domain, Guid? owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            insert into field_domain (name, definition, owner_principal_id)
            values (@name, @definition::jsonb, @owner)
            on conflict ((lower(name))) do nothing
            returning id
            """);
        command.Parameters.AddWithValue("name", domain.Name.Trim());
        command.Parameters.AddWithValue("definition", Definition(domain));
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);

        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return id is Guid created ? await FindAsync(created, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(Guid id, FieldDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            update field_domain
               set name = @name, definition = @definition::jsonb, updated_at = now()
             where id = @id
               and not exists (select 1 from field_domain o where lower(o.name) = lower(@name) and o.id <> @id)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", domain.Name.Trim());
        command.Parameters.AddWithValue("definition", Definition(domain));

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two renames racing to one name: the index is what decides, and the loser is told the name is taken.
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DomainUse>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // <b>Locked first</b>, so a field saved to point at it between the count and the delete waits for this
        // transaction and then finds it gone, rather than pointing at nothing.
        await using (NpgsqlCommand lockIt = new(
            "select 1 from field_domain where id = @id for update", connection, transaction))
        {
            lockIt.Parameters.AddWithValue("id", id);
            await lockIt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        List<DomainUse> uses = await UsesAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);

        if (uses.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return uses;
        }

        await using (NpgsqlCommand delete = new("delete from field_domain where id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    private async Task<List<SharedDomain>> ReadAsync(string sql, object? value, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        List<(Guid Id, FieldDomain Domain, Guid? Owner, string? OwnerName, DateTimeOffset Updated)> rows = [];

        await using (NpgsqlCommand command = new(sql, connection))
        {
            if (value is not null)
            {
                command.Parameters.AddWithValue("value", value);
            }

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid id = reader.GetGuid(0);

                using JsonDocument definition = JsonDocument.Parse(reader.GetString(1));

                // A definition this build cannot read is left out rather than thrown, for FieldOverrideJson's
                // proportion argument: one bad row must not take the list away.
                if (FieldDomainJson.ReadDomain(definition.RootElement, out _) is not { } domain)
                {
                    continue;
                }

                rows.Add((
                    id,
                    domain.WithId(id),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        if (rows.Count == 0)
        {
            return [];
        }

        List<(Guid DomainId, DomainUse Use)> uses = await UsesWithIdAsync(
                connection, null, rows.Count == 1 ? rows[0].Id : null, cancellationToken)
            .ConfigureAwait(false);

        ILookup<Guid, (Guid DomainId, DomainUse Use)> byDomain = uses.ToLookup(u => u.DomainId);

        return rows
            .Select(r => new SharedDomain(r.Domain, r.Owner, r.OwnerName, r.Updated,
                [.. byDomain[r.Id].Select(u => u.Use)]))
            .ToList();
    }

    private static async Task<List<DomainUse>> UsesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? only, CancellationToken cancellationToken) =>
        [.. (await UsesWithIdAsync(connection, transaction, only, cancellationToken).ConfigureAwait(false))
            .Select(u => u.Use)];

    private static async Task<List<(Guid DomainId, DomainUse Use)>> UsesWithIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? only, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            $"select layer_id, layer_name, column_name, subtype, domain_id from ({UsesNamed}) u"
            + (only is null ? string.Empty : " where u.domain_id = @only")
            + " order by layer_name, column_name, subtype nulls first",
            connection,
            transaction);

        if (only is { } id)
        {
            command.Parameters.AddWithValue("only", id.ToString());
        }

        List<(Guid, DomainUse)> read = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(4), out Guid domainId))
            {
                continue;
            }

            read.Add((domainId, new DomainUse(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3))));
        }

        return read;
    }

    private const string UsesNamed = """
        select l.id as layer_id, l.name as layer_name, e ->> 'column' as column_name,
               null::bigint as subtype, e -> 'domain' ->> 'id' as domain_id
          from layer l, jsonb_array_elements(l.field_overrides) e
         where e -> 'domain' ->> 'id' is not null
        union all
        select l.id, l.name, d.key, (t ->> 'code')::bigint, d.value ->> 'id'
          from layer l,
               jsonb_array_elements(l.field_overrides) e,
               jsonb_array_elements(case when jsonb_typeof(e #> '{subtypes,types}') = 'array'
                                         then e #> '{subtypes,types}' else '[]'::jsonb end) t,
               jsonb_each(case when jsonb_typeof(t -> 'domains') = 'object'
                               then t -> 'domains' else '{}'::jsonb end) d
         where d.value ->> 'id' is not null
        """;

    private static string Definition(FieldDomain domain) =>
        JsonSerializer.Serialize(FieldDomainJson.Write(domain.Named(domain.Name.Trim()), byReference: false)
            .Where(kv => kv.Key != "id")
            .ToDictionary(kv => kv.Key, kv => kv.Value));
}
