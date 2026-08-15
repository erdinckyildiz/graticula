using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Admin;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Secrets;
using Npgsql;
using NpgsqlTypes;

namespace GisServer.Platform.Postgres;

/// <summary><see cref="IAdminCatalog"/> over the platform store.</summary>
public sealed class PostgresAdminCatalog : IAdminCatalog
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SecretProtector _secrets;

    /// <summary>Creates the catalogue.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    /// <param name="secrets">Seals and opens data source credentials.</param>
    public PostgresAdminCatalog(NpgsqlDataSource dataSource, SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(secrets);

        _dataSource = dataSource;
        _secrets = secrets;
    }

    /// <inheritdoc/>
    public async Task<Guid> RegisterDataSourceAsync(
        string name, string kind, string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Guid id = Guid.NewGuid();

        // Sealed here, inside the process, with the key the server already
        // holds. This is the whole point of D-09: before the admin API existed,
        // registering meant an operator running AES-GCM outside the server and
        // pasting hex into an insert — and getting the port wrong in that step
        // surfaced as an authentication failure at query time, days later.
        const string Sql = """
            insert into data_source (id, name, kind, connection_secret, key_version)
            values (@id, @name, @kind, @secret, @version)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue(
            "secret", NpgsqlDbType.Bytea, _secrets.Protect(connectionString));
        command.Parameters.AddWithValue("version", _secrets.KeyVersion);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RegisteredDataSource>> ListDataSourcesAsync(
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select d.id, d.name, d.kind, count(l.id)
            from data_source d
            left join layer l on l.data_source_id = d.id
            group by d.id, d.name, d.kind
            order by d.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<RegisteredDataSource> sources = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new RegisteredDataSource(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),

                // Summary is filled by the caller, which holds the protector.
                // Decrypting every credential to render a list would be a lot of
                // key use for a cosmetic column.
                string.Empty,
                (int)reader.GetInt64(3)));
        }

        return sources;
    }

    /// <inheritdoc/>
    public async Task<string?> ConnectionStringOfAsync(
        Guid dataSourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select connection_secret, key_version from data_source where id = @id");
        command.Parameters.AddWithValue("id", dataSourceId);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return _secrets.Unprotect(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1));
    }

    /// <inheritdoc/>
    public async Task<PublishedLayerAddress> PublishLayerAsync(
        LayerPublication publication, Guid owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);

        Guid id = Guid.NewGuid();

        // <b>One statement, because the service, its index and the layer must
        // arrive together or not at all.</b> Two round trips would leave a
        // window where a second publisher reads the same max(layer_index) and
        // both write layer 1 — the unique constraint on (service_id,
        // layer_index) would then reject one of them <em>after</em> its service
        // row existed. The CTE makes the whole thing one transaction with no
        // application-side read to race on.
        //
        // <b>An existing service keeps its own sharing.</b> The scope belongs to
        // the container (ADR-018 §3b-i), so adding a layer to a public service
        // must not quietly reset it to whatever this request asked for — that
        // would be a privilege change disguised as a publish.
        const string Sql = """
            with folder as (
                select case when d.is_datastore then 'hosted' else null end as name
                from data_source d where d.id = @source
            ),
            existing as (
                select s.id from service s, folder f
                where lower(s.name) = lower(@service)
                  and coalesce(lower(s.folder), '') = coalesce(lower(f.name), '')
            ),
            created as (
                -- next_layer_index starts at 1 because this statement is about
                -- to put a layer at 0. See `slot` for why it cannot be bumped
                -- afterwards.
                insert into service
                    (id, name, folder, kind, owner_principal_id, sharing, next_layer_index)
                select gen_random_uuid(), @service, f.name, 'FeatureServer', @owner, @sharing, 1
                from folder f
                where not exists (select 1 from existing)
                returning id, 0 as layer_index
            ),
            bumped as (
                -- <b>The index comes from a counter on the service row, not from
                -- max(index) + 1.</b> Group layers and feature layers live in
                -- different tables and cannot share a unique constraint, so a
                -- maximum computed across both races: two concurrent publishes
                -- read the same number and both succeed, and /FeatureServer/3
                -- becomes ambiguous. This update takes the service's row lock,
                -- which serialises allocation — and the counter never goes
                -- backwards, so an index freed by a removal is never handed out
                -- again to something new.
                update service
                set next_layer_index = next_layer_index + 1, updated_at = now()
                where id = (select id from existing)
                returning id, next_layer_index - 1 as layer_index
            ),
            slot as (
                -- <b>Two branches, because one statement cannot update a row it
                -- just inserted.</b> Every data-modifying CTE sees the same
                -- snapshot, so an `update service` that targeted the row
                -- `created` had produced matched nothing — and the layer insert,
                -- selecting from an empty slot, inserted nothing. The result was
                -- a service created with no layers and a 201 saying it worked,
                -- which is exactly what publishing into a brand-new service did
                -- from the moment the counter was introduced. It went unnoticed
                -- because every test afterwards published into a service that
                -- already existed.
                select id, layer_index from bumped
                union all
                select id, layer_index from created
            )
            insert into layer
              (id, data_source_id, name, schema_name, table_name, geometry_column,
               identity_column, object_id_column, srid, geometry_type, is_hosted,
               owner_principal_id, sharing, service_id, layer_index, parent_layer_index,
               cache_seconds)
            select
               @id, @source, @name, @schema, @table, @geometry,
               @identity, @objectid, @srid, @type, false,
               @owner, @sharing, slot.id, slot.layer_index, @parent, @cache
            from slot
            returning layer_index
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("service", publication.ServiceName ?? publication.Name);
        command.Parameters.AddWithValue(
            "parent", (object?)publication.ParentLayerIndex ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "cache", (object?)publication.CacheSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("source", publication.DataSourceId);
        command.Parameters.AddWithValue("name", publication.Name);
        command.Parameters.AddWithValue("schema", publication.SchemaName);
        command.Parameters.AddWithValue("table", publication.TableName);
        command.Parameters.AddWithValue("geometry", publication.GeometryColumn);
        command.Parameters.AddWithValue("identity", publication.IdentityColumn);
        command.Parameters.AddWithValue("srid", publication.Srid);
        command.Parameters.AddWithValue("type", publication.GeometryType.ToString());
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("sharing", Wire(publication.Sharing));
        command.Parameters.Add(new NpgsqlParameter("objectid", NpgsqlDbType.Text)
        {
            Value = (object?)publication.ObjectIdColumn ?? DBNull.Value,
        });

        object? index = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return new PublishedLayerAddress(
            id,
            publication.ServiceName ?? publication.Name,
            index is int at ? at : 0);
    }

    /// <inheritdoc/>
    public async Task<bool> SetCacheLifetimeAsync(
        string name, int? seconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update layer set cache_seconds = @seconds, updated_at = now() where name = @name");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("seconds", (object?)seconds ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<Guid?> CreateServiceAsync(
        string name,
        string? folder,
        string? description,
        SharingScope sharing,
        Guid owner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Guid id = Guid.NewGuid();

        // on conflict do nothing against the (folder, lower(name)) index, so a
        // repeated create is a 409 rather than a second service that nobody can
        // address unambiguously.
        const string Sql = """
            insert into service (id, name, folder, kind, description, owner_principal_id, sharing)
            values (@id, @name, @folder, 'FeatureServer', @description, @owner, @sharing)
            on conflict do nothing
            returning id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("sharing", Wire(sharing));

        object? created = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return created is Guid made ? made : null;
    }

    /// <inheritdoc/>
    public async Task<GroupLayerAddress?> CreateGroupLayerAsync(
        string? folder,
        string serviceName,
        string name,
        int? parentLayerIndex,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Guid id = Guid.NewGuid();

        // Same counter, same row lock, same guarantee as publishing a layer:
        // one index, allocated once, never reused.
        const string Sql = """
            with target as (
                select id from service
                where lower(name) = lower(@service)
                  and coalesce(lower(folder), '') = coalesce(lower(@folder), '')
            ),
            slot as (
                update service
                set next_layer_index = next_layer_index + 1, updated_at = now()
                where id = (select id from target)
                returning id, next_layer_index - 1 as layer_index
            )
            insert into group_layer (id, service_id, layer_index, name, parent_layer_index)
            select @id, slot.id, slot.layer_index, @name, @parent
            from slot
            returning layer_index
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("service", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("parent", (object?)parentLayerIndex ?? DBNull.Value);

        object? index = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // No rows means no such service. The foreign key covers a parent that is
        // not a group; this covers a service that is not there at all.
        return index is int at ? new GroupLayerAddress(id, at) : null;
    }

    /// <inheritdoc/>
    public async Task<Guid> EnsureDatastoreAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // The partial unique index on (true) where is_datastore is what makes
        // the conflict target work: at most one row can ever carry the flag, so
        // "insert the datastore" and "update the datastore" are the same
        // statement and cannot race into two.
        const string Sql = """
            insert into data_source (id, name, kind, connection_secret, key_version, is_datastore)
            values (@id, @name, 'postgis', @secret, @version, true)
            on conflict ((true)) where is_datastore do update
              set connection_secret = excluded.connection_secret,
                  key_version       = excluded.key_version,
                  updated_at        = now()
            returning id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("name", DatastoreName);
        command.Parameters.AddWithValue(
            "secret", NpgsqlDbType.Bytea, _secrets.Protect(connectionString));
        command.Parameters.AddWithValue("version", _secrets.KeyVersion);

        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The reserved name of the datastore source.
    /// </summary>
    /// <remarks>
    /// Reserved so an administrator cannot register an unrelated database under
    /// the same name and have it appear, in every listing, to be the datastore.
    /// </remarks>
    public const string DatastoreName = "datastore";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminLayer>> ListLayersAsync(CancellationToken cancellationToken)
    {
        const string Sql = """
            select l.id, l.name, d.name, l.schema_name, l.table_name, l.sharing,
                   l.owner_principal_id, p.name, l.object_id_column, l.status,
                   d.is_datastore
            from layer l
            join data_source d on d.id = l.data_source_id
            left join principal p on p.id = l.owner_principal_id
            order by l.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<AdminLayer> layers = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            layers.Add(new AdminLayer(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                $"{reader.GetString(3)}.{reader.GetString(4)}",
                Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(8),
                ParseStatus(reader.GetString(9)),
                reader.GetBoolean(10)));
        }

        return layers;
    }

    /// <inheritdoc/>
    public async Task<AdminLayer?> SetSharingAsync(
        string layerName, SharingScope sharing, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // Returns the row as it was. ADR-017 §5d wants before and after in the
        // audit record, and the only moment the "before" is knowable is inside
        // the statement that replaces it.
        const string Sql = """
            update layer set sharing = @sharing
            where name = @name
            returning id, name, sharing, owner_principal_id, object_id_column, schema_name, table_name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", layerName);
        command.Parameters.AddWithValue("sharing", Wire(sharing));

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The scope reported here is the NEW one, because `returning` in an
        // update yields the updated row. The caller supplied the new value and
        // knows the old only if it read first — which is why the endpoint reads
        // the list before calling this, and why that is written down rather than
        // left as a puzzle.
        return new AdminLayer(
            reader.GetGuid(0),
            reader.GetString(1),
            string.Empty,
            $"{reader.GetString(5)}.{reader.GetString(6)}",
            Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            null,
            !reader.IsDBNull(4),
            ServiceStatus.Started,

            // Not read back by this statement and not used by its caller, which
            // only reports the new sharing scope. False rather than a guess: a
            // value invented here would be indistinguishable from a real one.
            Hosted: false);
    }

    /// <inheritdoc/>
    public async Task<bool> UnpublishLayerAsync(
        string layerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // Only the registration. The table in the customer's database is not
        // ours to drop, and a delete endpoint that removed data would be the
        // single most dangerous thing in this API.
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("delete from layer where name = @name");
        command.Parameters.AddWithValue("name", layerName);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<ServiceStatus?> SetStatusAsync(
        string layerName, ServiceStatus status, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // A CTE captures the old value before the update overwrites it, so the
        // audit record can say what changed rather than only what it is now —
        // which anybody can read. `returning` alone would yield the new value.
        const string Sql = """
            with before as (select name, status from layer where name = @name)
            update layer set status = @status
            from before
            where layer.name = before.name
            returning before.status
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", layerName);
        command.Parameters.AddWithValue("status", Wire(status));

        object? previous = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return previous is string text ? ParseStatus(text) : null;
    }

    /// <summary>The wire form of a service status.</summary>
    public static string Wire(ServiceStatus status) =>
        status == ServiceStatus.Started ? "started" : "stopped";

    /// <summary>Parses the wire form, or refuses.</summary>
    public static ServiceStatus ParseStatus(string status) => status switch
    {
        "started" => ServiceStatus.Started,
        "stopped" => ServiceStatus.Stopped,
        _ => throw new InvalidOperationException(
            $"The service status '{status}' is not one this build knows."),
    };

    /// <summary>The wire form of a sharing scope.</summary>
    public static string Wire(SharingScope scope) => scope switch
    {
        SharingScope.Private => "private",
        SharingScope.Organization => "organization",
        SharingScope.Public => "public",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>Parses the wire form, or refuses.</summary>
    public static SharingScope Parse(string scope) => scope switch
    {
        "private" => SharingScope.Private,
        "organization" => SharingScope.Organization,
        "public" => SharingScope.Public,
        _ => throw new InvalidOperationException(
            $"The sharing scope '{scope}' is not one this build knows."),
    };
}
