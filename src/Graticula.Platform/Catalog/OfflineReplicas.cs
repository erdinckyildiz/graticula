using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>One layer of a registered replica, and how far it has been synchronised — ADR-288.</summary>
/// <param name="Id">The layer's id in the service.</param>
/// <param name="ServerGeneration">
/// The server generation the replica holds for this layer, epoch milliseconds: everything the layer's history recorded
/// after it has not reached the replica yet.
/// </param>
/// <param name="QueryOption">ArcGIS's <c>queryOption</c>: <c>useFilter</c>, <c>all</c> or <c>none</c>.</param>
/// <param name="Where">The layer's filter, or empty for none.</param>
/// <param name="UseGeometry">Whether the replica's geometry filters this layer.</param>
public sealed record ReplicaLayer(int Id, long ServerGeneration, string QueryOption, string Where, bool UseGeometry);

/// <summary>A replica registered by <c>createReplica</c> — ADR-288 §6, the registry.</summary>
/// <param name="Id">The replica's id, which the client sends back on every sync.</param>
/// <param name="Name">The name the client gave it.</param>
/// <param name="ServiceId">The feature service it is a replica of.</param>
/// <param name="OwnerId">The principal who created it, and the only one besides an administrator who may sync it.</param>
/// <param name="OwnerName">That principal's name, as <c>replicaOwner</c> says it.</param>
/// <param name="SyncModel">Its sync model; only <c>perLayer</c> is registered (<c>none</c> is a snapshot).</param>
/// <param name="Layers">Its layers and their server generations, in service order.</param>
/// <param name="ReturnAttachments">Whether it carries attachments.</param>
/// <param name="AttachmentsSyncDirection">ArcGIS's <c>attachmentsSyncDirection</c>.</param>
/// <param name="Geometry">The filter it was created with, as ArcGIS JSON, or null for none.</param>
/// <param name="Wkid">The reference of <paramref name="Geometry"/>, or null.</param>
/// <param name="Created">When it was created.</param>
/// <param name="LastSynced">When it last synchronised; its creation until the first sync.</param>
public sealed record ReplicaRecord(
    Guid Id,
    string Name,
    Guid ServiceId,
    Guid OwnerId,
    string OwnerName,
    string SyncModel,
    IReadOnlyList<ReplicaLayer> Layers,
    bool ReturnAttachments,
    string AttachmentsSyncDirection,
    string? Geometry,
    int? Wkid,
    DateTimeOffset Created,
    DateTimeOffset LastSynced);

/// <summary>
/// Where a service's offline sync is kept: whether it is on, and the replicas registered against it — ADR-288.
/// </summary>
/// <remarks>
/// <b>Both halves in one port</b> because they change together: turning sync off unregisters every replica of the
/// service (ADR-288 §11.1, <c>INFERRED</c>), and a replica cannot be registered against a service that has it off.
/// </remarks>
public interface IOfflineSyncStore
{
    /// <summary>Turns sync on or off for a service.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="enabled">Whether it is on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many replicas were unregistered; zero when turning it on.</returns>
    Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken);

    /// <summary>Registers a replica.</summary>
    /// <param name="replica">The replica.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken);

    /// <summary>A replica by id, or null.</summary>
    /// <param name="id">The replica.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The replica, or null.</returns>
    Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A service's replicas, oldest first.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its replicas.</returns>
    Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken);

    /// <summary>Records a sync: the layers' new server generations and when it happened.</summary>
    /// <param name="id">The replica.</param>
    /// <param name="layers">Its layers with their new generations.</param>
    /// <param name="synced">When.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when the replica is gone.</returns>
    Task<bool> AdvanceAsync(Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken);

    /// <summary>Unregisters a replica.</summary>
    /// <param name="id">The replica.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there was none.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
