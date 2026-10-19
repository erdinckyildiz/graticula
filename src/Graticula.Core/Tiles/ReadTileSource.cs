using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>
/// Builds vector tiles for a layer whose rows another engine reads — a GeoParquet layer (ADR-066 §9, amended
/// 2026-09-13) and a registered Oracle one (ADR-205 §5c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>GeoParquetTileSource</c> until 2026-10-20</b>, when Oracle became the second reader it fits, and moved
/// into Core with nothing changed but the reader's type and the relation asked.
/// </para>
/// <para>
/// <b>Reads through the same bounded path every other query of the layer uses.</b> A tile is a
/// bounding-box question, and the layer's <see cref="IFeatureSource"/> already answers one: the box
/// test, the statement timeout and the reader's bound on what a filter may match are
/// not repeated here, they are <em>reached</em>, by asking for <see cref="SpatialRelation.IndexIntersects"/>
/// against the tile's own envelope — the plain <c>&amp;&amp;</c> test <c>PostGisTileSource</c> runs, box
/// against box, nothing about the shape considered yet. A GeoParquet reader answers it as an exact box test, an
/// Oracle one with its spatial index (<c>SDO_FILTER</c>) alone; either way the encoder clips.
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
/// the reader's bound on what a filter matches, which <em>refuses</em> a box that
/// matches more than a million features rather than truncating it.
/// </para>
/// </remarks>
public sealed class ReadTileSource : ITileSource
{
    private readonly IFeatureSource _reader;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _attributes;
    private readonly IMvtEncoder _encoder;
    private readonly VectorTileScheme _scheme;

    /// <summary>Creates a tile source over one layer read by another engine.</summary>
    /// <param name="reader">
    /// The layer's own bounded reader — the same one a FeatureServer query over this layer would
    /// use, so a tile pays the same statement timeout and the same million-feature bound.
    /// </param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="attributes">
    /// Columns to carry into the tile as feature tags, with their types — already checked against
    /// the source's real columns, the same whitelist <c>PostGisTileSource</c> is handed.
    /// </param>
    /// <param name="encoder">Where the rows are turned into MVT bytes.</param>
    /// <param name="scheme">
    /// The grid the service is cut on, or null for Web Mercator — ADR-096. <b>Supported, because the
    /// read shares the envelope</b>: the box test is asked in the scheme's own reference and the encoder is
    /// handed the scheme, so such a layer tiles on a TUREF grid exactly as a table does.
    /// </param>
    public ReadTileSource(
        IFeatureSource reader,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> attributes,
        IMvtEncoder encoder,
        VectorTileScheme? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(encoder);

        _reader = reader;
        _layer = layer;
        _attributes = attributes;
        _encoder = encoder;
        _scheme = scheme ?? VectorTileScheme.WebMercator;
    }

    /// <summary>How many rows one page of a tile's read holds.</summary>
    public const int PageRows = FeatureQuery.MaximumLimit;

    /// <inheritdoc/>
    public async Task<byte[]> BuildAsync(
        TileAddress address, string layerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        if (_scheme.Rejection(address) is { } rejection)
        {
            throw new ArgumentOutOfRangeException(nameof(address), rejection);
        }

        // For Web Mercator, `TileAddress.WebMercatorEnvelope` — the box this read always asked for.
        Envelope tileBox = _scheme.Envelope(address);
        // <b>Sixteen points an edge on another scheme's grid</b>, for PostGisTileSource.FilterBox's reason: a
        // transverse Mercator square bows when it is moved into degrees, and four moved corners miss the bow.
        // A Mercator tile keeps its four corners, as it always had.
        Geometry filter = Rectangle(tileBox, _scheme.IsWebMercator ? 1 : 16);

        List<Feature> features = [];

        for (int offset = 0; ; offset += PageRows)
        {
            FeatureQuery query = new(
                PageRows,
                fields: [.. _attributes.Select(a => a.Name)],
                includeGeometry: true,
                spatial: new SpatialFilter(filter, SpatialRelation.IndexIntersects),
                // Every page is ordered by identity inside the reader (D-21), so consecutive
                // pages neither repeat nor skip a row.
                offset: offset,

                // <b>The tile is in its scheme's reference — Web Mercator unless the service chose another
                // (ADR-096); the layer need not be.</b> Setting
                // FilterSrid whenever the layer is not already in it routes the box through the
                // projector once, exactly as PostGisTileSource transforms its own filter box into
                // the layer's reference for the `&&` test — never the other way around, which
                // would ask the box test to compare numbers in two different units.
                filterSrid: _layer.Srid == _scheme.Srid ? null : _scheme.Srid);

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

        // <b>The Web Mercator form for a Mercator service</b>, which is the call this made before schemes
        // existed — so an encoder that knows only that form (a test's, or an older one) still serves it.
        return _scheme.IsWebMercator
            ? await _encoder
                .EncodeAsync(rows, address, layerName, _layer.Srid, cancellationToken)
                .ConfigureAwait(false)
            : await _encoder
                .EncodeAsync(rows, address, _scheme, layerName, _layer.Srid, cancellationToken)
                .ConfigureAwait(false);
    }

    private static Polygon Rectangle(Envelope box, int steps)
    {
        if (steps <= 1)
        {
            return new(new LinearRing(XySequence.Wrap(
            [
                box.MinX, box.MinY,
                box.MaxX, box.MinY,
                box.MaxX, box.MaxY,
                box.MinX, box.MaxY,
                box.MinX, box.MinY,
            ])));
        }

        // Counter-clockwise from the south-west corner, `steps` segments an edge, closed.
        List<double> ring = new((steps * 4 + 1) * 2);
        double width = box.MaxX - box.MinX;
        double height = box.MaxY - box.MinY;

        for (int i = 0; i < steps; i++) { ring.Add(box.MinX + (width * i / steps)); ring.Add(box.MinY); }
        for (int i = 0; i < steps; i++) { ring.Add(box.MaxX); ring.Add(box.MinY + (height * i / steps)); }
        for (int i = 0; i < steps; i++) { ring.Add(box.MaxX - (width * i / steps)); ring.Add(box.MaxY); }
        for (int i = 0; i < steps; i++) { ring.Add(box.MinX); ring.Add(box.MaxY - (height * i / steps)); }

        ring.Add(box.MinX);
        ring.Add(box.MinY);

        return new(new LinearRing(XySequence.Wrap([.. ring])));
    }
}
