using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Admin;

/// <summary>A registered data source, as the admin API sees it.</summary>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="Name">Its administrator-chosen name.</param>
/// <param name="Kind">The provider kind. Only <c>postgis</c> in v1.</param>
/// <param name="Summary">
/// Host and database, never the credential — see <see cref="IAdminCatalog"/>.
/// </param>
/// <param name="LayerCount">How many published layers depend on it.</param>
public readonly record struct RegisteredDataSource(
    Guid Id, string Name, string Kind, string Summary, int LayerCount);

/// <summary>What is needed to publish a layer.</summary>
/// <param name="Name">The service name, unique across the server.</param>
/// <param name="DataSourceId">Which registered source it reads from.</param>
/// <param name="SchemaName">The schema.</param>
/// <param name="TableName">The table or view.</param>
/// <param name="GeometryColumn">The geometry column.</param>
/// <param name="IdentityColumn">The declared identity column (Q-57).</param>
/// <param name="ObjectIdColumn">
/// A unique integer column for the ArcGIS surface, or null. ADR-013 §2a: a layer
/// without one is servable natively and not through ArcGIS, and saying so at
/// publish time is better than at the first query.
/// </param>
/// <param name="Srid">Its SRID.</param>
/// <param name="GeometryType">Its declared geometry type.</param>
/// <param name="Sharing">Who may read it.</param>
/// <param name="CacheSeconds">
/// How long this layer's tiles stay fresh, or null for the server default.
/// <b>Asked at publish time because that is when somebody knows.</b> D-25 and
/// A-028: volatility is domain knowledge held by whoever is publishing, and the
/// alternative is that it is never set at all — a default nobody chose, applied
/// to a layer somebody could have described in one number.
/// </param>
/// <param name="ParentLayerIndex">
/// A group layer to nest this layer under, or null to put it at the top level.
/// The index must already name a group in the same service; the database
/// enforces it with a foreign key, so a typo is a refusal rather than a layer
/// hanging off nothing.
/// </param>
/// <param name="ServiceName">
/// The service to publish into, or null to give this layer one of its own.
/// <b>Null is the ordinary case and keeps the old behaviour exactly:</b> a layer
/// published on its own becomes a single-layer service named after it, which is
/// what every layer in this catalogue was before 2026-08-15. Naming an existing
/// service adds the layer to it at the next free index — which is how three
/// related layers become one service, as the owner asked for: <em>"a service is
/// a combination of layers."</em>
/// </param>
/// <param name="Folder">
/// The folder to publish into, or null for the default — <c>hosted</c> for the
/// datastore and the root for a registered source, which is what happened for every
/// publish before 2026-08-17. **A folder that does not exist is created**, at the owner's
/// request, and the caller is told so: implicit creation turns a typo into a folder, and
/// the only defence is saying which one was made.
/// </param>
public sealed record LayerPublication(
    string Name,
    Guid DataSourceId,
    string SchemaName,
    string TableName,
    string GeometryColumn,
    string IdentityColumn,
    string? ObjectIdColumn,
    int Srid,
    GeometryKind GeometryType,
    SharingScope Sharing,
    string? ServiceName = null,
    int? ParentLayerIndex = null,
    int? CacheSeconds = null,
    string? Folder = null);

/// <summary>
/// A publish named a data source this server does not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-147](../../../docs/architecture-debt.md): the publish answered 201 and created
/// nothing.</b> The statement that publishes a layer begins by reading the data source, and every
/// insert in it selects from that read. A source id that matches no row makes the whole statement
/// a no-op — no folder, no service, no layer — and the method returned the address it had
/// computed in memory before asking. The caller was told where its layer was; there was no layer.
/// </para>
/// <para>
/// <b>An exception rather than a nullable return, because five call sites assume success.</b>
/// Four of them publish into a source they have just created or just validated and will never see
/// this; the fifth is the admin endpoint, which turns it into a 404 naming the id. A nullable
/// return would have been four places to forget a null check, which is the shape of the defect
/// being repaired.
/// </para>
/// </remarks>
/// <param name="dataSourceId">The id nothing matched.</param>
public sealed class UnknownDataSourceException(Guid dataSourceId)
    : Exception($"There is no data source with id {dataSourceId}, so nothing was published.")
{
    /// <summary>The id nothing matched.</summary>
    public Guid DataSourceId { get; } = dataSourceId;
}

/// <summary>Where a freshly created group layer lives.</summary>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="LayerIndex">Its number within the service — the URL segment.</param>
public readonly record struct GroupLayerAddress(Guid Id, int LayerIndex);

/// <summary>Where a freshly published layer lives.</summary>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="ServiceName">The service it landed in.</param>
/// <param name="LayerIndex">Its number within that service — the URL segment.</param>
/// <param name="Folder">
/// The folder it actually landed in, or null for the root. <b>Returned because it may not
/// be the one that was asked for:</b> hosted data always goes to <c>hosted</c> whatever a
/// request says (owner rule, 2026-08-17), and a caller told only the service name would
/// build the wrong URL and read the 404 as a failed publish.
/// </param>
public readonly record struct PublishedLayerAddress(
    Guid Id,
    string ServiceName,
    int LayerIndex,
    string? Folder = null);

/// <summary>A published layer, as the admin API sees it.</summary>
/// <remarks>
/// <b>It carries its address, and not carrying it was a recorded gap that cost three
/// defects.</b> [ADR-020](../../../docs/adr/ADR-020-admin-surface.md) §2 already said the
/// listing should report the service and the index; until 2026-08-17 it did not, and the
/// console worked the address out by walking the services directory instead. **A stopped
/// service answers 503 to that walk**, so everything built on it silently lost stopped
/// services: the settings page for one drew from nothing, `Save` refused with *"not in the
/// services directory"*, and the service list could not offer a Start button for the one
/// service somebody most wanted to start. Three symptoms, one missing field.
/// </remarks>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="Name">Its service name.</param>
/// <param name="DataSourceName">Which source it reads from.</param>
/// <param name="Qualified">Schema-qualified table.</param>
/// <param name="Sharing">Who may read it.</param>
/// <param name="Owner">Who owns it, or null.</param>
/// <param name="OwnerName">Their name, or null.</param>
/// <param name="ArcGisServable">Whether it has an integer object id.</param>
/// <param name="Status">Whether it runs.</param>
/// <param name="Hosted">
/// Whether the data lives in the datastore, and so can be tiled. Derived from
/// the data source rather than read from <c>layer.is_hosted</c>, which is
/// written <c>false</c> by every insert and read by nothing
/// (<see href="../../../docs/architecture-debt.md">D-24</see>). Reported so the
/// console can offer a tile control only where there is a tile service, rather
/// than one that answers 400 (Q-67).
/// </param>
/// <param name="Service">The service it is in, without the folder.</param>
/// <param name="Folder">That service's folder, or null for the root.</param>
/// <param name="LayerIndex">Its number within the service — the last URL segment.</param>
/// <param name="TimeField">The column declared as this layer's time, or null to derive it (Q-129).</param>
public readonly record struct AdminLayer(
    Guid Id,
    string Name,
    string DataSourceName,
    string Qualified,
    SharingScope Sharing,
    Guid? Owner,
    string? OwnerName,
    bool ArcGisServable,
    ServiceStatus Status,
    bool Hosted,
    string Service,
    string? Folder,
    int LayerIndex,
    string? TimeField = null)
{
    /// <summary>Its address in the services directory, without the host.</summary>
    public string Address =>
        $"/rest/services/{(Folder is null ? "" : Folder + "/")}{Service}/FeatureServer/{LayerIndex}";
}

/// <summary>
/// A folder in the services directory, and what is in it.
/// </summary>
/// <remarks>
/// <b>A folder is a thing, not a string, since migration 18.</b> It was a text column on
/// the service, so it existed only while something was in it — an empty folder could not
/// be made and the last service leaving deleted it. The owner asked for both halves on
/// 2026-08-17: publish into a named folder, and make one first if you want to. <c>hosted</c>
/// is one of these rather than a rule beside them: *"hosted da bir folder."*
/// </remarks>
/// <param name="Name">Its name, in the case somebody typed.</param>
/// <param name="Services">How many feature services are in it.</param>
/// <param name="SystemServices">How many services with no layers — the geometry service.</param>
/// <param name="Layers">How many layers, across those services.</param>
/// <param name="Registered">
/// Whether the register holds a row for it. False means it exists only because a service
/// points at it, which the read path unions in deliberately — migration 18 adds no foreign
/// key yet, and a folder vanishing from the directory because a row is missing would be
/// worse than the inconsistency.
/// </param>
public readonly record struct AdminFolder(
    string Name,
    int Services,
    int SystemServices,
    int Layers,
    bool Registered)
{
    /// <summary>Whether anything is in it, and therefore whether it may be removed.</summary>
    public bool IsEmpty => Services == 0 && SystemServices == 0;
}

/// <summary>
/// A feature layer inside a service, named and numbered.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="AdminLayer"/>: this answers "which layer, and at what URL
/// segment", and carrying the whole layer would invite a caller to read sharing or status off
/// it — both of which belong to the service now (migration 11), and reading them from a layer
/// is the mistake D-57 was.
/// </remarks>
/// <param name="Name">Its service name, which is what the status endpoints take.</param>
/// <param name="LayerIndex">Its number within the service — the last URL segment.</param>
public readonly record struct AdminServiceCover(string Name, int LayerIndex);

/// <summary>
/// A feature service, and what it holds.
/// </summary>
/// <remarks>
/// <b>The counts are the point.</b> A service is a container, and until 2026-08-17
/// nothing could see an empty one: publishing creates a service implicitly,
/// unpublishing the last layer leaves it behind, and no listing anywhere reported it —
/// <c>/admin/layers</c> lists layers and <c>/admin/services</c> lists the system
/// services, which are a different table. So the ordinary residue of a day's work was
/// invisible, which is half of why <see href="../../../docs/architecture-debt.md">D-48</see>
/// went unnoticed: the missing delete had nothing to be missing from.
/// </remarks>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="Name">Its name within its folder.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="Kind">What it serves — <c>FeatureServer</c> for everything created here.</param>
/// <param name="Sharing">Who may read it.</param>
/// <param name="Status">Started or stopped.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="OwnerName">Who owns it, or null when the principal is gone.</param>
/// <param name="Layers">How many feature layers it holds.</param>
/// <param name="Groups">How many group layers it holds.</param>
/// <param name="Cover">
/// One of its feature layers — the lowest-numbered — or null when it holds none.
/// <b>Two things need a member rather than a container.</b> A picture of a service has to be
/// drawn from a layer's data, because a service holds no geometry of its own; and status is
/// still addressed by layer name, which is the residue of the layer-scoped API that
/// <see href="../../../docs/architecture-debt.md">D-57</see> came out of. Reported here so a
/// caller with the listing can do both without walking the services directory to find out
/// what is inside — which is what the console did, and which cannot see a stopped service at
/// all, since a stopped service answers 503 to the walk.
/// </param>
/// <param name="Updated">
/// When it last changed. <b>Optional at the end because it is a listing fact and not an identity
/// one</b> — every caller that had this record before still compiles, and the one that needs to sort
/// *most recently changed first* stops having to ask per service.
/// </param>
/// <param name="Created">When it was published.</param>
public readonly record struct AdminService(
    Guid Id,
    string Name,
    string? Folder,
    string Kind,
    SharingScope Sharing,
    ServiceStatus Status,
    string? Description,
    string? OwnerName,
    int Layers,
    int Groups,
    AdminServiceCover? Cover = null,
    DateTimeOffset Updated = default,
    DateTimeOffset Created = default)
{
    /// <summary>Its address in the directory: <c>folder/name</c>, or just the name.</summary>
    public string Qualified => Folder is null ? Name : $"{Folder}/{Name}";

    /// <summary>
    /// Whether it holds nothing, and may therefore be removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An image service holds a coverage and no layers, so counting layers called
    /// every one of them empty.</b> The SQL sweep was taught about coverages on
    /// 2026-08-21 and stopped deleting them; this flag was not, so the console went on
    /// listing them under *remove empty services* — offering a destructive action for
    /// something it would then decline to remove. A button that lists a thing and does
    /// not act on it teaches an operator that the button is unreliable, which is worse
    /// than either behaviour on its own.
    /// </para>
    /// <para>
    /// <b>Decided on the kind rather than on a coverage count.</b> Adding a fourth
    /// number to this record would mean every caller that builds an
    /// <see cref="AdminService"/> has to know how to count coverages, and the one that
    /// forgets writes zero — which is the `is_hosted` mistake `PostgresLayerCatalog`
    /// records at length. The kind is already carried and already true.
    /// </para>
    /// </remarks>
    public bool IsEmpty =>
        Layers == 0
        && Groups == 0
        && !string.Equals(Kind, "ImageServer", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What happened, or did not, when something was asked to be deleted.
/// </summary>
/// <remarks>
/// <b>Three outcomes rather than a bool, because two of them are refusals with
/// different answers.</b> Absent is a 404 and the caller may have the wrong name;
/// occupied is a 409 and the caller has to empty it first. Collapsing them would tell
/// an operator their service does not exist when it does, which sends them looking in
/// the wrong place.
/// </remarks>
public enum Removal
{
    /// <summary>There is no such service, group, or folder.</summary>
    Absent,

    /// <summary>It still holds something, and deleting it would take that with it.</summary>
    Occupied,

    /// <summary>Gone.</summary>
    Removed,
}

/// <summary>
/// A layer and the symbology stored against it.
/// </summary>
/// <remarks>
/// <b>The geometry travels with it because the conversion needs it.</b> A paint
/// property means different things on a fill and on a circle, so ADR-033's reader
/// refuses a style authored for the wrong geometry — and it can only do that if
/// whoever reads the layer also reads what shape it holds.
/// </remarks>
/// <param name="Name">The layer name.</param>
/// <param name="ServiceName">The service holding it, for the URL a caller builds.</param>
/// <param name="Geometry">What it holds, which decides what a paint property means.</param>
/// <param name="Symbology">The canonical document, or null for the generated appearance.</param>
public readonly record struct SymbolisedLayer(
    string Name, string ServiceName, GeometryKind Geometry, string? Symbology);

/// <summary>
/// A service and the style stored against it.
/// </summary>
/// <param name="Name">Its name.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="SourceLayers">The layer names a style may draw.</param>
/// <param name="Style">The stored style, or null for the generated one.</param>
public readonly record struct StyledService(
    string Name, string? Folder, IReadOnlyList<string> SourceLayers, string? Style);

/// <summary>One table a published layer depends on.</summary>
/// <param name="Layer">The layer's name, for a message a person reads.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Table">Its table.</param>
/// <remarks>
/// <b>Not <c>SourceTable</c>, which is taken</b> — that one is what a probe found in a database, with
/// a geometry column and an object-id candidate. This is the other direction: what a layer we have
/// already published needs to be there.
/// </remarks>
public readonly record struct LayerTable(string Layer, string Schema, string Table)
{
    /// <summary>Schema and table, in the form the probe reports.</summary>
    public string Qualified => $"{Schema}.{Table}";
}

/// <summary>
/// The write side of the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>PostgresLayerCatalog</c>, which the request path uses to
/// read. ADR-019 §4 keeps a seam between the catalogue domain and the runtime
/// domain, and the first thing that erodes such a seam is one interface that
/// both serving and administration reach for.
/// </para>
/// <para>
/// <b>A connection string never leaves here in the clear, and never enters an
/// audit record.</b> Registering a data source is the one operation that carries
/// a credential, and <see cref="RegisteredDataSource.Summary"/> exists so that
/// listing sources shows host and database without showing the password. An
/// audit log that leaks what it audits is a new place to steal from.
/// </para>
/// </remarks>
public interface IAdminCatalog
{
    /// <summary>Registers a data source, sealing its credential.</summary>
    /// <param name="name">An administrator-chosen name.</param>
    /// <param name="kind">The provider kind.</param>
    /// <param name="connectionString">The credential, in the clear. Sealed before it is stored.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new source's id.</returns>
    Task<Guid> RegisterDataSourceAsync(
        string name, string kind, string connectionString, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a registered source's connection string, and optionally its name.
    /// </summary>
    /// <param name="id">Which source.</param>
    /// <param name="name">A new name, or null to keep the current one.</param>
    /// <param name="connectionString">The new connection, in the clear.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a row was updated; false when there is no such source.</returns>
    /// <remarks>
    /// <para>
    /// <b>This did not exist until 2026-08-19, and its absence was found by the owner trying to use
    /// it.</b> A registered database could be created and listed and never corrected: a moved host, a
    /// new port, a rotated password all meant registering a second source and republishing every layer
    /// on the first. The connection string is the one field a deployment is *certain* to have to change
    /// eventually, and it was the one field with no way to change it.
    /// </para>
    /// <para>
    /// <b>Re-sealed with the current key, not re-encrypted with the old one.</b> The row carries its
    /// own <c>key_version</c>, so an update is also the one moment a source's secret can migrate
    /// forward to a rotated key — and writing it under the old version would leave a row that has been
    /// touched and still depends on a key somebody is retiring.
    /// </para>
    /// <para>
    /// <b>The kind is not a parameter.</b> A PostGIS source cannot become something else: every layer
    /// on it carries SQL this provider wrote. Changing that is deleting the source and registering
    /// another, which the caller can do and which makes what happens to the layers visible.
    /// </para>
    /// </remarks>
    Task<bool> UpdateDataSourceAsync(
        Guid id, string? name, string connectionString, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a registered source, but only while nothing is published on it.
    /// </summary>
    /// <param name="id">Which source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a row was removed; false when there is no such source.</returns>
    /// <remarks>
    /// <b>Refused while it holds layers, by the database rather than by politeness.</b> `layer` has a
    /// foreign key to `data_source`, so a delete with layers on it raises `23503` — and that is the
    /// right place for the rule to live, because it holds against any route to the table. The caller
    /// turns it into a sentence; nothing here cascades. Unpublishing a layer has consequences of its
    /// own (it purges tiles and forgets a remembered shape) and they should not happen as a side effect
    /// of tidying a list.
    /// </remarks>
    Task<bool> RemoveDataSourceAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Which tables the layers on one data source depend on.
    /// </summary>
    /// <param name="dataSourceId">Which source.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per layer, with the table it reads.</returns>
    /// <remarks>
    /// <b>For the one caller that has to know before it writes:</b> replacing a connection string
    /// points every layer on that source at a different database, and the honest check is whether
    /// their tables are in it. <see cref="AdminLayer"/> deliberately does not carry the schema and
    /// table — it is a listing for a screen — so this asks the question directly rather than widening
    /// a record that thirty callers already use.
    /// </remarks>
    Task<IReadOnlyList<LayerTable>> TablesOnAsync(
        Guid dataSourceId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure the datastore is registered as a source, and returns it.
    /// </summary>
    /// <param name="connectionString">The datastore's connection, in the clear.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The datastore source's id.</returns>
    /// <remarks>
    /// <para>
    /// <b>Registered by the server at startup, not by an administrator.</b>
    /// [ADR-019](../../../docs/adr/ADR-019-portal-server-split.md) fuses the
    /// datastore into the product and [Q-69] makes it mandatory, so it is not a
    /// thing somebody chooses to add — it is already there, and asking them to
    /// register it would be asking them to re-enter a credential the server
    /// already holds.
    /// </para>
    /// <para>
    /// <b>Idempotent, and it re-seals the credential each time.</b> The
    /// connection can change between restarts — a new password, a new host in a
    /// container rebuild — and a datastore row pointing at yesterday's address
    /// would make every hosted layer unreadable while looking correctly
    /// registered.
    /// </para>
    /// </remarks>
    Task<Guid> EnsureDatastoreAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Lists registered data sources, without their credentials.</summary>
    Task<IReadOnlyList<RegisteredDataSource>> ListDataSourcesAsync(
        CancellationToken cancellationToken);

    /// <summary>Reads back a source's connection string, for probing it.</summary>
    /// <remarks>
    /// Deliberately explicit rather than folded into the listing. Decrypting a
    /// credential is a thing a caller should have to ask for by name, so that
    /// every place it happens is greppable.
    /// </remarks>
    Task<string?> ConnectionStringOfAsync(Guid dataSourceId, CancellationToken cancellationToken);

    /// <summary>Publishes a layer, owned by the given principal.</summary>
    /// <remarks>
    /// <b>Returns the address, not just the id.</b> The caller needs to tell
    /// somebody where the layer now is, and since a service is a container of
    /// layers that address is a service name plus an index — neither of which
    /// the caller can compute, because the index depends on what was already in
    /// the service when this ran.
    /// </remarks>
    /// <param name="publication">What to publish.</param>
    /// <param name="owner">Who will own it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Where the layer landed.</returns>
    Task<PublishedLayerAddress> PublishLayerAsync(
        LayerPublication publication, Guid owner, CancellationToken cancellationToken);

    /// <summary>Sets how long a layer's tiles stay fresh.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="seconds">Seconds, or null to fall back to the server default.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the layer existed.</returns>
    /// <remarks>
    /// <b>No purge afterwards, deliberately.</b> Changing how long a tile stays
    /// fresh does not make any cached byte wrong — it changes when the next read
    /// decides an entry is stale. Purging would throw away a seeded pyramid to
    /// apply a number that does not affect content, which is the same reasoning
    /// [ADR-010](../../../docs/adr/ADR-010-caching.md) §5.1 gets right for
    /// sharing changes and would get wrong here.
    /// </remarks>
    Task<bool> SetCacheLifetimeAsync(
        string name, int? seconds, CancellationToken cancellationToken);

    /// <summary>Declares which column carries a layer's time, or clears the declaration.</summary>
    /// <param name="name">The layer.</param>
    /// <param name="field">The column, or null to go back to deriving it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether a layer of that name was found.</returns>
    /// <remarks>
    /// <b>[Q-129](../../../docs/open-questions.md), and the column is not checked
    /// here.</b> A registered table's schema drifts under us (A-023), so a
    /// declaration that is valid at the moment it is written can stop being valid
    /// without anybody touching this row. The endpoint checks it against the layer's
    /// fields and says so; the dimension checks it again on every read and falls back
    /// to the derivation. Storing it unchecked is what lets a publisher declare a
    /// column ahead of a schema change instead of after it.
    /// </remarks>
    Task<bool> SetTimeFieldAsync(
        string name, string? field, CancellationToken cancellationToken);

    /// <summary>Creates an empty service.</summary>
    /// <param name="name">Its name within the folder.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="description">What it is for, or null.</param>
    /// <param name="sharing">Who may read it.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its id, or null when a service of that name is already there.</returns>
    /// <remarks>
    /// <b>Empty services exist because the tree has to be built downwards.</b> A
    /// group layer needs a service to live in, and a layer nested in a group
    /// needs the group — so the first thing created cannot be a layer. Until
    /// this existed the only way to get a service was to publish one into
    /// existence, which put a layer at index 0 that could never be moved under
    /// the group that came after it.
    /// </remarks>
    Task<Guid?> CreateServiceAsync(
        string name,
        string? folder,
        string? description,
        SharingScope sharing,
        Guid owner,
        CancellationToken cancellationToken);

    /// <summary>Creates a group layer inside a service.</summary>
    /// <param name="folder">The service's folder, or null for the root.</param>
    /// <param name="serviceName">The service's name.</param>
    /// <param name="name">What to call the group.</param>
    /// <param name="parentLayerIndex">A group to nest it under, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Where it landed, or null when there is no such service.</returns>
    /// <remarks>
    /// <b>A group holds no data, so creating one is metadata and nothing else.</b>
    /// It cannot fail partway: there is no table to make and none to clean up if
    /// the catalogue write is refused.
    /// </remarks>
    Task<GroupLayerAddress?> CreateGroupLayerAsync(
        string? folder,
        string serviceName,
        string name,
        int? parentLayerIndex,
        CancellationToken cancellationToken);

    /// <summary>Lists published layers.</summary>
    Task<IReadOnlyList<AdminLayer>> ListLayersAsync(CancellationToken cancellationToken);

    /// <summary>Finds a service and the layer names a style may draw.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The service, or null.</returns>
    Task<StyledService?> FindServiceForStyleAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a style against a service, or clears it.
    /// </summary>
    /// <remarks>
    /// <b>Validated before it gets here.</b> This writes what it is given; the
    /// checks live in <c>StyleDocument</c> where the caller can be told which
    /// line is wrong.
    /// </remarks>
    /// <param name="name">The service name.</param>
    /// <param name="style">The document, or null to go back to the default.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a service was found and written.</returns>
    Task<bool> SetStyleAsync(string name, string? style, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every service that holds nothing, and reports which.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-54's cheaper half, and the half that needs no new fact.</b> Publishing a
    /// layer creates the service that holds it, and unpublishing the last layer leaves
    /// that service behind — so an estate accumulates empty containers advertised in
    /// the directory as feature services with no layers. The row rejected automatic
    /// removal for a good reason: a service created by a publish and one created
    /// deliberately by <c>POST /admin/featureservices</c> are the same row, and nothing
    /// records which, so deleting on unpublish would either take away a container
    /// somebody made on purpose or need a column that exists to support a convenience.
    /// </para>
    /// <para>
    /// <b>An operator-initiated sweep guesses about neither.</b> It says what it will
    /// remove, the operator decides, and a container somebody meant to keep is kept by
    /// not pressing the button. That is why the names come back rather than a count.
    /// </para>
    /// <para>
    /// <b>The geometry service cannot be swept, and not because this remembers to
    /// exclude it.</b> It holds no layers by design, so a sweep that counted layers
    /// would be exactly wrong about it — but it lives in <c>system_service</c>, a
    /// different table, and this deletes from <c>service</c>. The safety is structural
    /// rather than a condition somebody has to keep writing, which is the difference
    /// between a rule and a habit.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The qualified names of the services removed, in order.</returns>
    Task<IReadOnlyList<string>> SweepEmptyServicesAsync(CancellationToken cancellationToken);

    /// <summary>Finds a layer and the symbology stored against it.</summary>
    /// <param name="name">The layer name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The layer, or null.</returns>
    Task<SymbolisedLayer?> FindLayerForSymbologyAsync(
        string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a canonical symbology document against a layer, or clears it.
    /// </summary>
    /// <remarks>
    /// <b>Normalised and validated before it gets here</b>, by
    /// <c>SymbologyConversion.Read</c> — which is also where the losses are collected,
    /// because the caller is the only one who can be told about them. This writes what
    /// it is given, byte for byte, so that what comes back out is what went in.
    /// </remarks>
    /// <param name="name">The layer name.</param>
    /// <param name="canonical">The document, or null for the generated appearance.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a layer was found and written.</returns>
    Task<bool> SetSymbologyAsync(
        string name, string? canonical, CancellationToken cancellationToken);

    /// <summary>
    /// Stores what a service is configured to offer — a ceiling, never a grant.
    /// </summary>
    /// <remarks>
    /// <b>Writes all four fields, including the nulls.</b> ADR-031 makes null mean
    /// *unset*, so a partial write would leave a service in a state the caller did
    /// not ask for and could not see: an operator clearing the ceiling expects the
    /// column cleared, not left as it was. The whole set is replaced.
    /// </remarks>
    /// <param name="name">The service name within its folder.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="limits">The configuration to store.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when a service was found and written.</returns>
    Task<bool> SetServiceCapabilitiesAsync(
        string name,
        string? folder,
        ServiceCapabilityLimits limits,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads back what a service is configured to offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-17, because the write had no read and the console was
    /// pretending otherwise.</b> Every settings control was drawn from nothing and
    /// then explained itself in prose; the one thing an operator needs to know before
    /// changing a ceiling is what the ceiling is now. There was no route that could
    /// say, so the console asked <c>GET …/capabilities</c>, got <c>405</c>, and
    /// reported it in a corner.
    /// </para>
    /// <para>
    /// Null is returned for a service that does not exist, which is different from a
    /// service with nothing configured — that one answers with every field unset, and
    /// the distinction is what lets the caller tell "not found" from "no ceiling".
    /// </para>
    /// </remarks>
    /// <param name="name">The service name within its folder.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What is stored, or null when there is no such service.</returns>
    Task<ServiceCapabilityLimits?> FindServiceCapabilitiesAsync(
        string name,
        string? folder,
        CancellationToken cancellationToken);

    /// <summary>Every folder, from the register and from what services point at.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The folders, ordered by name.</returns>
    Task<IReadOnlyList<AdminFolder>> ListFoldersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Makes a folder, so something can be published into it later.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent by name, case-insensitively</b>, because that is how every folder
    /// lookup resolves and because a publish that creates its folder on the way must not
    /// race a person creating the same one. The answer says whether this call made it.
    /// </remarks>
    /// <param name="name">What to call it.</param>
    /// <param name="owner">Who made it, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when this call created it; false when it was already there.</returns>
    Task<bool> CreateFolderAsync(string name, Guid? owner, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a folder, but only while nothing is in it.
    /// </summary>
    /// <remarks>
    /// <b>Both tables count.</b> A folder holding only the geometry service looks empty to
    /// a query over <c>service</c> alone, and deleting it would take the folder out from
    /// under a URL that still answers. The same argument as a service that holds only a
    /// group layer.
    /// </remarks>
    /// <param name="name">The folder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and what is in the way if it was refused.</returns>
    Task<(Removal Outcome, int Services, int SystemServices)> DeleteFolderAsync(
        string name,
        CancellationToken cancellationToken);

    /// <summary>Every feature service, with what each one holds.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services, ordered by folder then name.</returns>
    Task<IReadOnlyList<AdminService>> ListServicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a service, but only while it is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused while it holds layers or groups, rather than cascading.</b> A cascade
    /// here would unpublish layers as a side effect of a call about their container —
    /// and unpublishing has consequences of its own: it purges tiles and forgets a
    /// remembered shape. An operator who wants that should ask for it per layer, where
    /// the response tells them the table was not touched.
    /// </para>
    /// <para>
    /// The common case is the empty one, and it is why this exists: publishing creates
    /// a service implicitly and unpublishing the last layer leaves it behind, so a
    /// working estate accumulates empty services that nothing could remove
    /// (<see href="../../../docs/architecture-debt.md">D-48</see>).
    /// </para>
    /// </remarks>
    /// <param name="name">The service name within its folder.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and what it still holds if it was refused.</returns>
    Task<(Removal Outcome, int Layers, int Groups)> DeleteServiceAsync(
        string name,
        string? folder,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a group layer, but only while nothing is nested under it.
    /// </summary>
    /// <remarks>
    /// <b>Children are not reparented, and that is the same argument as above.</b>
    /// Moving a layer to the root as a side effect of deleting the group above it
    /// changes where a saved web map finds it, silently. The refusal names how many
    /// children there are so the operator can move them deliberately.
    /// </remarks>
    /// <param name="name">The service name.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="index">The group's layer index — the number in the URL.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and how many children if it was refused.</returns>
    Task<(Removal Outcome, int Children)> DeleteGroupLayerAsync(
        string name,
        string? folder,
        int index,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets a service's sharing scope, and says what it was.
    /// </summary>
    /// <param name="serviceName">The service.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The scope it had, or null when there is no such service.</returns>
    /// <remarks>
    /// <para>
    /// <b>The service is the thing that has a sharing scope, and until 2026-08-18 there was no way
    /// to say so.</b> <see cref="SetSharingAsync"/> takes a <em>layer</em> name and writes the
    /// owning service's scope — correct since migration 11, and reachable only through a layer.
    /// So a service with three layers had three routes to one setting, which is
    /// <see href="../architecture-debt.md">D-61</see>'s shape; and a service with **no** layers had
    /// none at all. Server creates empty services, so Server could create a private thing whose
    /// scope nothing could change. Found by trying it.
    /// </para>
    /// <para>
    /// <b>Folder-qualified, because a name is not unique.</b> Two folders may each hold a service
    /// called <c>parcels</c>, and the layer route sidestepped that only because layer names are
    /// unique. This one cannot.
    /// </para>
    /// </remarks>
    Task<SharingScope?> SetServiceSharingAsync(
        string serviceName,
        string? folder,
        SharingScope sharing,
        CancellationToken cancellationToken);

    /// <summary>Changes a service's sharing scope, addressed by one of its layers.</summary>
    /// <param name="layerName">A layer in the service.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The layer as it was before, or null if there is no such layer.</returns>
    Task<AdminLayer?> SetSharingAsync(
        string layerName, SharingScope sharing, CancellationToken cancellationToken);

    /// <summary>Starts or stops a service (ADR-020 §3).</summary>
    /// <returns>The status it had before, or null if there is no such layer.</returns>
    Task<ServiceStatus?> SetStatusAsync(
        string layerName, ServiceStatus status, CancellationToken cancellationToken);

    /// <summary>Removes a published layer. The data is untouched.</summary>
    /// <param name="layerId">
    /// Which layer, by catalogue id rather than by name.
    /// <b>[D-109](../../../docs/architecture-debt.md): a bare layer name is not unique</b>, and
    /// this statement used to delete every row carrying it while the caller purged the tiles of
    /// one. Every caller holds the layer already.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether a layer was removed.</returns>
    Task<bool> UnpublishLayerAsync(Guid layerId, CancellationToken cancellationToken);
}
