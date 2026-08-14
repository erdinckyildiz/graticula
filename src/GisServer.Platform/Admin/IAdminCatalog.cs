using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;

namespace GisServer.Platform.Admin;

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
    SharingScope Sharing);

/// <summary>A published layer, as the admin API sees it.</summary>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="Name">Its service name.</param>
/// <param name="DataSourceName">Which source it reads from.</param>
/// <param name="Qualified">Schema-qualified table.</param>
/// <param name="Sharing">Who may read it.</param>
/// <param name="Owner">Who owns it, or null.</param>
/// <param name="OwnerName">Their name, or null.</param>
/// <param name="ArcGisServable">Whether it has an integer object id.</param>
/// <param name="Status">Whether it runs.</param>
public readonly record struct AdminLayer(
    Guid Id,
    string Name,
    string DataSourceName,
    string Qualified,
    SharingScope Sharing,
    Guid? Owner,
    string? OwnerName,
    bool ArcGisServable,
    ServiceStatus Status);

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
    Task<Guid> PublishLayerAsync(
        LayerPublication publication, Guid owner, CancellationToken cancellationToken);

    /// <summary>Lists published layers.</summary>
    Task<IReadOnlyList<AdminLayer>> ListLayersAsync(CancellationToken cancellationToken);

    /// <summary>Changes a layer's sharing scope.</summary>
    /// <returns>The layer as it was before, or null if there is no such layer.</returns>
    Task<AdminLayer?> SetSharingAsync(
        string layerName, SharingScope sharing, CancellationToken cancellationToken);

    /// <summary>Starts or stops a service (ADR-020 §3).</summary>
    /// <returns>The status it had before, or null if there is no such layer.</returns>
    Task<ServiceStatus?> SetStatusAsync(
        string layerName, ServiceStatus status, CancellationToken cancellationToken);

    /// <summary>Removes a published layer. The data is untouched.</summary>
    /// <returns>Whether a layer was removed.</returns>
    Task<bool> UnpublishLayerAsync(string layerName, CancellationToken cancellationToken);
}
