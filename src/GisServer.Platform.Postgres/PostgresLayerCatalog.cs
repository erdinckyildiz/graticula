using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using GisServer.Platform.Secrets;
using Npgsql;

namespace GisServer.Platform.Postgres;

/// <summary>Reads published layers from the platform store.</summary>
public sealed class PostgresLayerCatalog
{
    private const string Columns = """
        l.id, l.name, l.schema_name, l.table_name, l.geometry_column, l.srid,
        l.identity_column, l.object_id_column, l.is_hosted, l.geometry_type,
        d.name, d.connection_secret, d.key_version
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
            reader.GetGuid(0), definition, reader.GetString(10), connectionString, geometryType);
    }
}
