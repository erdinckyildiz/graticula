using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

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
    public async Task<bool> UpdateDataSourceAsync(
        Guid id, string? name, string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // <b>`coalesce` on the name, so one statement serves both shapes.</b> A caller changing only
        // the connection sends no name, and a two-statement version would have a window where the row
        // holds the new secret and the old name.
        const string Sql = """
            update data_source
            set name = coalesce(nullif(trim(@name), ''), name),
                connection_secret = @secret,
                key_version = @version
            where id = @id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "secret", NpgsqlDbType.Bytea, _secrets.Protect(connectionString));
        command.Parameters.AddWithValue("version", _secrets.KeyVersion);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveDataSourceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("delete from data_source where id = @id");

        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LayerTable>> TablesOnAsync(
        Guid dataSourceId, CancellationToken cancellationToken)
    {
        const string Sql = """
            select name, schema_name, table_name
            from layer
            where data_source_id = @source
            order by name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("source", dataSourceId);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<LayerTable> tables = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(
                new LayerTable(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return tables;
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
            with chosen as (
                -- <b>Hosted data is always in 'hosted'; a registered table may be put in a
                -- folder.</b> Owner rule, 2026-08-17: *"turkiye klasoru sadece reference
                -- registered olanlar için. hosted olanların tamamı hosted'a gidecek."* So the
                -- requested folder is honoured for a registered source and ignored for the
                -- datastore, rather than the folder being derived for both as it was until
                -- today. A datastore publish that named a folder is told where it actually
                -- landed — the caller asked for something this server will not do, and
                -- answering 201 without saying so is the silent kind of refusal.
                select case
                         when d.is_datastore then 'hosted'
                         else nullif(trim(@folder), '')
                       end as name,
                       d.is_datastore
                from data_source d where d.id = @source
            ),
            -- The register gains the folder a registered layer was published into, so the
            -- directory lists it and it survives the last service leaving. Nothing to do for
            -- 'hosted', which migration 18 seeded.
            registered as (
                insert into folder (name)
                select c.name from chosen c where c.name is not null and not c.is_datastore
                on conflict do nothing
                returning name
            ),
            existing as (
                select s.id from service s, chosen c
                where lower(s.name) = lower(@service)
                  and coalesce(lower(s.folder), '') = coalesce(lower(c.name), '')
            ),
            created as (
                -- next_layer_index starts at 1 because this statement is about
                -- to put a layer at 0. See `slot` for why it cannot be bumped
                -- afterwards.
                insert into service
                    (id, name, folder, kind, owner_principal_id, sharing, next_layer_index)
                select gen_random_uuid(), @service, c.name, 'FeatureServer', @owner, @sharing, 1
                from chosen c
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
            -- <b>Two dead columns are no longer written — D-24.</b> `owner_principal_id`
            -- and `sharing` moved onto the service in migration 11 and nothing has read
            -- the layer's copies since. Writing them anyway is what made D-24 dangerous
            -- rather than merely untidy: a plausible value in a column nobody reads is
            -- what the next reader finds and believes. `owner_principal_id` is nullable
            -- and `sharing` defaults to 'private', so both simply stop being mentioned.
            --
            -- <b>And `is_hosted` stopped too, on 2026-08-24.</b> It was `not null` with no
            -- default, so omitting it failed the insert — the one writer that could not be
            -- removed by an edit. Migration 34 gives it a default, which is an expand and
            -- moves no reader version: the column still exists and still holds `false`,
            -- and nothing has to say so. **Dropping the three is still D-33** and waits for
            -- the release after the one that ships migration 11.
            insert into layer
              (id, data_source_id, name, schema_name, table_name, geometry_column,
               identity_column, object_id_column, srid, geometry_type,
               service_id, layer_index, parent_layer_index, cache_seconds)
            select
               @id, @source, @name, @schema, @table, @geometry,
               @identity, @objectid, @srid, @type,
               slot.id, slot.layer_index, @parent, @cache
            from slot
            -- <b>The folder comes back with the index</b>, because the caller may have asked
            -- for one it did not get: a datastore publish naming 'turkiye' lands in 'hosted',
            -- and the response has to be able to say so rather than leave them to discover it
            -- from a 404 at the URL they expected.
            returning layer_index, (select name from chosen)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("service", publication.ServiceName ?? publication.Name);
        command.Parameters.AddWithValue(
            "parent", (object?)publication.ParentLayerIndex ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "cache", (object?)publication.CacheSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("source", publication.DataSourceId);
        command.Parameters.AddWithValue(
            "folder", (object?)publication.Folder?.Trim() ?? DBNull.Value);
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

        int at = 0;
        string? landed = null;
        bool published = false;

        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                published = true;
                at = reader.GetInt32(0);
                landed = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        /*
          <b>No row means nothing was written, and until 2026-08-23 this said nothing.</b>
          [D-147](../../../docs/architecture-debt.md): `chosen` reads the data source, and every
          insert below selects from it, so an id that matches no row makes the entire statement a
          no-op. `at` stayed 0, `landed` stayed null, and the address returned below was the one
          computed in memory before the statement ran — so `POST /admin/layers` with a data
          source that does not exist answered **201 Created** with an id, a service name and a
          URL, and the store had fourteen layers before and after.

          <b>The comment on `slot` above records the same class of defect from June</b> — a
          service created with no layers and a 201 saying it worked — which is the argument for
          checking the row count rather than for reading the CTE more carefully next time.

          <b>The unknown source is the only way to get here, which is why the message names
          it.</b> Every other failure in this statement raises: a duplicate name is 23505, a
          missing service is 23503, a bad enum is 23514. Zero rows has exactly one cause.
        */
        if (!published)
        {
            throw new UnknownDataSourceException(publication.DataSourceId);
        }

        return new PublishedLayerAddress(
            id,
            publication.ServiceName ?? publication.Name,
            at,
            landed);
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
    public async Task<bool> SetTimeFieldAsync(
        string name, string? field, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update layer set time_field = @field, updated_at = now() where name = @name");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("field", (object?)field ?? DBNull.Value);

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
        // <b>Sharing and status from the service, not from the layer.</b> Migration 11 moved
        // both onto the container and this listing kept reading the layer's copies, so it
        // reported a sharing scope and a status that nothing enforces — and disagreed with
        // /admin/featureservices about the same service. Found 2026-08-17 by stopping a layer
        // and watching it keep serving.
        const string Sql = """
            select l.id, l.name, d.name, l.schema_name, l.table_name, s.sharing,
                   s.owner_principal_id, p.name, l.object_id_column, s.status,
                   d.is_datastore, s.name, s.folder, l.layer_index,

                   -- Q-129, on the end: the console shows a declaration that is set and
                   -- a control that shows nothing is a control nobody trusts.
                   l.time_field,

                   -- <b>D-159, and the same sentence turned out to be literally true one
                   -- column along.</b> The console has read `l.cacheSeconds` off this
                   -- listing since the tile-cache control was written, and this listing
                   -- has never selected it — so the box was blank for every layer, and
                   -- pressing Set with a blank box sent 0, which means *never cache*.
                   l.cache_seconds
            from layer l
            join data_source d on d.id = l.data_source_id
            join service s on s.id = l.service_id
            left join principal p on p.id = s.owner_principal_id
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
                reader.GetBoolean(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15)));
        }

        return layers;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>A union, and the reason is in the type's own comment.</b> The register is the
    /// source of truth for a folder's *existence*, and <c>service.folder</c> is still where
    /// membership lives with no foreign key between them (migration 18, expand only). So a
    /// folder that a service points at and the register does not hold is reported anyway,
    /// marked as unregistered — the alternative is a folder that answers at its URL and is
    /// absent from the list of folders, which is the fault this whole change is fixing.
    /// </remarks>
    public async Task<IReadOnlyList<AdminFolder>> ListFoldersAsync(
        CancellationToken cancellationToken)
    {
        const string Sql = """
            with names as (
                select name, true as registered from folder
                union
                select distinct folder, false from service
                 where folder is not null and folder <> ''
                union
                select distinct folder, false from system_service
                 where folder is not null and folder <> ''
            ),
            folded as (
                select lower(name) as key,
                       min(name) as name,
                       bool_or(registered) as registered
                  from names group by lower(name)
            )
            select f.name,
                   (select count(*) from service s
                     where lower(coalesce(s.folder, '')) = f.key),
                   (select count(*) from system_service y
                     where lower(coalesce(y.folder, '')) = f.key),
                   (select count(*) from layer l
                     join service s2 on s2.id = l.service_id
                     where lower(coalesce(s2.folder, '')) = f.key),
                   f.registered
              from folded f
             order by lower(f.name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<AdminFolder> folders = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new AdminFolder(
                reader.GetString(0),
                (int)reader.GetInt64(1),
                (int)reader.GetInt64(2),
                (int)reader.GetInt64(3),
                reader.GetBoolean(4)));
        }

        return folders;
    }

    /// <inheritdoc/>
    public async Task<bool> CreateFolderAsync(
        string name, Guid? owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The unique index is on lower(name), so this is what makes the call idempotent
        // rather than a 23505 the caller has to interpret.
        const string Sql = """
            insert into folder (name, owner_principal_id)
            values (@name, @owner)
            on conflict do nothing
            returning name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Emptiness is part of the statement</b>, for the same reason a service delete puts
    /// it there: a check followed by a delete leaves a window in which something is
    /// published into the folder between the two.
    /// </remarks>
    public async Task<(Removal Outcome, int Services, int SystemServices)> DeleteFolderAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Delete = """
            delete from folder f
             where lower(f.name) = lower(@name)
               and not exists (
                     select 1 from service s
                      where lower(coalesce(s.folder, '')) = lower(f.name))
               and not exists (
                     select 1 from system_service y
                      where lower(coalesce(y.folder, '')) = lower(f.name))
            """;

        await using NpgsqlCommand delete = _dataSource.CreateCommand(Delete);
        delete.Parameters.AddWithValue("name", name);

        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return (Removal.Removed, 0, 0);
        }

        const string Holds = """
            select (select count(*) from service s
                     where lower(coalesce(s.folder, '')) = lower(@name)),
                   (select count(*) from system_service y
                     where lower(coalesce(y.folder, '')) = lower(@name)),
                   exists (select 1 from folder f where lower(f.name) = lower(@name))
            """;

        await using NpgsqlCommand holds = _dataSource.CreateCommand(Holds);
        holds.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await holds.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        int services = (int)reader.GetInt64(0);
        int system = (int)reader.GetInt64(1);
        bool registered = reader.GetBoolean(2);

        // Not in the register and nothing points at it: there is no such folder. Not in the
        // register but something does point at it is *occupied*, not absent — the folder is
        // real enough to serve a URL, and saying "no such folder" would be a lie the
        // directory contradicts.
        return (services + system == 0 && !registered ? Removal.Absent : Removal.Occupied,
                services, system);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Counted in the query rather than by reading the rows.</b> A service holding
    /// three thousand layers is a legitimate shape and the console only wants the
    /// number; two correlated subqueries answer it without carrying the layers back.
    /// </remarks>
    public async Task<IReadOnlyList<AdminService>> ListServicesAsync(
        CancellationToken cancellationToken)
    {
        // The cover is the lowest-numbered layer, which is layer 0 for everything published
        // here and the first surviving one after a delete. Ordered rather than assumed: index
        // 0 is not reserved, and a service whose first layer was unpublished still has a
        // member to draw and to address.
        const string Sql = """
            select s.id, s.name, s.folder, s.kind, s.sharing, s.status, s.description, p.name,
                   (select count(*) from layer l where l.service_id = s.id),
                   (select count(*) from group_layer g where g.service_id = s.id),
                   (select l.name from layer l
                     where l.service_id = s.id order by l.layer_index limit 1),
                   (select l.layer_index from layer l
                     where l.service_id = s.id order by l.layer_index limit 1),
                   s.updated_at, s.created_at
            from service s
            left join principal p on p.id = s.owner_principal_id
            order by coalesce(s.folder, ''), lower(s.name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<AdminService> services = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Add(new AdminService(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                Parse(reader.GetString(4)),
                ParseStatus(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                (int)reader.GetInt64(8),
                (int)reader.GetInt64(9),
                reader.IsDBNull(10)
                    ? null
                    : new AdminServiceCover(reader.GetString(10), reader.GetInt32(11)),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetFieldValue<DateTimeOffset>(13)));
        }

        return services;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>One statement, and the emptiness is part of the <c>where</c>.</b> Checking
    /// first and deleting second would leave a window in which a layer is published
    /// into the service between the two — and the delete would then take it along,
    /// because <c>layer.service_id</c> cascades. Making the condition part of the delete
    /// hands that race to the database instead of losing it here.
    /// </para>
    /// <para>
    /// The counts come from a second read, taken only when nothing was deleted, so the
    /// refusal can say what is in the way. Reading them afterwards is sound: whatever
    /// they are now, they were not zero at the moment that mattered.
    /// </para>
    /// </remarks>
    public async Task<(Removal Outcome, int Layers, int Groups)> DeleteServiceAsync(
        string name,
        string? folder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Delete = """
            delete from service s
             where lower(s.name) = lower(@name)
               and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '')
               and not exists (select 1 from layer l where l.service_id = s.id)
               and not exists (select 1 from group_layer g where g.service_id = s.id)
            """;

        await using NpgsqlCommand delete = _dataSource.CreateCommand(Delete);
        delete.Parameters.AddWithValue("name", name);
        delete.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return (Removal.Removed, 0, 0);
        }

        const string Holds = """
            select (select count(*) from layer l where l.service_id = s.id),
                   (select count(*) from group_layer g where g.service_id = s.id)
              from service s
             where lower(s.name) = lower(@name)
               and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '')
            """;

        await using NpgsqlCommand holds = _dataSource.CreateCommand(Holds);
        holds.Parameters.AddWithValue("name", name);
        holds.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        await using NpgsqlDataReader reader =
            await holds.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (Removal.Absent, 0, 0);
        }

        return (Removal.Occupied, (int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The same shape, and the children are counted from both tables.</b> A group may
    /// hold feature layers and other groups, and migration 12's foreign keys mean the
    /// database would refuse this anyway — but it would refuse with a constraint name,
    /// and an operator reading <c>layer_parent_is_a_group</c> learns nothing about which
    /// layers to move.
    /// </remarks>
    public async Task<(Removal Outcome, int Children)> DeleteGroupLayerAsync(
        string name,
        string? folder,
        int index,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Delete = """
            delete from group_layer g
             using service s
             where g.service_id = s.id
               and g.layer_index = @index
               and lower(s.name) = lower(@name)
               and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '')
               and not exists (
                     select 1 from layer l
                      where l.service_id = s.id and l.parent_layer_index = g.layer_index)
               and not exists (
                     select 1 from group_layer c
                      where c.service_id = s.id and c.parent_layer_index = g.layer_index)
            """;

        await using NpgsqlCommand delete = _dataSource.CreateCommand(Delete);
        delete.Parameters.AddWithValue("name", name);
        delete.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        delete.Parameters.AddWithValue("index", index);

        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return (Removal.Removed, 0);
        }

        const string Holds = """
            select (select count(*) from layer l
                     where l.service_id = s.id and l.parent_layer_index = g.layer_index)
                 + (select count(*) from group_layer c
                     where c.service_id = s.id and c.parent_layer_index = g.layer_index)
              from group_layer g
              join service s on s.id = g.service_id
             where g.layer_index = @index
               and lower(s.name) = lower(@name)
               and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '')
            """;

        await using NpgsqlCommand holds = _dataSource.CreateCommand(Holds);
        holds.Parameters.AddWithValue("name", name);
        holds.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        holds.Parameters.AddWithValue("index", index);

        await using NpgsqlDataReader reader =
            await holds.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (Removal.Absent, 0);
        }

        return (Removal.Occupied, (int)reader.GetInt64(0));
    }

    /// <inheritdoc/>
    public async Task<StyledService?> FindServiceForStyleAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Sql = """
            select s.name, s.folder, s.style,
                   coalesce(array_agg(l.name) filter (where l.name is not null), '{}')
            from service s
            left join layer l on l.service_id = s.id
            where lower(s.name) = lower(@name)
            group by s.id, s.name, s.folder, s.style
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StyledService(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetFieldValue<string[]>(3),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> SweepEmptyServicesAsync(
        CancellationToken cancellationToken)
    {
        // <b>One statement, and it returns what it did.</b> Selecting the empty ones and
        // then deleting them by name is two round trips with a gap in the middle, and a
        // layer published into one of them during that gap would be unpublished by a
        // sweep that had already decided. `delete ... returning` cannot be raced.
        //
        // <b>`group_layer` counts as holding something.</b> A service with no feature
        // layers and a group layer is not empty — the group is a container the operator
        // made, and it is exactly the sort of thing this must not take away.
        //
        // <b>And so does `coverage`, added 2026-08-21 the day image services arrived.</b>
        // An ImageServer holds a registered raster and no layers at all, so before this
        // line every one of them looked empty: pressing *remove empty services* deleted
        // the lot, and `on delete cascade` took the registrations with them. Found by
        // registering a coverage, running the suite, and finding the coverage gone.
        //
        // **The general shape is what to keep.** This query decides emptiness by listing
        // the kinds of thing a service can hold, so every new kind has to be added here
        // or it is silently destroyed by a button labelled harmless. That is
        // [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §4's
        // stated consequence arriving, and it is the third list in this repository that
        // had to grow when a face did — after `AdminEndpoints.Served` and the viewers.
        const string Sql = """
            delete from service s
             where not exists (select 1 from layer l where l.service_id = s.id)
               and not exists (select 1 from group_layer g where g.service_id = s.id)
               and not exists (select 1 from coverage c where c.service_id = s.id)
            returning coalesce(nullif(s.folder, '') || '/', '') || s.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

        List<string> removed = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            removed.Add(reader.GetString(0));
        }

        return removed;
    }

    /// <inheritdoc/>
    public async Task<SymbolisedLayer?> FindLayerForSymbologyAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Sql = """
            select l.name, coalesce(s.name, l.name), l.geometry_type, l.symbology
            from layer l
            left join service s on s.id = l.service_id
            where lower(l.name) = lower(@name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // <b>Parsed from text, not cast from an integer.</b> `geometry_type` is a text
        // column with a check constraint, which is how PostgresLayerCatalog reads it —
        // and the first version of this cast an int and answered 500 on every request.
        // A failure here means the constraint and the enum have drifted apart.
        string kind = reader.GetString(2);

        if (!Enum.TryParse(kind, out GeometryKind geometry))
        {
            throw new InvalidOperationException(
                $"Layer '{reader.GetString(0)}' declares geometry type '{kind}', which this build "
                + "does not know. The schema's check constraint and GeometryKind have diverged.");
        }

        return new SymbolisedLayer(
            reader.GetString(0),
            reader.GetString(1),
            geometry,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <inheritdoc/>
    public async Task<bool> SetSymbologyAsync(
        string name, string? canonical, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>The timestamp moves with the document, including to null.</b> Clearing the
        // symbology is a decision as much as setting one is, and a stamp left behind
        // would say a layer was styled at a time when it was un-styled.
        //
        // <b>And the parameter is cast, because clearing is what found this.</b> A bare
        // `@document` inside a `case when ... is null` gives Postgres nothing to infer a
        // type from — *42P08: could not determine data type of parameter $1* — and the
        // failure only appears on the clear, where the value is null. Setting a document
        // worked, so the defect was one code path deep in a pair that looks symmetrical.
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            update layer
               set symbology = @document::text,
                   symbology_updated_at =
                     case when @document::text is null then null else now() end,
                   updated_at = now()
             where lower(name) = lower(@name)
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("document", (object?)canonical ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetStyleAsync(
        string name, string? style, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>style_updated_at moves with the style and is cleared with it</b>, so
        // "when was this styled" cannot outlive the style itself.
        //
        // <b>The cast is load-bearing.</b> Without ::text the clear path fails
        // with 42P08, could not determine data type of parameter $1: a null
        // parameter inside a CASE gives Postgres nothing to infer from, and the
        // set-a-style path works fine while the clear-it path 500s.
        const string Sql = """
            update service
               set style = @style::text,
                   style_updated_at =
                     case when @style::text is null then null else now() end
             where lower(name) = lower(@name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("style", (object?)style ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Addressed by name and folder, matched case-insensitively</b>, the way
    /// every other service lookup in this catalogue is since migration 15 — a
    /// setter that matched case-sensitively would fail to find the service the read
    /// path finds.
    /// </para>
    /// <para>
    /// <b>All four columns are written, nulls included.</b> ADR-031 makes null mean
    /// *unset*; a partial write would leave a service configured in a way its
    /// operator did not ask for and cannot see from the screen they used.
    /// </para>
    /// <para>
    /// <b>The database checks the values as well.</b> Migration 16 constrains the
    /// ceiling to known names and the timeout to a positive bound, and
    /// <see cref="ServiceCapabilityLimits"/> refuses both in the domain. Two checks
    /// for one rule is deliberate here: the constraint is what stops a value that
    /// arrived by any other route — a migration, a script, a future endpoint — from
    /// becoming a service that looks configured and is not.
    /// </para>
    /// </remarks>
    public async Task<bool> SetServiceCapabilitiesAsync(
        string name,
        string? folder,
        ServiceCapabilityLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(limits);

        const string Sql = """
            update service
               set serves_features     = @features,
                   serves_tiles        = @tiles,
                   capability_ceiling  = @ceiling::text[],
                   statement_timeout_ms = @timeout,
                   max_record_count     = @maxRows,
                   default_record_count = @defaultRows,
                   max_response_bytes   = @responseBytes,
                   max_request_bytes    = @requestBytes,
                   max_edits_per_transaction = @edits,
                   request_deadline_seconds  = @deadline,
                   updated_at          = now()
             where lower(name) = lower(@name)
               and coalesce(lower(folder), '') = coalesce(lower(@folder), '')
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("features", (object?)limits.ServesFeatures ?? DBNull.Value);
        command.Parameters.AddWithValue("tiles", (object?)limits.ServesTiles ?? DBNull.Value);

        command.Parameters.Add(new NpgsqlParameter("ceiling", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = limits.Ceiling is null ? DBNull.Value : new List<string>(limits.Ceiling).ToArray(),
        });

        command.Parameters.AddWithValue(
            "timeout",
            limits.StatementTimeout is { } span
                ? (object)(int)span.TotalMilliseconds
                : DBNull.Value);

        command.Parameters.AddWithValue(
            "maxRows", (object?)limits.Cost.MaximumRecordCount ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "defaultRows", (object?)limits.Cost.DefaultRecordCount ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "responseBytes", (object?)limits.Cost.MaximumResponseBytes ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "requestBytes", (object?)limits.Cost.MaximumRequestBytes ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "edits", (object?)limits.Cost.MaximumEditsPerTransaction ?? DBNull.Value);

        // <b>Seconds, and a whole number of them.</b> The column is an integer because a
        // sub-second bound on a whole request is not something an operator means, and because a
        // fractional value in a settings page is a value somebody has to be told how to write.
        // Rounding up rather than truncating: a half-second must not be stored as nought, which
        // the check constraint refuses anyway.
        command.Parameters.AddWithValue(
            "deadline",
            limits.Cost.RequestDeadline is { } deadline
                ? (object)(int)Math.Ceiling(deadline.TotalSeconds)
                : DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The same nine columns the setter writes, read the same way.</b> A read that
    /// selected a subset would produce a screen that saves fields it never showed,
    /// which is how a ceiling gets cleared by somebody who never saw it.
    /// </remarks>
    public async Task<ServiceCapabilityLimits?> FindServiceCapabilitiesAsync(
        string name,
        string? folder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Sql = """
            select serves_features,
                   serves_tiles,
                   capability_ceiling,
                   statement_timeout_ms,
                   max_record_count,
                   default_record_count,
                   max_response_bytes,
                   max_request_bytes,
                   max_edits_per_transaction,
                   request_deadline_seconds
              from service
             where lower(name) = lower(@name)
               and coalesce(lower(folder), '') = coalesce(lower(@folder), '')
             limit 1
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        bool? features = reader.IsDBNull(0) ? null : reader.GetBoolean(0);
        bool? tiles = reader.IsDBNull(1) ? null : reader.GetBoolean(1);
        IReadOnlyList<string>? ceiling = reader.IsDBNull(2)
            ? null
            : reader.GetFieldValue<string[]>(2);
        TimeSpan? timeout = reader.IsDBNull(3)
            ? null
            : TimeSpan.FromMilliseconds(reader.GetInt32(3));

        int? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        long? Bytes(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

        return new ServiceCapabilityLimits(features, tiles, ceiling, timeout)
            .With(new ServiceCostCeilings(
                Number(4),
                Number(5),
                Bytes(6),
                Bytes(7),
                Number(8),
                Number(9) is { } seconds ? TimeSpan.FromSeconds(seconds) : null));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>One statement, returning the old value.</b> ADR-017 §5d wants before and after in the
    /// audit record, and the only moment the *before* is knowable without a race is inside the
    /// statement that replaces it — the same reason <see cref="SetSharingAsync"/> is written this
    /// way.
    /// </remarks>
    public async Task<SharingScope?> SetServiceSharingAsync(
        string serviceName,
        string? folder,
        SharingScope sharing,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // <b>Case-insensitive on both, and the folder compared through `coalesce`.</b> The rest of
        // this class matches services this way; a lookup that differed here would find a service
        // the catalogue does not, or miss one it does — which is the defect two services differing
        // only in the case of their folder already caused once.
        // <b>The old value comes from a `from` subquery, not from `returning` alone.</b>
        // `returning` yields the *updated* row, so it cannot report what the scope was. Joining
        // against a subquery over the same table does: it is evaluated on the statement's snapshot,
        // which is the state before the update.
        const string Sql = """
            update service s
               set sharing = @sharing, updated_at = now()
              from (select id, sharing from service
                     where lower(name) = lower(@name)
                       and coalesce(lower(folder), '') = coalesce(lower(@folder), '')) was
             where s.id = was.id
            returning was.sharing
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("sharing", Wire(sharing));

        object? was = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return was is string old ? Parse(old) : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Writes the service's scope, not the layer's, and that was a shipped
    /// defect until 2026-08-15.</b> This statement said
    /// <c>update layer set sharing</c>, and <c>layer.sharing</c> is read by
    /// nothing: since migration 11 the serving path takes sharing from the
    /// owning service, because a service holding three layers with three scopes
    /// cannot answer <em>who may see this service</em>.
    /// </para>
    /// <para>
    /// So an administrator making a layer private got <c>200</c> and
    /// <c>{"from":"public","to":"private"}</c> back, the column changed, and the
    /// layer stayed readable by anybody. It was found while testing something
    /// else entirely (Q-95): a service that had just been made private answered
    /// a request it should have refused, and the register said the service was
    /// still public.
    /// </para>
    /// <para>
    /// <b>The dead column is left alone rather than kept in step.</b> Writing
    /// both is how the fact acquires two homes and they drift — which is the
    /// <c>is_hosted</c> mistake, on the column that decides who sees what.
    /// <see href="../../docs/architecture-debt.md">D-24</see> covers dropping
    /// it.
    /// </para>
    /// </remarks>
    public async Task<AdminLayer?> SetSharingAsync(
        string layerName, SharingScope sharing, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // Returns the row as it was. ADR-017 §5d wants before and after in the
        // audit record, and the only moment the "before" is knowable is inside
        // the statement that replaces it.
        const string Sql = """
            update service s set sharing = @sharing
            from layer l
            where l.service_id = s.id and l.name = @name
            returning l.id, l.name, s.sharing, s.owner_principal_id,
                      l.object_id_column, l.schema_name, l.table_name
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
            // only reports the new sharing scope. False and empty rather than a guess: a
            // value invented here would be indistinguishable from a real one.
            Hosted: false,
            Service: string.Empty,
            Folder: null,
            LayerIndex: 0);
    }

    /// <inheritdoc/>
    public async Task<bool> UnpublishLayerAsync(Guid layerId, CancellationToken cancellationToken)
    {
        /*
          <b>By id, and until 2026-08-24 this was `delete from layer where name = @name`.</b>
          [D-109](../../../docs/architecture-debt.md) records that a bare layer name is not
          unique — `FindAsync` answers `limit 1` and the one whose service sorts first wins.
          The row describes that as the wrong layer being *edited*. This statement was worse: with
          two layers of one name it deleted **both**, and the caller purged the tiles of one of
          them.

          <b>Every caller already had the layer.</b> All three read it before calling — for
          the tile purge, for the cache eviction, for the audit line — so taking the id
          costs nothing and removes the ambiguity at the only place it could destroy something.

          <b>Only the registration.</b> The table in the customer's database is not ours to drop,
          and a delete endpoint that removed data would be the single most dangerous thing in this
          API.
        */
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("delete from layer where id = @id");
        command.Parameters.AddWithValue("id", layerId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Writes the <em>service's</em> status, and until 2026-08-17 it wrote the layer's —
    /// which meant stopping a service did nothing at all.</b> Migration 11 moved status and
    /// sharing onto the service, and the read path was moved with it: the serving catalogue
    /// takes <c>s.status</c> and its comment says why. This setter was left behind, so
    /// <c>POST /admin/layers/{name}/stop</c> answered 200 with *"Requests for this service now
    /// answer 503"*, wrote a column nothing reads, and the layer kept serving.
    /// </para>
    /// <para>
    /// <b>Measured, not inferred:</b> after stopping <c>tr_kara</c> the layer document answered
    /// 200 and a count query returned 2 rows, while <c>/admin/layers</c> said *stopped* and
    /// <c>/admin/featureservices</c> said *started* about the same service.
    /// </para>
    /// <para>
    /// <b>It is the sharing defect a second time, one method away.</b> The same mistake on
    /// <c>l.sharing</c> was found and repaired on 2026-08-15 — the paragraph below is its
    /// record — and the status setter beside it was not looked at. Two facts moved in one
    /// migration; one setter was fixed.
    /// </para>
    /// </remarks>
    public async Task<ServiceStatus?> SetStatusAsync(
        string layerName, ServiceStatus status, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // A CTE captures the old value before the update overwrites it, so the
        // audit record can say what changed rather than only what it is now —
        // which anybody can read. `returning` alone would yield the new value.
        const string Sql = """
            with before as (
                select s.id, s.status
                  from service s join layer l on l.service_id = s.id
                 where l.name = @name
            )
            update service set status = @status
            from before
            where service.id = before.id
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
        SharingScope.Group => "group",
        SharingScope.Public => "public",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>Parses the wire form, or refuses.</summary>
    public static SharingScope Parse(string scope) => scope switch
    {
        "private" => SharingScope.Private,
        "organization" => SharingScope.Organization,
        "public" => SharingScope.Public,
        "group" => SharingScope.Group,
        _ => throw new InvalidOperationException(
            $"The sharing scope '{scope}' is not one this build knows."),
    };
}
