using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis.Offline;

/// <summary>
/// Writes the mobile geodatabase ArcGIS Field Maps and Pro take a feature service offline in, and the delta files that
/// move edits between it and the server — ADR-288.
/// </summary>
/// <remarks>
/// <b>The port of ADR-288 §6.</b> Declared here, beside the ArcGIS surface that needs it, and implemented in
/// <c>Graticula.Offline.Sqlite</c>, the only project that references a SQLite library; no SQLite type crosses it.
/// The format is the one <c>docs/research/mobile-geodatabase-format.md</c> describes.
/// </remarks>
public interface IMobileGeodatabaseWriter
{
    /// <summary>Writes a whole offline copy: a snapshot, or a registered replica when <see cref="OfflineCopy.Replica"/> is set.</summary>
    /// <param name="copy">What to write.</param>
    /// <param name="path">The file to create; it must not exist.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(OfflineCopy copy, string path, CancellationToken cancellationToken);

    /// <summary>Writes the delta a <c>synchronizeReplica</c> download returns: what changed on the server since a generation.</summary>
    /// <param name="delta">The changes.</param>
    /// <param name="path">The file to create; it must not exist.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task WriteDeltaAsync(OfflineDelta delta, string path, CancellationToken cancellationToken);

    /// <summary>Reads a delta a client uploaded for <c>synchronizeReplica</c>.</summary>
    /// <param name="path">The uploaded file.</param>
    /// <param name="layers">The replica's layers, so rows are read against the schema they were written with.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The changes, per layer.</returns>
    Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken);
}

/// <summary>A whole offline copy of a feature service.</summary>
/// <param name="ServiceName">
/// The service's name; every table in the file is named <c>{ServiceName}_{layer name}</c>, as ArcGIS names them.
/// </param>
/// <param name="ServiceDocument">The service's REST document, as <c>FeatureServer?f=json</c> answers it.</param>
/// <param name="Layers">The layers and tables, in service order.</param>
/// <param name="Replica">The registration, or null for a snapshot (<c>syncModel=none</c>).</param>
public sealed record OfflineCopy(
    string ServiceName,
    string ServiceDocument,
    IReadOnlyList<OfflineLayer> Layers,
    OfflineReplica? Replica);

/// <summary>One layer or table of an offline copy.</summary>
/// <param name="Id">The layer id in the service.</param>
/// <param name="Name">The layer's name.</param>
/// <param name="LayerDocument">The layer's REST document, as <c>FeatureServer/{id}?f=json</c> answers it.</param>
/// <param name="GeometryType">
/// <c>esriGeometryPoint</c>, <c>esriGeometryMultipoint</c>, <c>esriGeometryPolyline</c> or <c>esriGeometryPolygon</c>;
/// null for a table.
/// </param>
/// <param name="Ordinates">The ordinates the geometry column carries.</param>
/// <param name="Wkid">The reference's latest well-known id.</param>
/// <param name="Geographic">Whether the reference is geographic.</param>
/// <param name="ReferenceWkt">The reference's WKT, for the item definition.</param>
/// <param name="Fields">The fields, in order, the geometry excluded.</param>
/// <param name="ObjectIdField">The object id field's name.</param>
/// <param name="GlobalIdField">The global id field's name, or null.</param>
/// <param name="HasAttachments">Whether the layer carries attachments.</param>
/// <param name="Extent">The layer's extent, or null when it has no features.</param>
/// <param name="Rows">The rows to write, read when the writer reaches this layer.</param>
/// <param name="Attachments">The attachments to write, read when the writer reaches this layer.</param>
public sealed record OfflineLayer(
    int Id,
    string Name,
    string LayerDocument,
    string? GeometryType,
    GeometryOrdinates Ordinates,
    int Wkid,
    bool Geographic,
    string ReferenceWkt,
    IReadOnlyList<OfflineField> Fields,
    string ObjectIdField,
    string? GlobalIdField,
    bool HasAttachments,
    Envelope? Extent,
    Func<CancellationToken, IAsyncEnumerable<OfflineRow>> Rows,
    Func<CancellationToken, IAsyncEnumerable<OfflineAttachment>> Attachments);

/// <summary>A field of an offline layer.</summary>
/// <param name="Name">The column name.</param>
/// <param name="EsriType">The ArcGIS field type, <c>esriFieldTypeString</c> and so on.</param>
/// <param name="Alias">The alias, or null.</param>
/// <param name="Length">A string field's length, or null.</param>
/// <param name="Nullable">Whether the field takes null.</param>
/// <param name="Domain">The field's domain, or null.</param>
public sealed record OfflineField(string Name, string EsriType, string? Alias, int? Length, bool Nullable, OfflineDomain? Domain);

/// <summary>A field domain.</summary>
/// <param name="Name">The domain's name, unique in the file.</param>
public abstract record OfflineDomain(string Name);

/// <summary>A coded value domain.</summary>
/// <param name="Name">The domain's name.</param>
/// <param name="Codes">Each code and its name, in order.</param>
public sealed record OfflineCodedDomain(string Name, IReadOnlyList<KeyValuePair<object, string>> Codes) : OfflineDomain(Name);

/// <summary>A range domain.</summary>
/// <param name="Name">The domain's name.</param>
/// <param name="Minimum">The smallest value allowed.</param>
/// <param name="Maximum">The largest value allowed.</param>
public sealed record OfflineRangeDomain(string Name, double Minimum, double Maximum) : OfflineDomain(Name);

/// <summary>
/// A row: one value per <see cref="OfflineLayer.Fields"/> entry, in order, as .NET values — <see cref="long"/>,
/// <see cref="int"/>, <see cref="short"/>, <see cref="double"/>, <see cref="string"/>, <see cref="DateTimeOffset"/> (UTC),
/// <see cref="Guid"/>, <c>byte[]</c> or null — and the geometry with its Z and M.
/// </summary>
/// <param name="Values">The values, in field order.</param>
/// <param name="Geometry">The geometry, or null.</param>
public sealed record OfflineRow(IReadOnlyList<object?> Values, Geometry? Geometry);

/// <summary>An attachment.</summary>
/// <param name="AttachmentId">The attachment's id.</param>
/// <param name="ParentGlobalId">The global id of the feature it belongs to.</param>
/// <param name="GlobalId">The attachment's own global id.</param>
/// <param name="Name">The file name.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Data">The bytes.</param>
public sealed record OfflineAttachment(long AttachmentId, Guid ParentGlobalId, Guid GlobalId, string Name, string ContentType, ReadOnlyMemory<byte> Data);

/// <summary>The registration a replica file carries.</summary>
/// <param name="Id">The replica's id, which the client sends back on every sync.</param>
/// <param name="Name">The replica's name; letters, digits and underscores only, as ArcGIS requires.</param>
/// <param name="ServiceUrl">The FeatureServer address the client syncs with.</param>
/// <param name="Owner">The user the replica belongs to.</param>
/// <param name="ServerGeneration">The server generation the copy was taken at, epoch milliseconds.</param>
/// <param name="Created">When the replica was made.</param>
/// <param name="QueryExtent">The area it was taken for.</param>
/// <param name="QueryWkid">The reference of <paramref name="QueryExtent"/>.</param>
public sealed record OfflineReplica(
    Guid Id,
    string Name,
    string ServiceUrl,
    string Owner,
    long ServerGeneration,
    DateTimeOffset Created,
    Envelope QueryExtent,
    int QueryWkid);

/// <summary>Changes between a replica and the server, either direction.</summary>
/// <param name="ReplicaId">The replica.</param>
/// <param name="ServerGeneration">The server generation the changes run up to.</param>
/// <param name="Layers">The changes per layer; a layer without changes may be absent.</param>
public sealed record OfflineDelta(Guid ReplicaId, long ServerGeneration, IReadOnlyList<OfflineLayerChanges> Layers);

/// <summary>One layer's changes.</summary>
/// <param name="Layer">The layer; its row and attachment readers are not used.</param>
/// <param name="Inserts">Rows added.</param>
/// <param name="Updates">Rows changed, whole.</param>
/// <param name="Deletes">Rows removed, by object id and global id.</param>
/// <param name="AttachmentInserts">Attachments added.</param>
/// <param name="AttachmentUpdates">Attachments changed.</param>
/// <param name="AttachmentDeletes">Attachments removed, by attachment id and global id.</param>
public sealed record OfflineLayerChanges(
    OfflineLayer Layer,
    IReadOnlyList<OfflineRow> Inserts,
    IReadOnlyList<OfflineRow> Updates,
    IReadOnlyList<OfflineKey> Deletes,
    IReadOnlyList<OfflineAttachment> AttachmentInserts,
    IReadOnlyList<OfflineAttachment> AttachmentUpdates,
    IReadOnlyList<OfflineKey> AttachmentDeletes);

/// <summary>A row's identity in a delete.</summary>
/// <param name="ObjectId">The object id.</param>
/// <param name="GlobalId">The global id.</param>
public readonly record struct OfflineKey(long ObjectId, Guid GlobalId);
