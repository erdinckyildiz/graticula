using System;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// One published layer, as WFS sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter's own view, built at the edge.</b> Nothing here is a catalogue
/// type: the host reads the catalogue, applies sharing, and hands this in. That
/// keeps the WFS writers testable without a database and keeps `Graticula.Core`
/// free of WFS vocabulary, which is ADR-005 §3.3's placement made concrete.
/// </para>
/// <para>
/// <b>Identity is a string and that is the point.</b> [Q-57](../../../docs/open-questions.md)
/// recorded that ArcGIS FeatureServer needs a unique *integer* object id, so a
/// registered table keyed by uuid or text is not servable through that surface.
/// WFS has no such requirement — a <c>gml:id</c> is an XML ID — so those tables
/// are servable here, and this is the first surface on which they are.
/// </para>
/// <para>
/// <b>The folder is a title, not a namespace.</b> It was a namespace prefix until
/// the OGC conformance suite was pointed at this server; see
/// <see cref="WfsNames.Prefix"/> for what that cost and why it changed.
/// </para>
/// </remarks>
/// <param name="Name">The layer name, unique across this server.</param>
/// <param name="Title">Something for a person to read in a layer list.</param>
/// <param name="Abstract">A longer description, or null.</param>
/// <param name="Srid">The EPSG code the layer's geometry is stored in.</param>
/// <param name="GeometryType">What shape its features are.</param>
/// <param name="GeometryProperty">What the geometry property is called in the schema.</param>
/// <param name="Fields">Its attribute columns, geometry excluded.</param>
/// <param name="Extent">Where its features are, or null when that is unknown.</param>
/// <param name="Geographic">
/// Its extent in WGS 84, longitude first, or null when there is none to publish.
/// <b>Separate from <paramref name="Extent"/> because they are different
/// numbers.</b> A layer in a national grid has both, and
/// <c>ows:WGS84BoundingBox</c> is defined in terms of the second whatever the
/// first is. Writing the layer's own numbers under a WGS 84 label is the defect
/// this parameter exists to make impossible.
/// </param>
public sealed record WfsFeatureType(
    string Name,
    string Title,
    string? Abstract,
    int Srid,
    GeometryKind GeometryType,
    string GeometryProperty,
    IReadOnlyList<FieldDescription> Fields,
    Envelope? Extent,
    Envelope? Geographic = null)
{
    /// <summary>The prefix this server publishes every feature type under.</summary>
    public static string Prefix => WfsNames.Prefix;

    /// <summary>The XML namespace every feature type lives in.</summary>
    public static string Namespace => WfsNames.Namespace;

    /// <summary>The qualified name a client asks for, such as <c>graticula:tr_yol</c>.</summary>
    public string QualifiedName => $"{WfsNames.Prefix}:{Name}";

    /// <summary>The <c>gml:id</c> for one of its features.</summary>
    /// <remarks>
    /// <b>Prefixed with the type, because a <c>gml:id</c> is unique per
    /// document</b> and one response may carry more than one feature type. It is
    /// also what <c>GetFeatureById</c> takes back, so the two must agree — the
    /// stored query splits on the last dot for exactly this reason.
    /// </remarks>
    /// <param name="id">The feature's declared identity.</param>
    /// <returns>The identifier.</returns>
    public string GmlIdOf(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return $"{Name}.{id}";
    }

    /// <summary>
    /// Splits a <c>gml:id</c> back into a type name and a feature identity.
    /// </summary>
    /// <remarks>
    /// <b>The last dot, not the first.</b> A layer name may contain a dot and so
    /// may an identity; splitting on the first would make <c>a.b.1</c> mean the
    /// layer <c>a</c> and the feature <c>b.1</c>. Splitting on the last is right
    /// whenever the identity has no dot, which is true of every integer, every
    /// uuid and almost every key anybody chooses. Where it is not, the id is
    /// simply not found — a wrong answer is the one outcome this must not have.
    /// </remarks>
    /// <param name="resourceId">The identifier a client sent.</param>
    /// <param name="typeName">The layer name it names.</param>
    /// <param name="id">The feature identity it names.</param>
    /// <returns>Whether it split.</returns>
    public static bool TrySplitResourceId(
        string? resourceId,
        out string typeName,
        out string id)
    {
        typeName = string.Empty;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return false;
        }

        int dot = resourceId.LastIndexOf('.');

        if (dot <= 0 || dot == resourceId.Length - 1)
        {
            return false;
        }

        typeName = resourceId[..dot];
        id = resourceId[(dot + 1)..];
        return true;
    }
}
