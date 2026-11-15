using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A service's offline sync in the platform store — migration 138, ADR-288: <c>service.sync_enabled</c> and the
/// <c>replica</c> registry.
/// </summary>
/// <remarks>
/// <b>Turning sync off and unregistering the service's replicas are one transaction</b>, so no replica outlives the
/// switch that made it possible (ADR-288 §11.1, <c>INFERRED</c>). A replica's layers and their server generations are
/// one <c>jsonb</c> array: they are read and advanced together, never queried apart.
/// </remarks>
public sealed class PostgresOfflineSyncStore : IOfflineSyncStore
{
    private const string Columns =
        "id, name, service_id, owner_principal_id, owner_name, sync_model, layers, return_attachments, "
        + "attachments_sync_direction, geometry, wkid, created_at, last_synced_at";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store over a data source.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresOfflineSyncStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand set = new("update service set sync_enabled = @enabled, updated_at = now() where id = @id", connection, transaction))
        {
            set.Parameters.AddWithValue("id", serviceId);
            set.Parameters.AddWithValue("enabled", enabled);
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int removed = 0;

        if (!enabled)
        {
            await using NpgsqlCommand unregister = new("delete from replica where service_id = @id", connection, transaction);
            unregister.Parameters.AddWithValue("id", serviceId);
            removed = await unregister.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc/>
    public async Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replica);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            insert into replica ({Columns})
            values (@id, @name, @service, @owner, @owner_name, @model, @layers, @attachments, @direction, @geometry, @wkid,
                    @created, @synced)
            """);

        command.Parameters.AddWithValue("id", replica.Id);
        command.Parameters.AddWithValue("name", replica.Name);
        command.Parameters.AddWithValue("service", replica.ServiceId);
        command.Parameters.AddWithValue("owner", replica.OwnerId);
        command.Parameters.AddWithValue("owner_name", replica.OwnerName);
        command.Parameters.AddWithValue("model", replica.SyncModel);
        command.Parameters.AddWithValue("layers", NpgsqlDbType.Jsonb, WriteLayers(replica.Layers));
        command.Parameters.AddWithValue("attachments", replica.ReturnAttachments);
        command.Parameters.AddWithValue("direction", replica.AttachmentsSyncDirection);
        command.Parameters.AddWithValue("geometry", NpgsqlDbType.Jsonb, (object?)replica.Geometry ?? DBNull.Value);
        command.Parameters.AddWithValue("wkid", NpgsqlDbType.Integer, (object?)replica.Wkid ?? DBNull.Value);
        command.Parameters.AddWithValue("created", replica.Created);
        command.Parameters.AddWithValue("synced", replica.LastSynced);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Columns} from replica where id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} from replica where service_id = @service order by created_at, id");
        command.Parameters.AddWithValue("service", serviceId);

        List<ReplicaRecord> replicas = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            replicas.Add(Read(reader));
        }

        return replicas;
    }

    /// <inheritdoc/>
    public async Task<bool> AdvanceAsync(
        Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layers);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update replica set layers = @layers, last_synced_at = @synced where id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("layers", NpgsqlDbType.Jsonb, WriteLayers(layers));
        command.Parameters.AddWithValue("synced", synced);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from replica where id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>The layers as the column keeps them.</summary>
    internal static string WriteLayers(IReadOnlyList<ReplicaLayer> layers) =>
        JsonSerializer.Serialize(layers.Select(l => new
        {
            id = l.Id,
            serverGen = l.ServerGeneration,
            queryOption = l.QueryOption,
            where = l.Where,
            useGeometry = l.UseGeometry,
        }));

    /// <summary>The layers as the column keeps them, read back.</summary>
    internal static IReadOnlyList<ReplicaLayer> ReadLayers(string json)
    {
        List<ReplicaLayer> layers = [];

        foreach (JsonElement layer in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            layers.Add(new ReplicaLayer(
                layer.GetProperty("id").GetInt32(),
                layer.GetProperty("serverGen").GetInt64(),
                layer.TryGetProperty("queryOption", out JsonElement option) ? option.GetString() ?? "useFilter" : "useFilter",
                layer.TryGetProperty("where", out JsonElement where) ? where.GetString() ?? string.Empty : string.Empty,
                !layer.TryGetProperty("useGeometry", out JsonElement geometry) || geometry.GetBoolean()));
        }

        return layers;
    }

    private static ReplicaRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.GetString(4),
        reader.GetString(5),
        ReadLayers(reader.GetString(6)),
        reader.GetBoolean(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12));
}
