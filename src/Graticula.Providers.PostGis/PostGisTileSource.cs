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

    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<string> _attributes;

    /// <summary>Creates a tile source over one layer.</summary>
    /// <param name="dataSource">The pool for the layer's database.</param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="attributes">
    /// Columns to carry into the tile as feature tags. Must already have been
    /// checked against the table's real columns — an identifier cannot be bound
    /// as a parameter, so the whitelist is the safety (ADR-008 §4.6).
    /// </param>
    public PostGisTileSource(
        NpgsqlDataSource dataSource, LayerDefinition layer, IReadOnlyList<string> attributes)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(attributes);

        _dataSource = dataSource;
        _layer = layer;
        _attributes = attributes;
    }

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

        await using NpgsqlCommand command = _dataSource.CreateCommand(BuildSql(layerName));
        command.Parameters.AddWithValue("z", address.Z);
        command.Parameters.AddWithValue("x", address.X);
        command.Parameters.AddWithValue("y", address.Y);

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
        bool native = _layer.Srid == WebMercator;

        // <b>Two envelopes, and that is what keeps the index in play.</b> The
        // tile is a Web Mercator box by definition, so the geometry has to reach
        // ST_AsMVTGeom in 3857. The obvious way — transform every row and
        // compare — cannot use the spatial index, because the index is built on
        // the stored column. So the box is transformed <em>once</em> into the
        // layer's own reference for the `&&` test, and only the rows that
        // survive it are transformed for output. Q-96 measured the difference at
        // 74.6 ms against 21.6 ms on the same tile, which the tile cache pays
        // once.
        string filterBox = native
            ? "bounds.geom"
            : $"ST_Transform(bounds.geom, {_layer.Srid.ToString(CultureInfo.InvariantCulture)})";

        string outputGeometry = native
            ? $"t.{column}"
            : $"ST_Transform(t.{column}, {WebMercator.ToString(CultureInfo.InvariantCulture)})";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             with bounds as (select ST_TileEnvelope(@z, @x, @y) as geom),
             tile as (
                 select ST_AsMVTGeom(
                            {outputGeometry},
                            bounds.geom, {Extent}, {Buffer}, true) as geom{columns}
                 from {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName)} t,
                      bounds
                 where t.{column} && {filterBox}
             )
             select ST_AsMVT(tile.*, '{safeName}', {Extent}, 'geom') from tile
             """);
    }
}
