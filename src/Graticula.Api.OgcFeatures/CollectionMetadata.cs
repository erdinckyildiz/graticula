using System;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// One published layer, as OGC API Features sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter's own view, built at the edge</b>, the same shape
/// <c>WfsFeatureType</c> and <c>WmsLayer</c> have and for the same reason: the host
/// reads the catalogue and applies sharing, and nothing in this project knows what a
/// catalogue is.
/// </para>
/// <para>
/// <b>The id is the layer's name, unqualified — the fourth face to use it.</b> A
/// WFS <c>typeName</c>, a WMS <c>LAYERS</c> value, a MapServer layer and an OGC
/// collection id are all the same string for the same data. An operator who learns
/// one name should not have to learn four.
/// </para>
/// </remarks>
/// <param name="Id">The collection id, which is the layer's name.</param>
/// <param name="Title">Something for a person to read.</param>
/// <param name="Description">A longer description, or null.</param>
/// <param name="Srid">The EPSG code its geometry is stored in.</param>
/// <param name="GeometryType">What shape its features are.</param>
/// <param name="Extent">Where its features are, in WGS 84 longitude/latitude, or null.</param>
/// <param name="Fields">Its attribute columns, geometry excluded.</param>
/// <param name="TemporalField">The column carrying its time, or null when it has none.</param>
/// <param name="From">Its earliest instant, or null.</param>
/// <param name="Until">Its latest instant, or null.</param>
/// <param name="ServedSrid">
/// The reference the service publishing this layer names, or null when it names none.
/// <b>Not <paramref name="Srid"/>, and the difference is the whole of
/// [D-229](../../docs/architecture-debt.md).</b> A service may choose a reference its
/// tables are not stored in; that choice reaches this face as something a client may
/// <em>ask for</em> rather than as what it gets by default, for the reason
/// <see cref="CoordinateSystems"/> gives.
/// </param>
public sealed record CollectionMetadata(
    string Id,
    string Title,
    string? Description,
    int Srid,
    GeometryKind GeometryType,
    Envelope? Extent,
    IReadOnlyList<FieldDescription> Fields,
    string? TemporalField = null,
    DateTimeOffset? From = null,
    DateTimeOffset? Until = null,
    int? ServedSrid = null)
{
    /// <summary>
    /// The reference systems this collection can be asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CRS84 first, because the first entry is the default and GeoJSON is
    /// longitude first.</b> A collection whose list began with its storage CRS would
    /// hand a client latitude-first coordinates in a format that has no way to say
    /// so.
    /// </para>
    /// <para>
    /// <b>The service's own reference is in the list and is not the default</b>, which
    /// is the shape [D-229](../../docs/architecture-debt.md) takes on this face and is
    /// different from the shape it takes on WFS. There, the owner's decision of
    /// 2026-09-09 — <em>wms ve wfs map'in projeksiyonunda yayınlanacak</em> — makes the
    /// service's reference the <c>DefaultCRS</c>. Here it cannot be: OGC API Features
    /// publishes GeoJSON, whose default is CRS84 by the specification rather than by
    /// this server's preference, and a face that answered a bare <c>/items</c> in
    /// EPSG:5253 would be non-conforming. <b>So this is a <em>no</em> to the default
    /// and a <em>yes</em> to the capability</b>: before this, a service could name a
    /// reference, publish it on two faces, and this one would refuse
    /// <c>crs=…/EPSG/0/5253</c> as *not one of this collection's reference systems* —
    /// measured 2026-09-09. Advertising it is what makes the choice reachable at all.
    /// </para>
    /// <para>
    /// <b><see cref="StorageCrs"/> is left alone deliberately.</b> Part 2 defines it as
    /// the reference the data is <em>stored</em> in, so the table's is the right answer
    /// and moving it would be a false statement about the database rather than a
    /// repair. The two fields answer different questions and this is the one place in
    /// the server where they can differ.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CoordinateSystems
    {
        get
        {
            List<string> systems = [OgcNames.Crs84, OgcNames.CrsUri(AxisOrder.Wgs84)];

            if (ServedSrid is { } served && !systems.Contains(OgcNames.CrsUri(served)))
            {
                systems.Add(OgcNames.CrsUri(served));
            }

            string storage = OgcNames.CrsUri(Srid);

            if (!systems.Contains(storage))
            {
                systems.Add(storage);
            }

            // Web Mercator, because a browser client asks for it and PostGIS
            // transforms to it for nothing.
            const int WebMercator = 3857;

            if (Srid != WebMercator)
            {
                systems.Add(OgcNames.CrsUri(WebMercator));
            }

            return systems;
        }
    }

    /// <summary>The CRS the data is stored in, which Part 2 requires be published.</summary>
    /// <remarks>
    /// <b>The table's, even when the service names another.</b> Part 2's word is
    /// <em>storage</em>; a service's chosen reference is in
    /// <see cref="CoordinateSystems"/> instead, where a client can ask for it.
    /// </remarks>
    public string StorageCrs => OgcNames.CrsUri(Srid);

    /// <summary>Whether this collection has a time dimension.</summary>
    public bool IsTemporal => TemporalField is { Length: > 0 };
}
