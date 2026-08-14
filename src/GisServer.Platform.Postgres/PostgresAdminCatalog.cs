using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Admin;
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
    public async Task<Guid> PublishLayerAsync(
        LayerPublication publication, Guid owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);

        Guid id = Guid.NewGuid();

        const string Sql = """
            insert into layer
              (id, data_source_id, name, schema_name, table_name, geometry_column,
               identity_column, object_id_column, srid, geometry_type, is_hosted,
               owner_principal_id, sharing)
            values
              (@id, @source, @name, @schema, @table, @geometry,
               @identity, @objectid, @srid, @type, false,
               @owner, @sharing)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
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

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminLayer>> ListLayersAsync(CancellationToken cancellationToken)
    {
        const string Sql = """
            select l.id, l.name, d.name, l.schema_name, l.table_name, l.sharing,
                   l.owner_principal_id, p.name, l.object_id_column
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
                !reader.IsDBNull(8)));
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
            !reader.IsDBNull(4));
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
