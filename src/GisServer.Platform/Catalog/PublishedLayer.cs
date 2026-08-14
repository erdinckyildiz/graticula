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
        long attachmentQuotaBytes = 0)
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
    }

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

    /// <summary>Who may read it (ADR-018 §3b).</summary>
    public SharingScope Sharing { get; }

    /// <summary>Whether it runs at all (ADR-020 §3).</summary>
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
