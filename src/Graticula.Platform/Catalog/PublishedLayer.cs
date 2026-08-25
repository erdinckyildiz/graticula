using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

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
        string? folder = null,
        int? parentIndex = null,
        TimeSpan? cacheLifetime = null,
        ServiceCostCeilings? cost = null,
        string? symbology = null,
        TimeSpan? statementTimeout = null,
        IEnumerable<Guid>? sharedWith = null,
        string? timeField = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Id = id;
        Cost = cost ?? ServiceCostCeilings.Unset;
        StatementTimeout = statementTimeout;
        SharedWith = sharedWith is null ? [] : [.. sharedWith];
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
        ParentIndex = parentIndex;
        CacheLifetime = cacheLifetime;
        Symbology = symbology;
        TimeField = timeField;
    }

    /// <summary>
    /// The column that carries this layer's phenomenon time, or null to derive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-129](../../../docs/open-questions.md), and null is the ordinary
    /// answer.</b> With nothing declared the dimension is derived from the schema —
    /// exactly one <c>Date</c> column, or no dimension — which is right for the
    /// layers that have one date and silent for the layers that have several. This
    /// says which one, for the layers where the schema cannot.
    /// </para>
    /// <para>
    /// <b>It is not trusted without looking.</b> A registered table's schema drifts
    /// under us (A-023), so a column declared last month may be gone or may no longer
    /// be a date. The dimension checks the declaration against the fields it was
    /// handed and falls back to the derivation when it does not hold — which is the
    /// same answer the layer had before anything was declared, rather than an error
    /// on a map request nobody can act on.
    /// </para>
    /// </remarks>
    public string? TimeField { get; }

    /// <summary>
    /// This layer's canonical symbology document, or null for the generated one.
    /// </summary>
    /// <remarks>
    /// <b>A MapLibre style, and the only authored artefact for appearance</b>
    /// (ADR-033 §5a). Both protocol faces derive from it on read, which is what
    /// stops them drifting apart: there is nothing to edit separately. Null is the
    /// ordinary case and a real answer — §5b gives an unstyled layer a generated
    /// appearance that is deterministic from its name.
    /// </remarks>
    public string? Symbology { get; }

    /// <summary>
    /// How long this layer's tiles stay fresh, or null for the server default.
    /// </summary>
    /// <remarks>
    /// <b>Null and zero are different answers.</b> Null is <em>nobody has
    /// said</em>; zero is <em>never cache this</em>, which is what an
    /// administrator wants for a layer that changes continuously. Collapsing
    /// them would make it impossible to ask for the second.
    /// </remarks>
    public TimeSpan? CacheLifetime { get; }

    /// <summary>
    /// What one request may cost the service this layer belongs to (Q-113).
    /// </summary>
    /// <remarks>
    /// <b>A service-level fact carried on the layer, as sharing and status already
    /// are.</b> The query path resolves a layer and never the service around it, and
    /// the read that produces this layer already joins the service — so carrying it
    /// costs nothing while asking for the service again would cost a round trip. The
    /// SQL that fills it says the same of sharing: <em>from the service, never from
    /// the layer</em>.
    /// </remarks>
    public ServiceCostCeilings Cost { get; }

    /// <summary>
    /// What this layer's service allows one database statement, or null for the pool's bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The service's value on the layer, because the layer is what opens a connection.</b>
    /// The same argument as <see cref="Cost"/>, which the catalogue has read off the service row
    /// since migration 17: a limit belongs to the service and is enforced where the work happens.
    /// </para>
    /// <para>
    /// <b>It was stored, reported and never applied until 2026-08-18.</b> ADR-031 §2a has listed
    /// a per-service statement timeout as configurable since the decision, the admin API accepted
    /// it, the console showed it and the `GET` said it back — and no query path read it. Reporting
    /// a limit that is not enforced is the same fault as advertising a `maxRecordCount` a service
    /// does not honour, which ADR-031 §3a already had to correct once. D-67.
    /// </para>
    /// </remarks>
    public TimeSpan? StatementTimeout { get; }

    /// <summary>
    /// Which groups this layer's *service* is shared with — ADR-036.
    /// </summary>
    /// <remarks>
    /// <b>The service's, like <see cref="Sharing"/> and for the same reason.</b> Migration 11 moved
    /// the scope onto the service and `layer.sharing` is vestigial; group shares were never on the
    /// layer at all. A layer carrying its service's answer is what lets the read path decide without
    /// a second lookup.
    /// </remarks>
    public ImmutableArray<Guid> SharedWith { get; }

    /// <summary>The group layer above it, or null when it sits at the top.</summary>
    public int? ParentIndex { get; }

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
