using System;
using System.Collections.Generic;

namespace Graticula.Platform.Admin;

/// <summary>
/// A whole service, described before any of it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-057](../../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5a and §5h.</b>
/// The composition <i>is</i> the service: its name is the service name, its order is the layer
/// order, and index 0 is drawn on top. Publishing it creates the service and everything in it
/// in one act, because the owner's rule is that a service is not created without layers — so
/// there is no empty container to make first and no sequence to remember.
/// </para>
/// <para>
/// <b>One index space, and that is why the tree is flattened here rather than in SQL.</b> A
/// group and a layer both occupy a numbered slot in the service — `subLayerIds` addresses one
/// list — so the numbering has to be decided once, over the whole tree, before anything is
/// inserted. Doing it in the order the operator built means the number a client sees is the
/// position they put the thing in.
/// </para>
/// <para>
/// <b>One level deep, per §5b.</b> A group holds layers; a group does not hold a group. The
/// schema allows deeper — migration 12 has done since the beginning — and this is the screen's
/// rule rather than the database's, which is the right way round: relaxing it later is a screen
/// change.
/// </para>
/// </remarks>
/// <param name="Name">What the service is called.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="Description">What somebody finding it in a directory needs to know.</param>
/// <param name="Sharing">Who may see it, in the catalogue's spelling.</param>
/// <param name="Srid">The reference to serve in, or null for each layer's own.</param>
/// <param name="SridWkt">
/// The reference written out, for one EPSG has no code for — owner decision 2026-09-06,
/// <i>"epsg güzel ama wkt de kabul etmemiz lazım"</i>. Never set beside <c>Srid</c>: migration
/// 41 refuses a service row carrying both.
/// </param>
/// <param name="Nodes">The tree, in draw order — the first is drawn on top.</param>
/// <param name="ServesFeatures">Whether the feature face answers, or null for the default.</param>
/// <param name="ServesTiles">Whether the tile face answers, or null for the default.</param>
/// <param name="Capabilities">
/// The capability ceiling in ArcGIS's spelling, or null to leave it unset.
/// <para>
/// <b>§5g, and it is a ceiling rather than a grant.</b> What a caller may actually do is the
/// intersection of this, what their privileges carry and what the data supports — the same
/// three-way rule <see cref="Graticula.Platform.Catalog.ServiceCapabilityLimits"/> states. So
/// setting it here can only narrow, and a composition that says nothing about it publishes a
/// service that offers whatever the caller and the data allow, which is what every service did
/// before this field existed.
/// </para>
/// </param>
/// <param name="Replacing">
/// The service already at this address that this composition replaces, or null to create one.
/// <para>
/// <b>§5e, and the id is reused rather than a new one allocated.</b> A service <i>is</i> its
/// item — there is no item table, so the id a person shared, bookmarked or wrote into a client is
/// this row's id. Replacing by delete-then-create would keep the URL and break every one of
/// those, which is the opposite of what *replace* means to whoever pressed it. So the row stays,
/// its contents are rewritten, and <c>created_at</c> keeps saying when this address was first
/// published while <c>updated_at</c> moves.
/// </para>
/// <para>
/// <b>The layers do not survive it.</b> They are deleted and the new composition's are inserted,
/// so every layer gets a new id — which is what republishing has always done
/// ([D-34](../../../docs/architecture-debt.md)) and what makes the old service's cached tiles
/// unreachable rather than wrong.
/// </para>
/// </param>
public sealed record ServiceComposition(
    string Name,
    string? Folder,
    string? Description,
    string Sharing,
    int? Srid,
    IReadOnlyList<CompositionNode> Nodes,
    string? SridWkt = null,
    bool? ServesFeatures = null,
    bool? ServesTiles = null,
    IReadOnlyList<string>? Capabilities = null,
    Guid? Replacing = null);

/// <summary>One entry in a composition: a group, or a layer.</summary>
/// <remarks>
/// <b>One type rather than two, because the order is one order.</b> Groups and layers interleave
/// in the tree and share the index space; two lists would need a third thing saying how they
/// weave together, which is the same information written twice.
/// </remarks>
/// <param name="GroupName">The group's name, when this is a group.</param>
/// <param name="Layer">What to publish, when this is a layer.</param>
/// <param name="Children">A group's layers, in draw order. Empty for a layer.</param>
public sealed record CompositionNode(
    string? GroupName,
    LayerPublication? Layer,
    IReadOnlyList<LayerPublication>? Children = null)
{
    /// <summary>Whether this node is a group rather than a layer.</summary>
    public bool IsGroup => GroupName is { Length: > 0 };
}

/// <summary>What a published composition became.</summary>
/// <param name="ServiceId">The service's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="Layers">Each layer's name and the index it answers at.</param>
/// <param name="Groups">Each group's name and the index it answers at.</param>
/// <param name="ReplacedLayers">
/// The ids of the layers this composition replaced, empty when it created a service.
/// <para>
/// <b>Carried out of the transaction because the caches live outside it.</b> A replaced layer's
/// tiles are keyed by its id, so they become unreachable the moment the row goes and would sit in
/// the cache for as long as the deployment lives. The endpoint purges them, which is the same
/// thing the unpublish and delete paths do with the same two calls — and it can only do it if it
/// is told which ids went.
/// </para>
/// </param>
public sealed record PublishedComposition(
    Guid ServiceId,
    string Name,
    string? Folder,
    IReadOnlyList<(string Name, int Index)> Layers,
    IReadOnlyList<(string Name, int Index)> Groups,
    IReadOnlyList<Guid>? ReplacedLayers = null);
