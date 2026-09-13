using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Tiles;

namespace Graticula.Providers.DuckDb;

/// <summary>
/// Builds vector tiles for a GeoParquet layer — ADR-066 §9, amended 2026-09-13.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads through the same bounded path every other GeoParquet query uses.</b> A tile is a
/// bounding-box question, and <see cref="GeoParquetFeatureSource"/> already answers one: the box
/// test, the statement timeout and <see cref="GeoParquetFeatureSource.DefaultMostMatched"/> are
/// not repeated here, they are <em>reached</em>, by asking for <see cref="SpatialRelation.EnvelopeIntersects"/>
/// against the tile's own envelope — which is the DuckDB-side equivalent of the plain <c>&amp;&amp;</c>
/// test <c>PostGisTileSource</c> runs, box against box, nothing about the shape considered yet.
/// </para>
/// <para>
/// <b>Encoding is still PostGIS's job.</b> ADR-021 decided that once and this does not reopen it:
/// rows read here travel, in the layer's own reference, to <see cref="IMvtEncoder"/> — the same
/// <c>ST_AsMVTGeom</c>/<c>ST_AsMVT</c> statement <c>PostGisTileSource</c> runs over a table, run
/// here over values sent across.
/// </para>
/// <para>
/// <b>Every row the box test matches, read in pages of <see cref="FeatureQuery.MaximumLimit"/> —
/// never the first page alone.</b> The first version of this class stopped at 50,000 rows on the
/// argument that a denser tile is unreadable ink. Measured on the showcase before it shipped, the
/// million-building layer's tile over central Istanbul matched 93,923 features at z6, 64,539 at
/// z9 and 55,182 at z10: the map would have drawn the lowest-numbered half of the city and looked
/// complete. A tile cannot say it is partial, so a cut there is silent degradation. Paging gives
/// the answer <c>PostGisTileSource</c> gives, whose query has no limit; what bounds it is what
/// bounds that one — the statement timeout — plus
/// <see cref="GeoParquetFeatureSource.DefaultMostMatched"/>, which <em>refuses</em> a box that
/// matches more than a million features rather than truncating it.
/// </para>
/// </remarks>
public sealed class GeoParquetTileSource : ITileSource
{
    private readonly GeoParquetFeatureSource _reader;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _attributes;
    private readonly IMvtEncoder _encoder;

    /// <summary>Creates a tile source over one GeoParquet layer.</summary>
    /// <param name="reader">
    /// The layer's own bounded reader — the same one a FeatureServer query over this layer would
    /// use, so a tile pays the same statement timeout and the same million-feature bound.
    /// </param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="attributes">
    /// Columns to carry into the tile as feature tags, with their types — already checked against
    /// the file's real columns, the same whitelist <c>PostGisTileSource</c> is handed.
    /// </param>
    /// <param name="encoder">Where the rows are turned into MVT bytes.</param>
    public GeoParquetTileSource(
        GeoParquetFeatureSource reader,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> attributes,
        IMvtEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(encoder);

        _reader = reader;
        _layer = layer;
        _attributes = attributes;
        _encoder = encoder;
    }

    /// <summary>How many rows one page of a tile's read holds.</summary>
    public const int PageRows = FeatureQuery.MaximumLimit;

    /// <inheritdoc/>
    public async Task<byte[]> BuildAsync(
        TileAddress address, string layerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        if (!address.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address), address.Rejection() ?? "The tile address is outside the pyramid.");
        }

        Envelope tileBox = address.WebMercatorEnvelope();
        Geometry filter = Rectangle(tileBox);

        List<Feature> features = [];

        for (int offset = 0; ; offset += PageRows)
        {
            FeatureQuery query = new(
                PageRows,
                fields: [.. _attributes.Select(a => a.Name)],
                includeGeometry: true,
                spatial: new SpatialFilter(filter, SpatialRelation.EnvelopeIntersects),
                // Every page is ordered by identity inside the reader (D-21), so consecutive
                // pages neither repeat nor skip a row.
                offset: offset,

                // <b>The tile is Web Mercator by construction; the layer need not be.</b> Setting
                // FilterSrid whenever the layer is not already 3857 routes the box through the
                // projector once, exactly as PostGisTileSource transforms its own filter box into
                // the layer's reference for the `&&` test — never the other way around, which
                // would ask the box test to compare numbers in two different units.
                filterSrid: _layer.Srid == PostGisWebMercator ? null : PostGisWebMercator);

            int read = 0;

            await foreach (Feature feature in _reader.ReadAsync(query, cancellationToken).ConfigureAwait(false))
            {
                features.Add(feature);
                read++;
            }

            if (read < PageRows)
            {
                break;
            }
        }

        if (features.Count == 0)
        {
            return [];
        }

        List<MvtRow> rows = new(features.Count);

        foreach (Feature feature in features)
        {
            if (feature.Geometry is not { IsEmpty: false } geometry)
            {
                continue;
            }

            MvtTag[] tags = new MvtTag[_attributes.Count];

            for (int i = 0; i < _attributes.Count; i++)
            {
                tags[i] = new MvtTag(_attributes[i].Name, _attributes[i].Type, feature[i]);
            }

            rows.Add(new MvtRow(geometry, tags));
        }

        if (rows.Count == 0)
        {
            return [];
        }

        return await _encoder
            .EncodeAsync(rows, address, layerName, _layer.Srid, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The reference every tile pyramid is cut on — <c>PostGisTileSource.WebMercator</c>'s value,
    /// restated so this project does not reference the PostGIS provider for one constant.
    /// </summary>
    private const int PostGisWebMercator = 3857;

    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));
}
