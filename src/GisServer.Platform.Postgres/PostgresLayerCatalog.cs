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
        l.owner_principal_id, l.sharing, l.status
        """;

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
            $"select {Columns} from layer l join data_source d on d.id = l.data_source_id order by l.name");

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
            $"select {Columns} from layer l join data_source d on d.id = l.data_source_id "
            + "where l.name = @name");
        command.Parameters.AddWithValue("name", name);

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
            ParseStatus(reader.GetString(15)));
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
