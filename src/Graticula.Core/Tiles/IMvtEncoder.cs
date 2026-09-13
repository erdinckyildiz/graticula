using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>One attribute carried into a tile as a feature tag.</summary>
/// <param name="Name">The column name, already checked against the layer's real columns.</param>
/// <param name="Type">Its type, so the encoder can give the value the same PostgreSQL type a
/// hosted column of the same kind would have — ADR-021 §2b.</param>
/// <param name="Value">The value, in whatever .NET shape the source produced it in.</param>
public readonly record struct MvtTag(string Name, FieldType Type, object? Value);

/// <summary>One row an encoder turns into part of a tile.</summary>
/// <param name="Geometry">
/// The shape, in the layer's own coordinate reference — not yet moved to Web Mercator. The
/// encoder does that, the same way <c>PostGisTileSource</c> transforms a stored column that is
/// not already 3857.
/// </param>
/// <param name="Attributes">Its tags, in the order they should appear.</param>
public readonly record struct MvtRow(Geometry Geometry, IReadOnlyList<MvtTag> Attributes);

/// <summary>
/// Encodes a batch of rows into one Mapbox Vector Tile layer — the far side of ADR-021.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-13 so a second source of rows could reach the same encoder.</b> Until then
/// <c>PostGisTileSource</c> read rows and encoded them in the same statement, because both halves
/// were always PostGIS. A GeoParquet layer's rows come from DuckDB — a different engine, with no
/// <c>ST_AsMVTGeom</c> of its own — and ADR-021's decision was never <i>PostGIS reads rows</i>, it
/// was <i>PostGIS encodes them</i>. This port is what lets the second half stand on its own: a
/// caller hands over geometry and tags, in the layer's own reference, and gets MVT bytes back,
/// whatever read them off disk.
/// </para>
/// <para>
/// <b>Tier 1 signature, Tier 2 engine</b> (build-vs-adopt §4). <see cref="Geometry"/>, an int SRID
/// and <see cref="FieldType"/> are ours; no Npgsql or DuckDB type may appear here, which is what
/// makes this callable from <c>Graticula.Providers.DuckDb</c> without that project referencing
/// Npgsql at all.
/// </para>
/// </remarks>
public interface IMvtEncoder
{
    /// <summary>
    /// Builds one tile's worth of one layer from rows already read and filtered by the caller.
    /// </summary>
    /// <param name="rows">
    /// The candidate rows — already the caller's answer to <i>which features are near this
    /// tile</i>. The encoder still runs its own box test against <paramref name="address"/>'s
    /// envelope and clips with <c>ST_AsMVTGeom</c>'s buffer, exactly as <c>PostGisTileSource</c>
    /// does over a table, so that a row that turns out not to touch the tile does not appear —
    /// which can happen at the margins of a caller's own prefilter.
    /// </param>
    /// <param name="address">Which tile, so the encoder can compute the same envelope
    /// <c>ST_TileEnvelope</c> would for it.</param>
    /// <param name="layerName">The name to give the layer inside the tile.</param>
    /// <param name="srid">The reference <paramref name="rows"/>' geometries are in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The encoded tile, or an empty array when nothing in <paramref name="rows"/> survives
    /// the box test.</returns>
    Task<byte[]> EncodeAsync(
        IReadOnlyList<MvtRow> rows,
        TileAddress address,
        string layerName,
        int srid,
        CancellationToken cancellationToken);
}
