using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

/// <summary>A group layer: a name with layers under it, and no data of its own.</summary>
/// <param name="Id">Its catalogue identity.</param>
/// <param name="Index">Its number within the service — the URL segment.</param>
/// <param name="Name">What it is called.</param>
/// <param name="ParentIndex">The group above it, or null at the top.</param>
/// <remarks>
/// <b>It holds no data, which is why it is not a <see cref="PublishedLayer"/>.</b>
/// A group layer is organisation: it has no table, no geometry, no fields and
/// nothing to query. Modelling it as a layer with all of that nulled out would
/// make five guaranteed columns optional everywhere, to store a name and a
/// parent.
/// </remarks>
public readonly record struct GroupLayer(Guid Id, int Index, string Name, int? ParentIndex);

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
    /// <param name="groups">Its group layers, in any order.</param>
    /// <param name="style">
    /// The cartographic style somebody wrote for it, or null to keep the
    /// generated default. Held as the text it was stored as, because a style is
    /// a document an author reads again and normalising it would return
    /// something they did not write.
    /// </param>
    /// <param name="limits">
    /// What this service has been configured to offer, or null for nothing
    /// configured — which is the same as <see cref="ServiceCapabilityLimits.Unset"/>
    /// and is how every service behaved before ADR-031.
    /// </param>
    /// <param name="sharedWith">
    /// Which groups this service is shared with — ADR-036. Empty unless the scope is `group`.
    /// </param>
    /// <param name="srid">The reference to serve in, or null for each layer's own.</param>
    public PublishedService(
        Guid id,
        string name,
        string? folder,
        string kind,
        string? description,
        Guid? owner,
        SharingScope sharing,
        ServiceStatus status,
        IEnumerable<PublishedLayer> layers,
        IEnumerable<GroupLayer>? groups = null,
        string? style = null,
        ServiceCapabilityLimits? limits = null,
        IEnumerable<Guid>? sharedWith = null,
        int? srid = null)
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
        SharedWith = sharedWith is null ? [] : [.. sharedWith];
        Status = status;
        Srid = srid;
        Style = style;
        Layers = [.. layers.OrderBy(l => l.LayerIndex)];
        Groups = [.. (groups ?? []).OrderBy(g => g.Index)];
        Limits = limits ?? ServiceCapabilityLimits.Unset;
    }

    /// <summary>The catalogue identity.</summary>
    public Guid Id { get; }

    /// <summary>
    /// What this service is configured to offer — a ceiling, never a grant.
    /// </summary>
    /// <remarks>
    /// Never null: an unconfigured service carries
    /// <see cref="ServiceCapabilityLimits.Unset"/>, so no caller has to decide what
    /// absent means. ADR-031.
    /// </remarks>
    public ServiceCapabilityLimits Limits { get; }

    /// <summary>
    /// The stored style, or null when this service still uses the generated one.
    /// </summary>
    /// <remarks>
    /// <b>Text rather than a parsed document.</b> Nothing in the serving path
    /// needs to look inside it — it was checked when it was written — and
    /// reparsing it per request to serialise it again would be work whose only
    /// effect is to reformat somebody's file.
    /// </remarks>
    public string? Style { get; }

    /// <summary>Its name within its folder.</summary>
    public string Name { get; }

    /// <summary>Its folder, or null for the root.</summary>
    public string? Folder { get; }

    /// <summary>The ArcGIS service type — <c>FeatureServer</c> in v1.</summary>
    public string Kind { get; }

    /// <summary>The reference this service is served in, or null for each layer's own.</summary>
    /// <remarks>
    /// <b>Null is the meaning, not a missing value.</b> A service composed before
    /// [ADR-057](../../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5c — and any
    /// service whose author never chose — answers in whatever its tables are stored in, which is
    /// what every service did before migration 39. A number says *serve in this whatever the
    /// table says*, and the reprojection is the one the query and drawing paths already do.
    /// </remarks>
    public int? Srid { get; }

    /// <summary>
    /// The written reference this service is served in, when it named one instead of a code.
    /// </summary>
    /// <remarks>
    /// <b>Never beside <see cref="Srid"/>.</b> Migration 41's constraint refuses a row carrying
    /// both, so a service reporting two would be reporting something that cannot exist.
    /// </remarks>
    public string? SridWkt { get; init; }

    /// <summary>What it is for, or null.</summary>
    public string? Description { get; }

    /// <summary>Who owns it, or null.</summary>
    public Guid? Owner { get; }

    /// <summary>Who may read it (ADR-018 §3b).</summary>
    public SharingScope Sharing { get; }

    /// <summary>
    /// Which groups this service is shared with — ADR-036, and empty unless the scope is `group`.
    /// </summary>
    /// <remarks>
    /// <b>`SharedWith` rather than `Groups`, because this class already has a `Groups`.</b> That one
    /// is group *layers* — the folders inside a service document — and the collision is the same
    /// ambiguity ADR-015 §6c had to settle in the owner's word *grup*. A property whose name means
    /// two things in one class is a property somebody reads wrong once and then trusts.
    /// </remarks>
    public ImmutableArray<Guid> SharedWith { get; }

    /// <summary>Whether it is served (ADR-020 §3).</summary>
    public ServiceStatus Status { get; }

    /// <summary>Its layers, ordered by index.</summary>
    public IReadOnlyList<PublishedLayer> Layers { get; }

    /// <summary>Its group layers, ordered by index.</summary>
    public IReadOnlyList<GroupLayer> Groups { get; }

    /// <summary>The group at this index, or null.</summary>
    /// <param name="index">The number from the URL.</param>
    /// <returns>The group, or null if the index is not a group.</returns>
    public GroupLayer? Group(int index)
    {
        foreach (GroupLayer group in Groups)
        {
            if (group.Index == index)
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>
    /// The indices directly under a group, or under the root when null.
    /// </summary>
    /// <param name="parent">The group's index, or null for the top level.</param>
    /// <returns>The children, in index order.</returns>
    /// <remarks>
    /// <b>Direct children only.</b> ArcGIS's <c>subLayerIds</c> is one level: a
    /// nested group appears in its parent's list, and its own children appear in
    /// its own. Flattening the tree here would list a grandchild twice and draw
    /// it under both.
    /// </remarks>
    public IReadOnlyList<int> ChildrenOf(int? parent)
    {
        List<int> children = [];

        foreach (GroupLayer group in Groups)
        {
            if (group.ParentIndex == parent)
            {
                children.Add(group.Index);
            }
        }

        foreach (PublishedLayer layer in Layers)
        {
            if (layer.ParentIndex == parent)
            {
                children.Add(layer.LayerIndex);
            }
        }

        children.Sort();
        return children;
    }

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
