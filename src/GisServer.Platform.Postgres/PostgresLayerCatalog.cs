using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Secrets;
using Npgsql;

namespace GisServer.Platform.Postgres;

/// <summary>Reads published layers from the platform store.</summary>
public sealed class PostgresLayerCatalog
{
    private const string Columns = """
        l.id, l.name, l.schema_name, l.table_name, l.geometry_column, l.srid,
        l.identity_column, l.object_id_column,

        -- Derived, never read from l.is_hosted. That column was written false by
        -- every insert since version 1, so anything trusting it concluded that
        -- nothing in the world was hosted — which silently disabled every vector
        -- tile service (Q-67). Hosted means the data lives in the datastore, and
        -- the datastore is a registered source flagged as such, so the fact has
        -- exactly one home and cannot drift from the other one.
        d.is_datastore, l.geometry_type,
        d.name, d.connection_secret, d.key_version,

        -- <b>From the service, never from the layer.</b> The layer's own
        -- sharing, status and owner columns survive migration 11 and are read
        -- by nothing: a service holding three layers with three scopes cannot
        -- answer "who may see this service", so the question is asked of the
        -- container. Reading l.sharing here would be the is_hosted mistake a
        -- second time, on the column that decides who sees what.
        s.owner_principal_id, s.sharing, s.status, l.attachment_quota_bytes,
        s.id, l.layer_index, s.name, s.folder, s.kind, s.description
        """;

    /// <summary>The joins a layer read needs: a layer, its source, its service.</summary>
    private const string From =
        "from layer l "
        + "join data_source d on d.id = l.data_source_id "
        + "join service s on s.id = l.service_id";

    /// <summary>
    /// The same joins driven from the service, so an empty one is still a service.
    /// </summary>
    /// <remarks>
    /// <b>Left, and it matters the moment somebody creates a service before
    /// adding layers to it.</b> An inner join makes that service invisible — the
    /// administrator who just created it sees nothing in the catalogue and
    /// reasonably concludes the creation failed.
    /// </remarks>
    private const string ServiceFrom =
        "from service s "
        + "left join layer l on l.service_id = s.id "
        + "left join data_source d on d.id = l.data_source_id";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SecretProtector _secrets;

    /// <summary>Creates a catalogue reader.</summary>
    public PostgresLayerCatalog(NpgsqlDataSource dataSource, SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(secrets);

        _dataSource = dataSource;
        _secrets = secrets;
    }

    /// <summary>Every published layer.</summary>
    public async Task<IReadOnlyList<PublishedLayer>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} order by s.name, l.layer_index");

        List<PublishedLayer> layers = [];
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            layers.Add(Map(reader));
        }

        return layers;
    }

    /// <summary>One layer by published name, or <see langword="null"/>.</summary>
    public async Task<PublishedLayer?> FindAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} where l.name = @name order by s.name limit 1");
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>One layer by its catalogue id, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The layer, or null.</returns>
    /// <remarks>
    /// Relationships name layers by id rather than by name, because a service
    /// can be renamed and a declaration that pointed at a name would then point
    /// at nothing — or worse, at whatever took the name next.
    /// </remarks>
    public async Task<PublishedLayer?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} where l.id = @id");

        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private PublishedLayer Map(NpgsqlDataReader reader)
    {
        string layerName = reader.GetString(1);

        LayerDefinition definition = new(
            name: layerName,
            schemaName: reader.GetString(2),
            tableName: reader.GetString(3),
            geometryColumn: reader.GetString(4),
            srid: reader.GetInt32(5),
            identityColumn: reader.GetString(6),
            objectIdColumn: reader.IsDBNull(7) ? null : reader.GetString(7),
            isHosted: reader.GetBoolean(8));

        if (!Enum.TryParse(reader.GetString(9), out GeometryKind geometryType))
        {
            // The column has a check constraint, so this means the constraint
            // and this enum have drifted apart — a schema change that forgot
            // its code.
            throw new InvalidOperationException(
                $"Layer '{layerName}' declares geometry type '{reader.GetString(9)}', which this "
                + "build does not know. The schema's check constraint and GeometryKind have "
                + "diverged.");
        }

        // Decrypted here rather than carried around sealed: the connection
        // string exists to be used, and passing a ciphertext plus a protector to
        // whoever needs it just spreads the key further.
        string connectionString = _secrets.Unprotect(
            reader.GetFieldValue<byte[]>(11), reader.GetInt32(12));

        return new PublishedLayer(
            reader.GetGuid(0),
            definition,
            reader.GetString(10),
            connectionString,
            geometryType,
            reader.IsDBNull(13) ? null : reader.GetGuid(13),
            ParseSharing(reader.GetString(14)),
            ParseStatus(reader.GetString(15)),
            reader.GetInt64(16),
            reader.GetGuid(17),
            reader.GetInt32(18),
            reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20));
    }

    /// <summary>Every service, with its layers.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services, ordered by name.</returns>
    /// <remarks>
    /// <b>One query, then grouped in memory.</b> A query per service would be
    /// the N+1 the catalogue endpoint cannot afford — it runs on every
    /// <c>/rest/services</c> — and the join returns one row per layer, which at
    /// the 100–1,000 services this product targets is a few thousand rows.
    /// </remarks>
    public async Task<IReadOnlyList<PublishedService>> ListServicesAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {ServiceFrom} order by s.name, l.layer_index");

        return await ReadServicesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One service by folder and name, or null.</summary>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The service, or null.</returns>
    /// <remarks>
    /// <b>Matched case-insensitively</b>, because ArcGIS writes its own folder
    /// as <c>Hosted</c> and a client copying that convention must not meet a 404
    /// over a capital letter.
    /// </remarks>
    public async Task<PublishedService?> FindServiceAsync(
        string? folder, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {ServiceFrom} "
            + "where lower(s.name) = lower(@name) "
            + "  and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '') "
            + "order by l.layer_index");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue(
            "folder", (object?)folder ?? DBNull.Value);

        IReadOnlyList<PublishedService> services =
            await ReadServicesAsync(command, cancellationToken).ConfigureAwait(false);

        return services.Count == 0 ? null : services[0];
    }

    private async Task<IReadOnlyList<PublishedService>> ReadServicesAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        Dictionary<Guid, List<PublishedLayer>> byService = [];
        Dictionary<Guid, (string Name, string? Folder, string Kind, string? Description,
            Guid? Owner, SharingScope Sharing, ServiceStatus Status)> heads = [];
        List<Guid> order = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid serviceId = reader.GetGuid(17);

            if (!byService.TryGetValue(serviceId, out List<PublishedLayer>? layers))
            {
                layers = [];
                byService[serviceId] = layers;
                order.Add(serviceId);

                heads[serviceId] = (
                    reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.GetString(21),
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    reader.IsDBNull(13) ? null : reader.GetGuid(13),
                    ParseSharing(reader.GetString(14)),
                    ParseStatus(reader.GetString(15)));
            }

            // A left join, so a service with no layers arrives as one row of
            // nulls. That is a service, not a broken row.
            if (!reader.IsDBNull(0))
            {
                layers.Add(Map(reader));
            }
        }

        List<PublishedService> services = [];

        foreach (Guid id in order)
        {
            var head = heads[id];

            services.Add(new PublishedService(
                id, head.Name, head.Folder, head.Kind, head.Description,
                head.Owner, head.Sharing, head.Status, byService[id]));
        }

        return services;
    }

    /// <summary>Reads the sharing scope, refusing an unknown one.</summary>
    /// <remarks>
    /// <b>Throws rather than defaulting.</b> The column has a check constraint
    /// listing exactly three values, so an unknown one means the store was
    /// written by a different version. Defaulting to <c>Private</c> would be the
    /// safe direction for that row and would hide a store we do not understand;
    /// defaulting to anything else would publish data. Refusing is the only
    /// option that is neither silent nor dangerous.
    /// </remarks>
    /// <summary>Reads the status, refusing an unknown one.</summary>
    /// <remarks>
    /// Throws for the same reason <see cref="ParseSharing"/> does: the column
    /// has a check constraint listing exactly two values, so a third means the
    /// store was written by a version this one does not understand. Defaulting
    /// to started would serve a service somebody deliberately stopped.
    /// </remarks>
    private static ServiceStatus ParseStatus(string status) => status switch
    {
        "started" => ServiceStatus.Started,
        "stopped" => ServiceStatus.Stopped,
        _ => throw new InvalidOperationException(
            $"The service status '{status}' is not one this build knows. The platform store has "
            + "been written by a different version of the server."),
    };

    private static SharingScope ParseSharing(string sharing) => sharing switch
    {
        "private" => SharingScope.Private,
        "organization" => SharingScope.Organization,
        "public" => SharingScope.Public,
        _ => throw new InvalidOperationException(
            $"The sharing scope '{sharing}' is not one this build knows. The platform store has "
            + "been written by a different version of the server."),
    };
}
