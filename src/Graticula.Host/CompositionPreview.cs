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
