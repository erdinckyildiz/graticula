using System;
using System.Collections.Generic;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

/// <summary>
/// A registered coverage, and the service that publishes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="PublishedLayer"/> rather than inside it</b>
/// ([ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §4). A
/// layer here is a PostGIS table with a geometry column and an identity column, and
/// seven protocol faces read those and rely on them being there. Widening that type to
/// hold a file on a disk would make every one of them ask whether this layer is the
/// kind it can serve, in seven places, and the seventh would be the one that forgot.
/// </para>
/// <para>
/// <b>What it shares is everything about authorisation.</b> The sharing scope, the
/// status, the owner and the folder come from the same <c>service</c> row that governs
/// a feature service, so a private coverage is private by the mechanism that is
/// already tested rather than by a second one. The security gate's headline result on
/// 2026-08-20 was that the sharing model held across five new faces; it holds across
/// this one for the same reason, which is that there is nothing new to hold.
/// </para>
/// <para>
/// <b>Everything here was read at registration and is stored.</b> ADR-043 §3.3
/// registers imagery in place, so the file may be an object-storage URL — the service
/// document has to be answerable without touching it.
/// </para>
/// </remarks>
public sealed class PublishedCoverage
{
    /// <summary>Describes a published coverage.</summary>
    /// <param name="id">Its own identifier.</param>
    /// <param name="serviceId">The service that publishes it.</param>
    /// <param name="serviceName">That service's name.</param>
    /// <param name="folder">That service's folder, or null for the root.</param>
    /// <param name="name">The coverage's name.</param>
    /// <param name="path">Where the file lives, unmoved.</param>
    /// <param name="info">What registration read out of it.</param>
    /// <param name="style">The stored rendering rule, or null for the default.</param>
    /// <param name="sharing">Who may see it.</param>
    /// <param name="status">Whether the service is started.</param>
    /// <param name="owner">Who published it.</param>
    public PublishedCoverage(
        Guid id,
        Guid serviceId,
        string serviceName,
        string? folder,
        string name,
        string path,
        CoverageInfo info,
        string? style,
        SharingScope sharing,
        ServiceStatus status,
        Guid? owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(info);

        Id = id;
        ServiceId = serviceId;
        ServiceName = serviceName;
        Folder = folder;
        Name = name;
        Path = path;
        Info = info;
        Style = style;
        Sharing = sharing;
        Status = status;
        Owner = owner;
    }

    /// <summary>Its own identifier.</summary>
    public Guid Id { get; }

    /// <summary>The service that publishes it.</summary>
    public Guid ServiceId { get; }

    /// <summary>That service's name.</summary>
    public string ServiceName { get; }

    /// <summary>That service's folder, or null for the root.</summary>
    public string? Folder { get; }

    /// <summary>The coverage's name.</summary>
    public string Name { get; }

    /// <summary>
    /// Where the file lives, unmoved.
    /// </summary>
    /// <remarks>
    /// <b>Never returned to a caller.</b> It is a path on the server's own filesystem
    /// or a URL with a credential in front of it, and either way it says more about
    /// this deployment than any client is owed. ADR-043 §3.3's proxy exists so that the
    /// bytes travel through here; handing out the location would make that proxy
    /// optional in practice while remaining mandatory in the design.
    /// </remarks>
    public string Path { get; }

    /// <summary>What registration read out of the file.</summary>
    public CoverageInfo Info { get; }

    /// <summary>The stored rendering rule, or null for the default.</summary>
    public string? Style { get; }

    /// <summary>Who may see it.</summary>
    public SharingScope Sharing { get; }

    /// <summary>Whether the service is started.</summary>
    public ServiceStatus Status { get; }

    /// <summary>Who published it.</summary>
    public Guid? Owner { get; }

    /// <summary>The service's address, folder and all.</summary>
    public string QualifiedName =>
        string.IsNullOrEmpty(Folder) ? ServiceName : $"{Folder}/{ServiceName}";
}

/// <summary>
/// Reads registered coverages. Separate from the layer catalogue for the same reason
/// <see cref="PublishedCoverage"/> is separate from <see cref="PublishedLayer"/>.
/// </summary>
public interface ICoverageCatalog
{
    /// <summary>Every coverage, whatever its sharing.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The coverages.</returns>
    System.Threading.Tasks.Task<IReadOnlyList<PublishedCoverage>> ListAsync(
        System.Threading.CancellationToken cancellationToken);

    /// <summary>One coverage, by the service that publishes it.</summary>
    /// <param name="folder">The folder, or null for the root.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The coverage, or null.</returns>
    System.Threading.Tasks.Task<PublishedCoverage?> FindAsync(
        string? folder, string serviceName, System.Threading.CancellationToken cancellationToken);

    /// <summary>
    /// Registers a coverage in place, creating the service that publishes it.
    /// </summary>
    /// <param name="folder">The folder, or null for the root.</param>
    /// <param name="serviceName">What to call the service.</param>
    /// <param name="path">Where the file lives.</param>
    /// <param name="info">What was read out of it.</param>
    /// <param name="owner">Who is publishing it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The coverage as published.</returns>
    System.Threading.Tasks.Task<PublishedCoverage> RegisterAsync(
        string? folder,
        string serviceName,
        string path,
        CoverageInfo info,
        Guid? owner,
        System.Threading.CancellationToken cancellationToken);
}
