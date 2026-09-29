using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Tiles;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Builds vector tiles with <c>ST_AsMVTGeom</c> and <c>ST_AsMVT</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding happens in the database, and that was measured rather than
/// assumed</b> — ADR-021, from
/// <c>benchmarks/mvt-generation/RESULTS.md</c> run 4. An in-process encoder was
/// written, measured over three rounds and found to be good; then Q-67 removed
/// its reason to exist, and run 4 showed the one surviving argument
/// (read-once-encode-many for seeding) bought no measurable throughput while
/// costing 124–245× the allocation and a GC pause reaching 35.5% at concurrency
/// 4 against 0.0%.
/// </para>
/// <para>
/// <b>Nothing here parses geometry.</b> Bytes go out; no WKB is read, no
/// geometry graph is built, and there is no clip, transform or simplify stage in
/// this process. That is what buys the 0.02–0.15 MB per tile.
/// </para>
/// <para>
/// <b>Run in the layer's own database, which since ADR-095 (2026-09-29) may be a registered one.</b>
/// Nothing below assumes the datastore: the schema, table, geometry column and SRID are the layer
/// definition's, the pool is the source's, and a table with no spatial index still answers — the
/// <c>&amp;&amp;</c> becomes a scan, and the tile path says so once. What it does assume is PostGIS 3.0
/// or later, for <c>ST_TileEnvelope</c>; an older registered database answers with an undefined
/// function, and <c>ErrorResponse</c> names the version rather than calling it a missing install.
/// </para>
/// </remarks>
public sealed class PostGisTileSource : ITileSource
{
    /// <summary>MVT internal coordinate space. 4096 is the near-universal choice.</summary>
    public const int Extent = 4096;

    /// <summary>
    /// Margin in tile units on every side.
    /// </summary>
    /// <remarks>
    /// A polygon crossing a tile edge is clipped to the tile, and a renderer
    /// drawing its outline would then draw a line down the seam. The buffer
    /// carries the geometry far enough past the edge that the stroke falls
    /// outside the visible area. 64 is what <c>ST_AsMVTGeom</c> is conventionally
    /// called with and what the benchmarks used, so tiles produced now are
    /// comparable with the numbers already banked.
    /// </remarks>
    public const int Buffer = 64;

    /// <summary>
    /// The grid every tile is cut on, whatever the layer is stored in.
    /// </summary>
    /// <remarks>
    /// <c>ST_TileEnvelope</c> produces a Web Mercator box because the XYZ scheme
    /// is defined in Web Mercator. That is a property of tiling, not a
    /// requirement on the data — which is why the layer keeps its own reference
    /// and the transform happens here.
    /// </remarks>
    public const int WebMercator = 3857;

    /// <summary>How many pixels across a client draws a tile — the size a pixel is measured against.</summary>
    /// <remarks>
    /// <b>512, which is what MapLibre and the ArcGIS Maps SDK draw a vector tile at</b>, so a pixel is the
    /// tile's width over 512: 19 m at z12, 1.2 m at z16.
    /// </remarks>
    public const int Pixels = 512;

    /// <summary>The deepest zoom at which a tile's geometry is simplified — Q-157.</summary>
    /// <remarks>
    /// <b>Measured, not chosen</b> (benchmarks/tile-generalisation): on 927,350 Istanbul polygons,
    /// simplifying at half a pixel took z10 from 1.43 MB to 918 KB and z12 from 1.39 MB to 1.26 MB, left z13 and
    /// z14 no slower, and added 9 to 14 ms to a z15 or z16 tile for a few hundred bytes.
    /// </remarks>
    public const int SimplifiedThroughZoom = 14;

    /// <summary>The tile's width in its own units — Web Mercator metres, or the metres of the service's own tiling scheme (ADR-096) — as SQL over the tile envelope.</summary>
    private const string Span = "(ST_XMax(bounds.geom) - ST_XMin(bounds.geom))";

    /// <summary>
    /// The geometry a tile encodes, generalised by zoom — Q-157, the owner's decision of 2026-09-23.
    /// </summary>
    /// <param name="geometry">SQL for the feature's geometry in Web Mercator.</param>
    /// <returns>SQL for what <c>ST_AsMVTGeom</c> is given.</returns>
    /// <remarks>
    /// <b>Half a pixel at z14 and below, and nothing above.</b> Half a pixel is four of the tile's 4,096 grid
    /// units, below what a client can draw; <c>preserveCollapsed</c> keeps a shape the tolerance would
    /// have reduced to nothing, which <see cref="LargeEnough"/> has already decided should be drawn — for a
    /// line shorter than the tolerance, its two ends, which is what keeps a boundary made of short pieces
    /// joined up.
    /// </remarks>
    public static string Generalised(string geometry) =>
        Generalised(geometry, $"@z <= {SimplifiedThroughZoom}");

    /// <summary>
    /// The geometry a tile encodes, generalised when <paramref name="when"/> holds — ADR-096's form of
    /// Q-157 for a scheme whose levels are not Web Mercator's.
    /// </summary>
    /// <param name="geometry">SQL for the feature's geometry in the tile's reference.</param>
    /// <param name="when">A SQL condition: <c>@z &lt;= 14</c> for Web Mercator, <c>@simplify</c> otherwise.</param>
    /// <returns>SQL for what <c>ST_AsMVTGeom</c> is given.</returns>
    /// <remarks>
    /// <b>The tolerance was always a pixel's size, read off the tile's own width</b>, so it needs nothing
    /// new for another scheme; only the switch was a level number. For another scheme the switch is
    /// decided in C# from the level's resolution (<see cref="VectorTileScheme.Simplifies"/>) and bound, so
    /// the Web Mercator text — and so its tiles — did not change.
    /// </remarks>
    public static string Generalised(string geometry, string when) =>
        $"case when {when} "
        + $"then ST_Simplify({geometry}, {Span} / {Pixels * 2}, true) else {geometry} end";

    /// <summary>The tile's envelope as SQL: <c>ST_TileEnvelope</c> for Web Mercator, the scheme's own box otherwise.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>SQL naming the box, over parameters <see cref="Bind"/> sets.</returns>
    /// <remarks>
    /// <b>Not <c>ST_TileEnvelope(z, x, y, bounds)</c>, though it takes a bounds geometry.</b> That form
    /// cuts a square power-of-two grid from the bounds it is given, and a custom scheme's resolutions need
    /// not halve; the box is computed once, in <see cref="VectorTileScheme.Envelope"/>, and bound as four
    /// numbers, so only one side decides where a tile is — the property the Mercator form gets by leaving
    /// it to PostGIS.
    /// </remarks>
    public static string BoundsSql(VectorTileScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        return scheme.IsWebMercator
            ? "ST_TileEnvelope(@z, @x, @y)"
            : $"ST_MakeEnvelope(@minx, @miny, @maxx, @maxy, {scheme.Srid.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>When a tile is simplified, as SQL — <c>@z &lt;= 14</c> for Web Mercator, <c>@simplify</c> otherwise.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>The condition.</returns>
    public static string SimplifyWhen(VectorTileScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        return scheme.IsWebMercator ? $"@z <= {SimplifiedThroughZoom}" : "@simplify";
    }

    /// <summary>
    /// The tile's box in a layer's own reference, for the <c>&amp;&amp;</c> that reaches the index.
    /// </summary>
    /// <param name="scheme">The scheme.</param>
    /// <param name="srid">The layer's reference.</param>
    /// <returns>SQL over <c>bounds.geom</c>.</returns>
    /// <remarks>
    /// <b>Densified before it is moved, for another scheme only.</b> A transverse Mercator square is not a
    /// square in degrees: its northern edge bows north between its corners, by about 1.2 km over a 600 km
    /// level-0 tile at 41°N, so a box made of the four moved corners would miss what lies in the bow.
    /// Sixteen points an edge take that below five metres at level 0 and to nothing that matters a level or
    /// two down. The Mercator statement keeps its four corners, which is what it has always cut with.
    /// </remarks>
    public static string FilterBox(VectorTileScheme scheme, int srid)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        string target = srid.ToString(CultureInfo.InvariantCulture);

        if (srid == scheme.Srid)
        {
            return "bounds.geom";
        }

        return scheme.IsWebMercator
            ? $"ST_Transform(bounds.geom, {target})"
            : $"ST_Transform(ST_Segmentize(bounds.geom, {Span} / 16), {target})";
    }

    /// <summary>Binds what <see cref="BoundsSql"/> and <see cref="SimplifyWhen"/> read.</summary>
    /// <param name="command">The command.</param>
    /// <param name="scheme">The scheme.</param>
    /// <param name="address">The tile.</param>
    public static void Bind(NpgsqlCommand command, VectorTileScheme scheme, TileAddress address)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(scheme);

        if (scheme.IsWebMercator)
        {
            command.Parameters.AddWithValue("z", address.Z);
            command.Parameters.AddWithValue("x", address.X);
            command.Parameters.AddWithValue("y", address.Y);
            return;
        }

        Graticula.Geometries.Envelope box = scheme.Envelope(address);

        command.Parameters.AddWithValue("minx", box.MinX);
        command.Parameters.AddWithValue("miny", box.MinY);
        command.Parameters.AddWithValue("maxx", box.MaxX);
        command.Parameters.AddWithValue("maxy", box.MaxY);
        command.Parameters.AddWithValue("simplify", scheme.Simplifies(address.Z));
    }

    /// <summary>
    /// Whether a feature is large enough to see at this zoom — Q-157: a polygon whose box is smaller than a
    /// pixel both ways is left out of the tile, and a point or a line never is.
    /// </summary>
    /// <param name="geometry">SQL for the feature's geometry in Web Mercator.</param>
    /// <returns>A SQL condition.</returns>
    /// <remarks>
    /// <para>
    /// <b>Where almost all of Q-157's gain is.</b> A z10 tile over Istanbul went from 16.6 MB and 14.4 s to
    /// 1.43 MB and 3.1 s on this alone; at z16 it costs about 3 ms. A point is exempt because its box has no
    /// size at all — the rule is about shapes too small to draw, and a point is drawn as a symbol.
    /// </para>
    /// <para>
    /// <b>A line is exempt since 2026-09-29, because a line is often one piece of something longer.</b> A
    /// boundary stored as many short lines — the showcase's <c>tr_il</c> is 81 provinces in 5,433 of them,
    /// Ankara alone 248 — lost every piece shorter than a pixel, and the owner saw the provinces drawn
    /// dashed in ArcGIS Pro at 1:10.7 million. A polygon under a pixel is a speck and leaving it out loses
    /// a speck; a line piece under a pixel is a link in a chain and leaving it out breaks the chain. The
    /// piece is still simplified (<see cref="Generalised(string, string)"/>) to its two ends, which join its neighbours',
    /// and <c>ST_AsMVTGeom</c> drops it only when both ends snap to one cell of the tile's grid — a piece
    /// nobody can see, whose neighbours meet in that cell anyway. ADR-085 §5.1, amended.
    /// </para>
    /// </remarks>
    public static string LargeEnough(string geometry) =>
        $"(ST_Dimension({geometry}) < 2"
        + $" or ST_XMax({geometry}) - ST_XMin({geometry}) >= {Span} / {Pixels}"
        + $" or ST_YMax({geometry}) - ST_YMin({geometry}) >= {Span} / {Pixels})";

    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<string> _attributes;
    private readonly VectorTileScheme _scheme;

    /// <summary>Creates a tile source over one layer.</summary>
    /// <param name="dataSource">The pool for the layer's database.</param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="attributes">
    /// Columns to carry into the tile as feature tags. Must already have been
    /// checked against the table's real columns — an identifier cannot be bound
    /// as a parameter, so the whitelist is the safety (ADR-008 §4.6).
    /// </param>
    /// <param name="scheme">The grid the service is cut on, or null for Web Mercator — ADR-096.</param>
    public PostGisTileSource(
        NpgsqlDataSource dataSource,
        LayerDefinition layer,
        IReadOnlyList<string> attributes,
        VectorTileScheme? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(attributes);

        _dataSource = dataSource;
        _layer = layer;
        _attributes = attributes;
        _scheme = scheme ?? VectorTileScheme.WebMercator;
    }

    /// <inheritdoc/>
    public async Task<byte[]> BuildAsync(
        TileAddress address, string layerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        // The scheme's own test: for Web Mercator, `TileAddress.Rejection`, as before.
        if (_scheme.Rejection(address) is { } rejection)
        {
            throw new ArgumentOutOfRangeException(nameof(address), rejection);
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(BuildSql(layerName));
        Bind(command, _scheme, address);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // ST_AsMVT over no rows returns a zero-length tile rather than NULL, but
        // an aggregate over an empty set can still come back NULL depending on
        // how the plan collapses. Both mean the same thing here and both are a
        // valid answer: see ITileSource on why that is not a 404.
        return result as byte[] ?? [];
    }

    /// <summary>
    /// The one statement, with identifiers quoted and every value bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bounding-box test is separate from <c>ST_AsMVTGeom</c> on purpose.</b>
    /// <c>ST_AsMVTGeom</c> returns NULL for geometry outside the tile, so the query
    /// would be correct without the <c>&amp;&amp;</c> — and would read the whole
    /// table for every tile. The <c>&amp;&amp;</c> is what reaches the spatial
    /// index; the function call is what clips. Dropping it is a one-character
    /// change from a tile server into a table scan.
    /// </para>
    /// <para>
    /// <b>The envelope comes from <c>ST_TileEnvelope</c>, not from arithmetic
    /// here.</b> PostGIS and this server must agree exactly on where a tile is,
    /// and the way to guarantee that is for only one of them to decide.
    /// <b>For a Web Mercator service.</b> A service cut on another grid (ADR-096) has
    /// its box computed by <see cref="VectorTileScheme.Envelope"/> and bound as numbers —
    /// still one side deciding, the other side this time (<see cref="BoundsSql"/>).
    /// </para>
    /// </remarks>
    private string BuildSql(string layerName)
    {
        System.Text.StringBuilder columns = new();

        foreach (string attribute in _attributes)
        {
            columns.Append(", t.").Append(LayerDefinition.Quote(attribute));
        }

        // The layer name inside the tile is a string literal in an argument
        // position, so it is escaped rather than quoted as an identifier.
        string safeName = layerName.Replace("'", "''", StringComparison.Ordinal);

        string column = LayerDefinition.Quote(_layer.GeometryColumn);

        // <b>Native to the tile's grid, which is Web Mercator unless the service chose another</b> —
        // ADR-096. For a Mercator service every expression below is the text it was before schemes existed.
        bool native = _layer.Srid == _scheme.Srid;

        // <b>Two envelopes, and that is what keeps the index in play.</b> The
        // tile is a Web Mercator box by definition, so the geometry has to reach
        // ST_AsMVTGeom in 3857. The obvious way — transform every row and
        // compare — cannot use the spatial index, because the index is built on
        // the stored column. So the box is transformed <em>once</em> into the
        // layer's own reference for the `&&` test, and only the rows that
        // survive it are transformed for output. Q-96 measured the difference at
        // 74.6 ms against 21.6 ms on the same tile, which the tile cache pays
        // once.
        string filterBox = FilterBox(_scheme, _layer.Srid);

        string outputGeometry = native
            ? $"t.{column}"
            : $"ST_Transform(t.{column}, {_scheme.Srid.ToString(CultureInfo.InvariantCulture)})";

        // <b>The output geometry once per row, in a lateral</b>, because Q-157's rules read it several
        // times and on a layer not stored in Web Mercator each read would be a transform. The `&&` stays
        // on the stored column, which is what reaches the index.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             with bounds as (select {BoundsSql(_scheme)} as geom),
             tile as (
                 select ST_AsMVTGeom(
                            {Generalised("o.g", SimplifyWhen(_scheme))},
                            bounds.geom, {Extent}, {Buffer}, true) as geom{columns}
                 from {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName)} t,
                      bounds,
                      lateral (select {outputGeometry} as g) o
                 where t.{column} && {filterBox}
                   and {LargeEnough("o.g")}
             )
             select ST_AsMVT(tile.*, '{safeName}', {Extent}, 'geom') from tile
             """);
    }
}
