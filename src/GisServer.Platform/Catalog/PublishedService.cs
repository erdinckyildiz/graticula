using System;
using System.Collections.Generic;
using System.Linq;
using GisServer.Platform.Identity;

namespace GisServer.Platform.Catalog;

/// <summary>
/// A service: a named, shared, startable container of layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner correction, 2026-08-15: "a service is a combination of layers
/// actually. so multiple layers can be shown as a service."</b> Before this
/// type, one published layer <em>was</em> one service, and the assumption was
/// visible in the URLs — every route ended in <c>/0</c> because there could
/// never be a layer 1.
/// </para>
/// <para>
/// <b>This is where sharing, status, folder and owner live, and only here.</b>
/// A service holding three layers with three different sharing scopes has no
/// answer to <em>who may see this service</em>, so the question has to be asked
/// of the container. <see cref="PublishedLayer"/> still exposes
/// <c>Sharing</c> and <c>Status</c>, and they are its service's — copied on
/// read, never stored twice.
/// </para>
/// <para>
/// <b>Layers are ordered by their index, which is the number in the URL.</b>
/// Not by name: <c>/FeatureServer/2</c> must keep meaning the same layer after
/// somebody renames layer 0, or every saved web map breaks quietly.
/// </para>
/// </remarks>
public sealed class PublishedService
{
    /// <summary>Creates a service.</summary>
    /// <param name="id">Its catalogue identity.</param>
    /// <param name="name">Its name within its folder.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="kind">The ArcGIS service type.</param>
    /// <param name="description">What it is for, or null.</param>
    /// <param name="owner">Who owns it, or null.</param>
    /// <param name="sharing">Who may read it.</param>
    /// <param name="status">Whether it is served.</param>
    /// <param name="layers">Its layers, in any order.</param>
    public PublishedService(
        Guid id,
        string name,
        string? folder,
        string kind,
        string? description,
        Guid? owner,
        SharingScope sharing,
        ServiceStatus status,
        IEnumerable<PublishedLayer> layers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(layers);

        Id = id;
        Name = name;
        Folder = folder;
        Kind = kind;
        Description = description;
        Owner = owner;
        Sharing = sharing;
        Status = status;
        Layers = [.. layers.OrderBy(l => l.LayerIndex)];
    }

    /// <summary>The catalogue identity.</summary>
    public Guid Id { get; }

    /// <summary>Its name within its folder.</summary>
    public string Name { get; }

    /// <summary>Its folder, or null for the root.</summary>
    public string? Folder { get; }

    /// <summary>The ArcGIS service type — <c>FeatureServer</c> in v1.</summary>
    public string Kind { get; }

    /// <summary>What it is for, or null.</summary>
    public string? Description { get; }

    /// <summary>Who owns it, or null.</summary>
    public Guid? Owner { get; }

    /// <summary>Who may read it (ADR-018 §3b).</summary>
    public SharingScope Sharing { get; }

    /// <summary>Whether it is served (ADR-020 §3).</summary>
    public ServiceStatus Status { get; }

    /// <summary>Its layers, ordered by index.</summary>
    public IReadOnlyList<PublishedLayer> Layers { get; }

    /// <summary>Whether requests for it should be served.</summary>
    public bool IsRunning => Status == ServiceStatus.Started;

    /// <summary>The address a client builds, without the type segment.</summary>
    public string QualifiedName => Folder is null ? Name : $"{Folder}/{Name}";

    /// <summary>The layer with this index, or null.</summary>
    /// <param name="index">The number from the URL.</param>
    /// <returns>The layer, or null if the service has no such layer.</returns>
    public PublishedLayer? Layer(int index)
    {
        foreach (PublishedLayer layer in Layers)
        {
            if (layer.LayerIndex == index)
            {
                return layer;
            }
        }

        return null;
    }
}
