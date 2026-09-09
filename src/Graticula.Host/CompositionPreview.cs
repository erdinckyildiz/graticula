using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Draws a composition that has not been published, out of the databases it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>By owner decision, 2026-09-06:</b> <i>"db'den okuduğunu direkt çizebilen bir yapı olmalı.
/// db bağlantısı varsa çizebilmeli de. gerçek önizleme ile benzer bir yapı."</i> The Publish
/// screen composes a service out of tables in registered databases, and until this existed the
/// operator pressed Publish to find out what they had built.
/// </para>
/// <para>
/// <b>It is the real drawing path, not a second one.</b> The loop below is
/// <c>MapServerEndpoints.ExportAsync</c>'s loop: the same <see cref="MapRenderer"/>, the same
/// <c>WmsEndpoints.DrawLayerAsync</c>, the same symbology default, the same reprojection. A
/// preview rendered by its own code would be a picture of that code — which is the sentence
/// already written over the symbology editor's preview, for the same reason.
/// </para>
/// <para>
/// <b>Nothing has to exist first, and that is what makes it possible at all.</b>
/// <c>LayerConnections.SourceFor</c> reads three things off a
/// <see cref="PublishedLayer"/> — its connection string, its definition and its statement
/// timeout — and never asks the catalogue whether that layer is published. So a layer assembled
/// in memory from a composition entry reads features exactly as a served one does. This was
/// measured before the endpoint was written rather than hoped for; had it been false, the
/// preview would have needed a temporary service and its own decision record.
/// </para>
/// <para>
/// <b>What it is not:</b> a cache, a job, or a thing that writes. It opens the same pooled
/// connections a served layer would, honours the same record ceiling, and leaves nothing behind.
/// </para>
/// </remarks>
internal static class CompositionPreview
{
    /// <summary>The image when the caller does not ask for a size.</summary>
    /// <remarks>
    /// The Publish screen's middle pane, at the width it actually has. A caller asking for
    /// something else is bounded by the same setting <c>MapServer/export</c> is bounded by.
    /// </remarks>
    private const int DefaultWidth = 900;

    /// <summary>The image height when the caller does not ask for one.</summary>
    private const int DefaultHeight = 620;

    /// <summary>How many features one preview layer may draw.</summary>
    /// <remarks>
    /// <b>Lower than the served ceiling on purpose.</b> A preview is looked at while somebody is
    /// still deciding, so it is answered quickly or it is not looked at; a served map is fetched
    /// by a client that will wait. The number is a bound rather than a promise — where it bites,
    /// the drawing is a sample of the layer and the screen says so.
    /// <para>
    /// <b>That last clause was false for two days, and it is the reason ADR-057 condition 7
    /// exists.</b> Written 2026-09-06 as a description of intent; nothing anywhere reported
    /// whether the ceiling had bitten, so a sampled drawing and a complete one were the same
    /// picture. Since 2026-09-08 <c>DrawLayerAsync</c> returns what it drew,
    /// <c>POST /admin/publish/preview</c> answers with <c>X-Graticula-Ceiling</c> and — when it
    /// bit — <c>X-Graticula-Sampled</c>, and the sentence over the map names the layers.
    /// </para>
    /// <para>
    /// ~~<b>The number itself is still a guess</b>, which is condition 6 and is not this one. It
    /// was picked for feel: nobody has measured where a preview stops being instant, or whether
    /// the bound should be rows or vertices.~~
    /// <b>Measured 2026-09-08 and this comment was a day behind it — corrected 2026-09-09.</b>
    /// [benchmarks/publish-scale](../../../benchmarks/publish-scale/RESULTS.md) discharged
    /// ADR-057 condition 6: 4,000 simple polygons draw in 61 ms and 1,000 complex ones take
    /// 291 ms, so the cost follows vertices rather than rows.
    /// </para>
    /// <para>
    /// <b>So this number is no longer the only bound, and by itself it never bounded the case
    /// that is slow.</b> The row ceiling holds a simple layer's cost flat and does nothing for a
    /// dense one: 16,000 rows of 500 vertices capped to 4,000 drawn still cost 1,113 ms against
    /// a 4,000-row table's 716 ms. <see cref="RowLimit"/> is the other half —
    /// [Q-148](../../../docs/open-questions.md) answered 2026-09-09 — and it lowers this number
    /// per layer, from the width the database already knows its geometries to have. Where the two
    /// disagree the smaller wins, so a simple layer is drawn exactly as it was.
    /// </para>
    /// </remarks>
    private const int PreviewRecordCeiling = 4000;

    /// <summary>
    /// Builds the in-memory layers a composition would publish, in draw order.
    /// </summary>
    /// <remarks>
    /// <b>Index 0 is drawn on top, so this reverses.</b> The composition's order is the
    /// operator's drawing order and the renderer paints in call order, so the last entry has to
    /// be drawn first. Getting this backwards is invisible until two layers overlap, which is
    /// why it is stated here rather than left to the loop.
    /// </remarks>
    /// <param name="nodes">The composition, flattened — groups contribute their children.</param>
    /// <param name="catalog">Where a data source's connection string comes from.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The layers bottom-first, or null with the entry that could not be resolved.</returns>
    public static async Task<(List<PublishedLayer>? Layers, string? Refusal)> LayersAsync(
        IReadOnlyList<CompositionNode> nodes,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(catalog);

        List<LayerPublication> flat = [];

        foreach (CompositionNode node in nodes)
        {
            if (node.IsGroup)
            {
                flat.AddRange(node.Children ?? []);
                continue;
            }

            if (node.Layer is { } one)
            {
                flat.Add(one);
            }
        }

        if (flat.Count == 0)
        {
            return (null, "There is nothing in this composition to draw.");
        }

        // <b>One lookup per data source, not per layer.</b> A composition of forty tables from
        // one database would otherwise decrypt the same credential forty times.
        Dictionary<Guid, string?> connections = [];
        List<PublishedLayer> layers = [];

        foreach (LayerPublication publication in flat)
        {
            if (!connections.TryGetValue(publication.DataSourceId, out string? connection))
            {
                connection = await catalog
                    .ConnectionStringOfAsync(publication.DataSourceId, cancellation)
                    .ConfigureAwait(false);

                connections[publication.DataSourceId] = connection;
            }

            if (connection is null)
            {
                return (null,
                    $"'{publication.Name}' names a database this server is not pointed at any "
                    + "more. Remove it from the composition, or register the database again.");
            }

            LayerDefinition definition = new(
                publication.Name,
                publication.SchemaName,
                publication.TableName,
                publication.GeometryColumn,
                publication.Srid,
                publication.IdentityColumn,

                // <b>The object-id column, which is what an ArcGIS client addresses a feature
                // by.</b> A preview never answers a query, so it would draw without one — but
                // the definition is the same definition publishing builds, and two shapes for
                // one table is how a preview stops being a picture of the real thing.
                publication.ObjectIdColumn,
                isHosted: false);

            layers.Add(new PublishedLayer(
                Guid.NewGuid(),
                definition,
                "preview",
                connection,
                publication.GeometryType,
                owner: null,
                SharingScope.Private,
                ServiceStatus.Started,

                // <b>The symbol the operator chose, so the picture is the service.</b> A preview
                // drawn with the generated appearance while the composition carries a chosen one
                // would be a picture of what publishing does not do.
                symbology: publication.Symbology));
        }

        layers.Reverse();

        return (layers, null);
    }

    /// <summary>
    /// The reference the preview is drawn in.
    /// </summary>
    /// <remarks>
    /// <b>The composition's own choice, because that is the question the preview answers.</b>
    /// The dialog's <i>Served in</i> is what the service will use, and a preview drawn in some
    /// other reference would show a map the service never serves. With nothing chosen, each
    /// layer answers in its own — so the preview takes the first layer's, which is what the
    /// service document will report too.
    /// </remarks>
    /// <param name="asked">The composition's reference, or null.</param>
    /// <param name="layers">The layers, in draw order.</param>
    /// <returns>An EPSG code.</returns>
    public static int ReferenceFor(int? asked, IReadOnlyList<PublishedLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        return asked is { } chosen && chosen > 0
            ? chosen
            : layers.Count > 0 ? layers[^1].Definition.Srid : 3857;
    }

    /// <summary>
    /// The reference to draw in: the caller's map, then the composition's, then a layer's.
    /// </summary>
    /// <remarks>
    /// <b>A caller with a map wins, because their `bbox` is in that reference.</b> Drawing in
    /// the composition's choice while framing on the map's numbers would put the picture
    /// somewhere else entirely — the two are only ever the same by luck.
    /// </remarks>
    /// <param name="bboxSr">What the caller says its frame is in, or empty.</param>
    /// <param name="asked">The composition's reference, or null.</param>
    /// <param name="layers">The layers, in draw order.</param>
    /// <returns>An EPSG code.</returns>
    public static int ReadReference(
        string? bboxSr, int? asked, IReadOnlyList<PublishedLayer> layers)
    {
        return int.TryParse(
                bboxSr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int given)
            && given > 0
                ? given
                : ReferenceFor(asked, layers);
    }

    /// <summary>
    /// The extent to draw, from the layers themselves when the caller does not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The union of what is in the composition, which is the only frame that shows all of
    /// it.</b> An operator who has just dragged four tables in wants to see the four; asking
    /// them for a bounding box first would be asking them to know where their data is, which is
    /// what the picture is for.
    /// </para>
    /// <para>
    /// <b>Each layer's extent is projected into the chosen reference before they are
    /// combined</b>, because a union of numbers from three different references is a rectangle
    /// in no reference at all. That is [D-226](../../docs/architecture-debt.md)'s lesson applied
    /// where it was learnt.
    /// </para>
    /// </remarks>
    /// <param name="contexts">Where a layer's described extent comes from.</param>
    /// <param name="layers">The layers.</param>
    /// <param name="srid">The reference to combine in.</param>
    /// <param name="projector">The projector.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>An extent, or null when no layer could report one.</returns>
    public static async Task<Envelope?> ExtentAsync(
        ServiceContexts contexts,
        IReadOnlyList<PublishedLayer> layers,
        int srid,
        IProjector projector,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(layers);

        Envelope? all = null;

        foreach (PublishedLayer layer in layers)
        {
            LayerDescription described;

            try
            {
                (_, described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // <b>A layer that cannot be described is skipped rather than fatal.</b> The
                // preview's job is to show what can be shown; one unreachable table must not
                // turn a picture of the other three into an error page.
                continue;
            }

            if (described.Extent is not { } extent)
            {
                continue;
            }

            Envelope? inReference = layer.Definition.Srid == srid
                ? extent
                : await ServedExtent
                    .InAsync(extent, layer.Definition.Srid, srid, projector, cancellation)
                    .ConfigureAwait(false);

            if (inReference is not { } piece)
            {
                continue;
            }

            all = all is { } sofar
                ? new Envelope(
                    Math.Min(sofar.MinX, piece.MinX),
                    Math.Min(sofar.MinY, piece.MinY),
                    Math.Max(sofar.MaxX, piece.MaxX),
                    Math.Max(sofar.MaxY, piece.MaxY))
                : piece;
        }

        return all is { } found ? Padded(found) : null;
    }

    /// <summary>
    /// Where each layer of a composition is, and where all of them are together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>So the screen can move the map to one layer.</b> Owner instruction 2026-09-06: *sağ
    /// clickte zoom to layer yapabilmeliyim*. Without this the only extent the console could get
    /// was the one the preview reports for the whole composition, so *zoom to this layer* had
    /// no answer for a composition of more than one.
    /// </para>
    /// <para>
    /// <b>And it replaces a wasteful first draw.</b> The map used to get its opening frame by
    /// asking for a picture with no <c>bbox</c> and reading the extent off the answer — a full
    /// rendering thrown away for four numbers. This is the four numbers.
    /// </para>
    /// <para>
    /// <b>A layer that will not say is absent rather than zero.</b> An empty table and an
    /// unreachable one both report nothing, and a zero envelope would put the map on Null
    /// Island; the caller is told which layers have an extent and draws its own conclusion about
    /// the rest.
    /// </para>
    /// </remarks>
    /// <param name="contexts">Where a layer's described extent comes from.</param>
    /// <param name="layers">The layers.</param>
    /// <param name="srid">The reference to report in.</param>
    /// <param name="projector">The projector.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>Each layer that has an extent, by name, and the union of them.</returns>
    public static async Task<(List<(string Name, Envelope Extent)> Each, Envelope? All)>
        EachExtentAsync(
            ServiceContexts contexts,
            IReadOnlyList<PublishedLayer> layers,
            int srid,
            IProjector projector,
            CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(layers);

        List<(string, Envelope)> each = [];
        Envelope? all = null;

        foreach (PublishedLayer layer in layers)
        {
            LayerDescription described;

            try
            {
                (_, described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                continue;
            }

            if (described.Extent is not { } extent)
            {
                continue;
            }

            Envelope? inReference = layer.Definition.Srid == srid
                ? extent
                : await ServedExtent
                    .InAsync(extent, layer.Definition.Srid, srid, projector, cancellation)
                    .ConfigureAwait(false);

            if (inReference is not { } piece)
            {
                continue;
            }

            each.Add((layer.Definition.Name, Padded(piece)));

            all = all is { } sofar
                ? new Envelope(
                    Math.Min(sofar.MinX, piece.MinX),
                    Math.Min(sofar.MinY, piece.MinY),
                    Math.Max(sofar.MaxX, piece.MaxX),
                    Math.Max(sofar.MaxY, piece.MaxY))
                : piece;
        }

        return (each, all is { } found ? Padded(found) : null);
    }

    /// <summary>
    /// Reads a `bbox` the caller supplied, in the drawing's own reference.
    /// </summary>
    /// <param name="text">Four comma-separated numbers, or null.</param>
    /// <param name="extent">The extent read.</param>
    /// <returns>Whether it parsed and describes a real rectangle.</returns>
    public static bool TryReadExtent(string? text, out Envelope extent)
    {
        extent = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4)
        {
            return false;
        }

        double[] numbers = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                    parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                || !double.IsFinite(numbers[i]))
            {
                return false;
            }
        }

        if (numbers[2] <= numbers[0] || numbers[3] <= numbers[1])
        {
            return false;
        }

        extent = new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);

        return true;
    }

    /// <summary>
    /// Reads a `size` of the form `width,height`, bounded by what the server allows.
    /// </summary>
    /// <param name="text">The size asked for, or null for the default.</param>
    /// <param name="bound">The server's own ceiling.</param>
    /// <returns>A width and a height, both at least one pixel.</returns>
    public static (int Width, int Height) ReadSize(string? text, WidthHeight bound)
    {
        int width = DefaultWidth;
        int height = DefaultHeight;

        if (!string.IsNullOrWhiteSpace(text))
        {
            string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                width = w;
                height = h;
            }
        }

        return (
            Math.Clamp(width, 1, Math.Max(1, bound.Width)),
            Math.Clamp(height, 1, Math.Max(1, bound.Height)));
    }

    /// <summary>How many features one preview layer draws.</summary>
    public static int RecordCeiling(int serverCeiling) =>
        Math.Min(PreviewRecordCeiling, Math.Max(1, serverCeiling));

    /// <summary>How much geometry one preview layer may read when nothing says otherwise.</summary>
    /// <remarks>
    /// <para>
    /// <b>2 MB, and the number is where the dense case stops being slow rather than where it
    /// stops being visible.</b> Measured 2026-09-09 on PostgreSQL 16.4 / PostGIS 3.4.3: the worst
    /// layer in the corpus — 16,000 rows of 501 vertices — falls from <b>1,287 ms to 82 ms</b>,
    /// every dense layer lands in 67–82 ms whatever its row count, and no simple layer moves at
    /// all because <see cref="PreviewRecordCeiling"/> still binds first.
    /// </para>
    /// <para>
    /// <b>Written here and read by <see cref="HostSettings"/> rather than the other way
    /// round</b>, because a record's own parameter list cannot see its own constants and two
    /// copies of a default is how the configured default and the compiled one come to disagree.
    /// </para>
    /// </remarks>
    public const long DefaultGeometryBudgetBytes = 2L * 1024 * 1024;

    /// <summary>
    /// How many features of one layer to draw, given how wide its geometries are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-148](../../docs/open-questions.md), answered by measurement on 2026-09-09.</b> The
    /// preview's cost is in vertices and its only bound counted rows, so the layer that needed
    /// bounding was the one the bound did nothing about: 16,000 rows of 501 vertices drew in
    /// <b>1,287 ms</b> with the 4,000-row ceiling applied. The rule is
    /// <c>min(ceiling, budget ÷ width)</c>, and at a 2 MB budget that layer draws in
    /// <b>82 ms</b> — every dense layer in the corpus lands in 67–82 ms whatever its row count.
    /// </para>
    /// <para>
    /// <b>Simple layers do not move, which is the property that makes this safe to apply
    /// everywhere.</b> A 5-vertex polygon is 136 bytes, so 2 MB is fifteen thousand of them and
    /// the row ceiling binds first: a 16,000-row 5-vertex layer draws in 24.1 ms with the budget
    /// and 24.1 ms without it.
    /// </para>
    /// <para>
    /// <b>No statistic means the ceiling, and it must never mean a refusal.</b> A table that has
    /// had neither <c>CREATE INDEX</c> nor <c>ANALYZE</c> reports a row count of −1 and no width
    /// at all — and that is exactly the freshly imported layer somebody is most likely to be
    /// previewing. Guessing a width for it would bound the one layer nothing is known about by
    /// a number nothing supports; drawing it as this server always did is the honest answer, and
    /// the ceiling above is still a bound.
    /// </para>
    /// <para>
    /// <b>What this gate cannot do, stated here rather than discovered later.</b> A table-level
    /// average describes the table and not the draw. Two 16,000-row tables holding the same 160
    /// giant polygons, with statistics identical to the byte, drew in <b>139.9 ms and 23.9 ms</b>
    /// — because the query reads a particular 4,000 rows in identity order and one table happens
    /// to keep its giants inside that window. On the clustered one a 2 MB budget withheld
    /// <b>1,861 features that were free</b>. That is a false refusal and it is the price of the
    /// gate rather than a defect in it, which is why the drawing says when the bound bit.
    /// </para>
    /// <para>
    /// <b>At least one feature, never zero.</b> A layer whose average geometry is larger than the
    /// whole budget would otherwise be drawn as nothing, which on screen is indistinguishable
    /// from an empty table — and one feature plus the notice is a truthful picture where a blank
    /// one is not.
    /// </para>
    /// </remarks>
    /// <param name="ceiling">The row ceiling this preview would use.</param>
    /// <param name="budgetBytes">How much geometry one layer may read. Zero or less is off.</param>
    /// <param name="width">What the source knows about its geometries, or null for nothing.</param>
    /// <returns>The most features to draw, between one and <paramref name="ceiling"/>.</returns>
    public static int RowLimit(int ceiling, long budgetBytes, GeometryWidth? width)
    {
        int bound = Math.Max(1, ceiling);

        if (budgetBytes <= 0 || width?.PerFeatureBytes is not { } bytes || bytes <= 0)
        {
            return bound;
        }

        long allowed = budgetBytes / bytes;

        return allowed >= bound ? bound : (int)Math.Max(1, allowed);
    }

    /// <summary>
    /// Draws the layers of a composition, each bounded by rows and by geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in the endpoint, because this is the class that owns what a preview
    /// draws with.</b> The loop is <c>MapServerEndpoints.ExportAsync</c>'s loop with two bounds
    /// instead of one, and the endpoint's remaining job is to read a request and write headers.
    /// </para>
    /// <para>
    /// <b>One statistic lookup per layer per draw, and the arithmetic is on the record.</b> It is
    /// 0.72 ms measured, against a preview it holds to tens of milliseconds — 1.8%. Remembering
    /// it would save that and would go on bounding a layer by what it used to be after somebody
    /// analysed it, which is the wrong way round for a screen whose whole purpose is to show what
    /// is there now.
    /// </para>
    /// <para>
    /// <b>A statistic that cannot be read is not a reason to refuse a picture.</b> The same
    /// judgement <see cref="ExtentAsync"/> makes one method up: the preview's job is to show what
    /// can be shown. If the database is genuinely unreachable the draw on the next line says so.
    /// </para>
    /// <para>
    /// <b>At the limit, not over it.</b> A layer with exactly as many features as its limit is
    /// reported as sampled too. That is a false positive of one and it is the safe direction —
    /// the sentence the screen shows says <i>this may be part of it</i>, which is true either
    /// way, rather than promising a completeness nothing here can check without a second count
    /// query per layer.
    /// </para>
    /// </remarks>
    /// <param name="contexts">The feature sources and their described shapes.</param>
    /// <param name="renderer">What to draw into.</param>
    /// <param name="transform">Map units to pixels.</param>
    /// <param name="layers">The layers, bottom-first.</param>
    /// <param name="srid">The reference the picture is drawn in.</param>
    /// <param name="ceiling">The row ceiling every layer shares.</param>
    /// <param name="budgetBytes">How much geometry one layer may read. Zero or less is off.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The names of the layers whose drawing reached the bound, in draw order.</returns>
    public static async Task<List<string>> DrawAsync(
        ServiceContexts contexts,
        MapRenderer renderer,
        PixelTransform transform,
        IReadOnlyList<PublishedLayer> layers,
        int srid,
        int ceiling,
        long budgetBytes,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(layers);

        List<string> sampled = [];

        foreach (PublishedLayer layer in layers)
        {
            int limit = RowLimit(
                ceiling,
                budgetBytes,
                await WidthAsync(contexts, layer, cancellation).ConfigureAwait(false));

            int drawn = await WmsEndpoints
                .DrawLayerAsync(
                    contexts, renderer, transform, layer, srid, null, limit, cancellation)
                .ConfigureAwait(false);

            if (drawn >= limit)
            {
                sampled.Add(layer.Definition.Name);
            }
        }

        return sampled;
    }

    /// <summary>
    /// What the layer's source knows about the size of its geometries, or nothing.
    /// </summary>
    /// <param name="contexts">Where the source comes from.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The statistic, or null when there is none or it could not be read.</returns>
    private static async Task<GeometryWidth?> WidthAsync(
        ServiceContexts contexts, PublishedLayer layer, CancellationToken cancellation)
    {
        try
        {
            (IFeatureSource source, _) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            // <b>A source that keeps no statistics is not an error.</b> Only PostGIS answers
            // this today, and a preview that demanded it would be deciding for providers that
            // do not exist yet — the same reasoning `IFeatureVersions` is written with.
            return source is IGeometryStatistics statistics
                ? await statistics.GeometryWidthAsync(cancellation).ConfigureAwait(false)
                : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A little air around the data, so the outermost feature is not on the frame.
    /// </summary>
    /// <remarks>
    /// <b>Five per cent, and a degenerate extent is widened rather than refused.</b> A
    /// composition of one point has an extent of zero width, which is a transform that divides
    /// by nothing — the point becomes a hundred metres of nothing around itself instead.
    /// </remarks>
    private static Envelope Padded(Envelope extent)
    {
        double width = extent.MaxX - extent.MinX;
        double height = extent.MaxY - extent.MinY;

        double padX = width > 0 ? width * .05 : 100;
        double padY = height > 0 ? height * .05 : 100;

        return new Envelope(
            extent.MinX - padX, extent.MinY - padY, extent.MaxX + padX, extent.MaxY + padY);
    }
}
