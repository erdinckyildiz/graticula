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
    DateTimeOffset? Until = null)
{
    /// <summary>
    /// The reference systems this collection can be asked for.
    /// </summary>
    /// <remarks>
    /// <b>CRS84 first, because the first entry is the default and GeoJSON is
    /// longitude first.</b> A collection whose list began with its storage CRS would
    /// hand a client latitude-first coordinates in a format that has no way to say
    /// so.
    /// </remarks>
    public IReadOnlyList<string> CoordinateSystems
    {
        get
        {
            List<string> systems = [OgcNames.Crs84, OgcNames.CrsUri(AxisOrder.Wgs84)];

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
    public string StorageCrs => OgcNames.CrsUri(Srid);

    /// <summary>Whether this collection has a time dimension.</summary>
    public bool IsTemporal => TemporalField is { Length: > 0 };
}
