using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;

namespace Graticula.Host;

/// <summary>
/// A service's sprite files as the sprite routes serve them: the uploaded sheet, with the layers'
/// picture markers packed in beneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-099](../../docs/adr/ADR-099-picture-markers-are-drawn-on-every-face.md) §5.4, and why the
/// sheet is merged rather than replaced.</b> A MapLibre style names one sprite, so a service whose
/// publisher uploaded a sheet (ADR-092) and whose layers draw pictures has to serve both from one
/// picture. The uploaded picture is drawn at the top-left unmoved, so every rectangle its index
/// states is still right, and the generated icons are packed below it under names beginning
/// <see cref="MarkerPicture.NamePrefix"/>, which an upload may not use.
/// </para>
/// <para>
/// <b>Composed when asked for and kept, not composed when a symbology is stored.</b> The sheet
/// depends on every layer of the service and on the uploaded sheet, which change by several doors;
/// computing it from what is there when a client asks means no door can leave it stale. The
/// index needs no pixels and is computed on every request. The picture is kept under a key made of
/// everything it is drawn from — the service, the ratio, the uploaded sheet's own stamp and the
/// generated icons' names, which are content hashes — so a change to any of them is a new key, and an
/// unchanged sheet is composed once per process.
/// </para>
/// <para>
/// <b>A service with no picture markers serves exactly what it served before</b>: the uploaded
/// sheet's bytes as they were stored, or the empty sheet.
/// </para>
/// </remarks>
internal static class GeneratedSprites
{
    /// <summary>How many composed pictures the process keeps before it forgets them all.</summary>
    /// <remarks>
    /// <b>Thirty-two.</b> A composed sheet is tens of kilobytes for a handful of icons and at most a few
    /// megabytes over a large uploaded sheet; two ratios for sixteen busy services is what this keeps.
    /// </remarks>
    private const int MostKept = 32;

    private static readonly ConcurrentDictionary<string, byte[]> Composed = new(StringComparer.Ordinal);

    /// <summary>Every picture the service's layers draw, ordered by name.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The pictures.</returns>
    internal static IReadOnlyList<MarkerPicture> PicturesOf(PublishedService service) =>
        SpriteLayout.PicturesOf(service.Layers.Select(l => l.Symbology));

    /// <summary>The generated icons' names, which a style may draw on this service.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The names.</returns>
    internal static IReadOnlyList<string> Names(PublishedService service) =>
        [.. PicturesOf(service).Select(p => p.Name)];

    /// <summary>
    /// One of the four sprite files, as the route and an exported package carry it.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="ratio">1, or 2 for <c>@2x</c>.</param>
    /// <param name="image">The picture, or else the index.</param>
    /// <param name="store">Where the uploaded sheet is, or null when there is no store to ask.</param>
    /// <param name="canvases">What composes the picture.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The file's bytes.</returns>
    internal static async Task<byte[]> FileAsync(
        PublishedService service,
        int ratio,
        bool image,
        PostgresLayerCatalog? store,
        IMapCanvasFactory canvases,
        CancellationToken cancellation)
    {
        Graticula.Platform.Admin.StoredSprite? uploaded = store is null
            ? null
            : await store.FindSpriteAsync(service.Id, ratio, image, cancellation).ConfigureAwait(false);

        IReadOnlyList<MarkerPicture> pictures = PicturesOf(service);

        if (pictures.Count == 0)
        {
            return image
                ? uploaded?.Image ?? VectorTileEndpoints.EmptySheet
                : Encoding.UTF8.GetBytes(uploaded?.Index ?? "{}");
        }

        SpritePacking packing = SpriteLayout.Pack(pictures);
        int top = uploaded?.Height ?? 0;

        if (!image)
        {
            return Encoding.UTF8.GetBytes(SpriteLayout.Index(packing, ratio, top, uploaded?.Index));
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            service.Id.ToString("N"),
            ratio.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uploaded is null
                ? "-"
                : $"{uploaded.PixelRatio}:{uploaded.UpdatedAt.UtcTicks}:{uploaded.ImageBytes}",
            string.Join(",", packing.Slots.Select(s => s.Picture.Name))))));

        if (Composed.TryGetValue(key, out byte[]? kept))
        {
            return kept;
        }

        byte[] composed = Compose(packing, ratio, uploaded, canvases);

        if (Composed.Count >= MostKept)
        {
            Composed.Clear();
        }

        Composed[key] = composed;

        return composed;
    }

    /// <summary>Draws the uploaded picture and the generated icons into one PNG.</summary>
    /// <remarks>
    /// <b>Drawn at the size the index states, so the pixels and the rectangles cannot disagree</b>:
    /// each icon at its sheet size times the ratio, at the place <see cref="SpriteLayout.Index"/>
    /// writes for it. At @2x an icon is its picture resampled to twice its 1x size, which carries the
    /// picture's own detail on a high-density screen whenever it has any.
    /// </remarks>
    private static byte[] Compose(
        SpritePacking packing,
        int ratio,
        Graticula.Platform.Admin.StoredSprite? uploaded,
        IMapCanvasFactory canvases)
    {
        int top = uploaded?.Height ?? 0;
        int width = Math.Max(uploaded?.Width ?? 0, packing.Width * ratio);
        int height = top + (packing.Height * ratio);

        using IMapCanvas canvas = canvases.Create(Math.Max(1, width), Math.Max(1, height));

        canvas.Clear(Rgba.Transparent);

        if (uploaded?.Image is { } sheet)
        {
            try
            {
                MarkerPicture under = MarkerPicture.FromSheet(sheet);

                canvas.DrawPicture(
                    under.PixelWidth / 2.0,
                    under.PixelHeight / 2.0,
                    new MapSymbol.Picture(under, under.PixelWidth, under.PixelHeight, 0, 0, 0, 1));
            }
            catch (SymbologyException)
            {
                // Checked when it was uploaded; a picture that no longer reads leaves its space empty
                // rather than taking the generated icons with it.
            }
        }

        foreach (SpriteSlot slot in packing.Slots)
        {
            double wide = slot.Picture.SheetWidth * ratio;
            double tall = slot.Picture.SheetHeight * ratio;

            canvas.DrawPicture(
                (slot.X * ratio) + (wide / 2),
                top + (slot.Y * ratio) + (tall / 2),
                new MapSymbol.Picture(slot.Picture, wide, tall, 0, 0, 0, 1));
        }

        return canvas.Encode(MapImageFormat.Png, 100);
    }
}
