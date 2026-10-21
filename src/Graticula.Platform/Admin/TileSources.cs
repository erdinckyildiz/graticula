using System;

namespace Graticula.Platform.Admin;

/// <summary>
/// Which layers serve vector tiles — the datastore's only, <see href="../../../docs/adr/ADR-206-vector-tiles-are-served-from-the-datastore-only.md">ADR-206</see> —
/// and how long a layer's cached answers are kept and how far they can be trusted when nobody chose, ADR-095 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule, asked by every reader.</b> The tile face, the services directory, the portal, the seed, the
/// console's listings and the catalogue's own <c>AdminLayer.Tileable</c> all ask this rather than spelling it.
/// Until 2026-09-29 the catalogue spelled it in SQL (<c>d.is_datastore or d.kind in (...)</c>) beside the
/// C# copy in <c>VectorTileEndpoints.Tileable</c>, which is the two-copies shape
/// [D-264](../../../docs/architecture-debt.md) was about.
/// </para>
/// <para>
/// <b>Tiles come from the datastore only — owner decision 2026-10-20, ADR-206</b>, which restores
/// [Q-67](../../../docs/open-questions.md)'s rule of 2026-08-12. Between those dates a registered PostGIS database
/// (ADR-095), the DuckDB-read kinds (ADR-066 §9, ADR-067) and, for a day, Oracle (ADR-205 §5c) served tiles too;
/// nothing on the showcase drew them, and a registered layer is drawn through its feature service, as ArcGIS draws one.
/// </para>
/// <para>
/// <b>The lifetimes and coherences below outlived the tiles they were written for</b>, because the query face sends
/// its answers with the same lifetime (ADR-069, <c>QueryResponseCaching</c>): a registered PostGIS table's answers are
/// still kept five minutes, not the hour a datastore layer's are, because other tools write to it.
/// </para>
/// </remarks>
public static class TileSources
{
    /// <summary>
    /// How long a registered PostGIS layer's tiles are kept when nobody set a lifetime on the layer — five
    /// minutes, or the server's own default when that is shorter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>INFERRED, ADR-095 §5.3.</b> The owner decided registered PostGIS layers serve tiles and that their
    /// cache be bounded by a lifetime shorter than a hosted layer's; the number is this session's. ADR-010 §5.2
    /// says TTL is the only coherence mechanism that works on a database somebody else writes to, and §5.3 that
    /// only the administrator knows how volatile a layer is. So the default has to be safe for the layer nobody
    /// declared, and the declared one is <c>PUT /admin/layers/{name}/cache</c>.
    /// </para>
    /// <para>
    /// <b>Why five and not one or sixty.</b> Sixty is the hosted default, and a hosted layer's own edits empty
    /// its cache at once; a registered table's edits mostly arrive through other tools and would stay on every
    /// map for an hour. One minute keeps almost nothing: a map panning over a city asks for most of its tiles
    /// again within a few minutes, and a cold registered tile is a query against somebody else's database. Five
    /// is the window an operator can explain — *an edit made in QGIS appears within five minutes* — and it is
    /// the same order as the WMS time extent's (ADR-010 §5.2's table).
    /// </para>
    /// </remarks>
    public static readonly TimeSpan RegisteredLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Whether a layer serves vector tiles — ADR-206.</summary>
    /// <param name="hosted">Whether the layer's data is in the datastore.</param>
    /// <returns>True for a datastore layer; false for every registered source, whatever its kind.</returns>
    public static bool Tiled(bool hosted) => hosted;

    /// <summary>Whether this server sees every change to the layer's rows — false for a registered PostGIS layer.</summary>
    /// <param name="hosted">Whether the layer's data is in the datastore.</param>
    /// <param name="kind">Its source's kind.</param>
    /// <returns>True when the layer is a registered PostGIS table, which other tools write to behind our back.</returns>
    /// <remarks>
    /// <b>PostGIS only, and MotherDuck is the other case it does not cover.</b> A MotherDuck table can also change
    /// under us and carries no version (<c>GeoParquetFolder</c> reads none for it), so it is <c>best-effort</c> in
    /// <see cref="CoherenceOf"/> too — but its default lifetime was not part of the owner's decision and is left
    /// as it was. ADR-095 §6 records it. <b>A registered Oracle table is one too</b> (ADR-205 §5c): other tools write to
    /// it, and it has five minutes and <c>best-effort</c> as a registered PostGIS table has.
    /// </remarks>
    public static bool Registered(bool hosted, string? kind) =>
        !hosted && kind is DataSourceKinds.PostGis or DataSourceKinds.Oracle or DataSourceKinds.MySql or DataSourceKinds.SqlServer;

    /// <summary>How long a layer's tiles are kept when the layer itself names no lifetime.</summary>
    /// <param name="hosted">Whether the layer's data is in the datastore.</param>
    /// <param name="kind">Its source's kind.</param>
    /// <param name="serverDefault">The server's own default — <c>Graticula:TileCacheMinutes</c>.</param>
    /// <returns><see cref="RegisteredLifetime"/> for a registered PostGIS layer, never more than the server's; the server's otherwise.</returns>
    public static TimeSpan DefaultLifetimeOf(bool hosted, string? kind, TimeSpan serverDefault) =>
        Registered(hosted, kind) && serverDefault > RegisteredLifetime ? RegisteredLifetime : serverDefault;

    /// <summary>
    /// How closely a layer's cached tiles follow its data, in the words the admin API reads back — ADR-010 §6b.
    /// </summary>
    /// <param name="hosted">Whether the layer's data is in the datastore.</param>
    /// <param name="kind">Its source's kind.</param>
    /// <returns>
    /// <c>exact</c> for a hosted layer, whose writes come through this server and empty its tiles at once;
    /// <c>file-version</c> for a GeoParquet file or DuckDB database, whose version rides in the tile's key so a
    /// replaced file is never served from the old one's tiles; <c>best-effort</c> for a registered PostGIS table
    /// and a MotherDuck database, which other tools change and only the lifetime bounds.
    /// </returns>
    /// <remarks>
    /// <b>§6b's own sentence is why this is a field and not a document:</b> <i>a best-effort guarantee an
    /// administrator cannot inspect is indistinguishable from a bug.</i> An edit made through this server's
    /// FeatureServer still empties a registered layer's tiles at once; <c>best-effort</c> is about every other
    /// writer.
    /// </remarks>
    public static string CoherenceOf(bool hosted, string? kind) =>
        hosted ? "exact"
        : kind is DataSourceKinds.PostGis or DataSourceKinds.MotherDuck or DataSourceKinds.Oracle or DataSourceKinds.MySql
            or DataSourceKinds.SqlServer ? "best-effort"
        : "file-version";
}
