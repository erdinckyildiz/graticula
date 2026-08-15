using System;
using GisServer.Catalog;
using GisServer.Geometries;
using GisServer.Platform.Identity;

namespace GisServer.Platform.Catalog;

/// <summary>
/// A layer as the catalogue holds it: its definition, plus how to reach the data.
/// </summary>
/// <remarks>
/// The connection string is separate from <see cref="LayerDefinition"/> because
/// the definition is a Tier 1 domain type that several layers may share a source
/// with, and because a credential should not travel further than it must.
/// </remarks>
public sealed class PublishedLayer
{
    /// <summary>Creates a published layer.</summary>
    public PublishedLayer(
        Guid id,
        LayerDefinition definition,
        string dataSourceName,
        string connectionString,
        GeometryKind geometryType,
        Guid? owner,
        SharingScope sharing,
        ServiceStatus status,
        long attachmentQuotaBytes = 0,
        Guid serviceId = default,
        int layerIndex = 0,
        string? serviceName = null,
        string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Id = id;
        Definition = definition;
        DataSourceName = dataSourceName;
        ConnectionString = connectionString;
        GeometryType = geometryType;
        Owner = owner;
        Sharing = sharing;
        Status = status;
        AttachmentQuotaBytes = attachmentQuotaBytes;
        ServiceId = serviceId;
        LayerIndex = layerIndex;
        ServiceName = serviceName ?? definition.Name;
        Folder = folder;
    }

    /// <summary>The service that contains it.</summary>
    public Guid ServiceId { get; }

    /// <summary>
    /// Its number within that service — the <c>{id}</c> in the URL.
    /// </summary>
    /// <remarks>
    /// <b>Assigned once and never reused.</b> A saved web map stores this number,
    /// so renumbering after a layer is removed would silently repoint somebody's
    /// map at different data. Gaps in the sequence are correct.
    /// </remarks>
    public int LayerIndex { get; }

    /// <summary>
    /// The service's name, which is the address — not the layer's own.
    /// </summary>
    /// <remarks>
    /// These were the same string until 2026-08-15, when a service became a
    /// container of layers. They are now different concepts that happen to
    /// coincide for a single-layer service, which is the most common kind, which
    /// is exactly why the distinction needs a name.
    /// </remarks>
    public string ServiceName { get; }

    /// <summary>The service's folder, or null for the root.</summary>
    public string? Folder { get; }

    /// <summary>The catalogue identity.</summary>
    public Guid Id { get; }

    /// <summary>What a provider needs to read it.</summary>
    public LayerDefinition Definition { get; }

    /// <summary>The registered source it came from, for diagnostics.</summary>
    public string DataSourceName { get; }

    /// <summary>The decrypted connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// The layer's declared geometry type.
    /// </summary>
    /// <remarks>
    /// Declared rather than inferred from the first row, because ArcGIS puts it
    /// in the response header before any feature has been read — and a layer
    /// whose query matches nothing still has a type.
    /// </remarks>
    public GeometryKind GeometryType { get; }

    /// <summary>
    /// Who owns it, or null for a layer registered before ownership existed.
    /// </summary>
    /// <remarks>
    /// Null rather than a default owner. An owner nobody chose is a fact the
    /// audit trail would then repeat, and a private layer with a wrong owner is
    /// readable by the wrong person.
    /// </remarks>
    public Guid? Owner { get; }

    /// <summary>
    /// Who may read it (ADR-018 §3b) — its service's scope, not its own.
    /// </summary>
    /// <remarks>
    /// <b>A layer has no sharing of its own and must not grow one.</b> A service
    /// with three layers and three scopes cannot answer "who may see this
    /// service", and the client asks about the service. Copied from the service
    /// on read so that call sites which reason about a single layer keep working;
    /// the database has one column, on <c>service</c>.
    /// </remarks>
    public SharingScope Sharing { get; }

    /// <summary>Whether it runs at all (ADR-020 §3) — again, its service's.</summary>
    public ServiceStatus Status { get; }

    /// <summary>
    /// How many bytes of attachments this layer may hold.
    /// </summary>
    /// <remarks>
    /// <b>Per layer, and it exists because attachments could not ship without
    /// it</b> — [ADR-013](../../../docs/adr/ADR-013-feature-service-data-model.md)
    /// §4e. The datastore is mandatory and is about to contain arbitrary user
    /// binaries, so its backup size stops being a function of feature count.
    /// One layer must not be able to consume the appliance.
    /// </remarks>
    public long AttachmentQuotaBytes { get; }

    /// <summary>Whether requests for it should be served.</summary>
    public bool IsRunning => Status == ServiceStatus.Started;
}
