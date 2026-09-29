using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Graticula.Cartography;

/// <summary>
/// Where each of a service's picture markers sits in its generated sprite sheet, and the index that
/// says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-099](../../../docs/adr/ADR-099-picture-markers-are-drawn-on-every-face.md) §5.4: the
/// sheet is generated from the layers' symbology, not uploaded.</b> A MapLibre client — ArcGIS Pro
/// among them — draws an icon by cutting a named rectangle out of the service's one sprite picture,
/// so a picture marker reaches the tile face only through that picture. Each distinct picture any of
/// the service's layers draws is packed into it once, under <see cref="MarkerPicture.Name"/>, which
/// is the name the derived style asks for.
/// </para>
/// <para>
/// <b>Arithmetic only; the pixels are the rasteriser's.</b> This decides positions and writes the
/// index, and needs no decoder, so the style check and the index route can ask it without a picture
/// being drawn. The picture itself is composed through <see cref="IMapCanvas.DrawPicture"/> by the
/// host, and only when a client asks for it.
/// </para>
/// <para>
/// <b>Deterministic, so the sheet and its ETag change exactly when a picture does.</b> The pictures
/// are ordered by name, not by layer, so reordering a service's layers or restyling one without
/// changing its pictures leaves every rectangle where it was.
/// </para>
/// </remarks>
public static class SpriteLayout
{
    /// <summary>How wide a row of generated icons may run, in 1x pixels.</summary>
    /// <remarks>
    /// <b>1024, which is 2048 in the @2x sheet</b> — half the 4096 a sheet may be (ADR-092), so the
    /// generated part fits beside any uploaded sheet up to the same width. With icons at most
    /// <see cref="MarkerPicture.SheetSide"/> across, a row holds at least seven.
    /// </remarks>
    public const int RowWidth = 1024;

    /// <summary>The transparent pixels between two icons, in 1x pixels.</summary>
    /// <remarks>
    /// <b>One, because a client samples linearly.</b> An icon cut from a sheet is filtered at its
    /// edge with whatever lies next to it; a neighbour's opaque pixel there is a coloured fringe along
    /// one side of every icon.
    /// </remarks>
    public const int Gap = 1;

    /// <summary>
    /// Every distinct picture a service's layers draw, by name.
    /// </summary>
    /// <param name="symbologies">Each layer's stored document, or null for a layer with none.</param>
    /// <returns>The pictures, ordered by name.</returns>
    /// <remarks>
    /// <b>A document that cannot be read contributes nothing and stops nothing.</b> The faces that
    /// draw such a layer fall back to its generated appearance and say why; the sheet the other
    /// layers' icons need is not held hostage to it.
    /// </remarks>
    public static IReadOnlyList<MarkerPicture> PicturesOf(IEnumerable<string?> symbologies)
    {
        ArgumentNullException.ThrowIfNull(symbologies);

        SortedDictionary<string, MarkerPicture> found = new(StringComparer.Ordinal);

        foreach (string? document in symbologies)
        {
            // Cheap first: a document with no picture marker in it has nothing to add.
            if (document is not { Length: > 0 }
                || !document.Contains("CIMPictureMarker", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(document) is JsonObject body && Cim.IsRenderer(body))
                {
                    foreach (MarkerPicture picture in Cim.Project(body).Pictures())
                    {
                        found.TryAdd(picture.Name, picture);
                    }
                }
            }
            catch (Exception e) when (e is SymbologyException or JsonException)
            {
                // Said by the faces that draw the layer; nothing to add here.
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Packs pictures into rows, tallest first, at pixel ratio 1.
    /// </summary>
    /// <param name="pictures">The pictures, in any order.</param>
    /// <returns>Where each one goes, and how large the packed block is.</returns>
    /// <remarks>
    /// <b>Shelves, which is the packing every sprite tool uses and the one a reader can check.</b>
    /// Sorting by height makes each row as tall as its first icon and wastes little on icons that
    /// are, in practice, much the same size; ties are broken by name so the answer never depends on
    /// the order the pictures arrived in.
    /// </remarks>
    public static SpritePacking Pack(IReadOnlyList<MarkerPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);

        List<MarkerPicture> order = [.. pictures
            .Distinct()
            .OrderByDescending(p => p.SheetHeight)
            .ThenBy(p => p.Name, StringComparer.Ordinal)];

        List<SpriteSlot> slots = [];
        int x = 0;
        int y = 0;
        int row = 0;
        int widest = 0;

        foreach (MarkerPicture picture in order)
        {
            if (x > 0 && x + picture.SheetWidth > RowWidth)
            {
                y += row + Gap;
                x = 0;
                row = 0;
            }

            slots.Add(new SpriteSlot(picture, x, y));

            x += picture.SheetWidth + Gap;
            row = Math.Max(row, picture.SheetHeight);
            widest = Math.Max(widest, x - Gap);
        }

        return new SpritePacking(slots, widest, slots.Count == 0 ? 0 : y + row);
    }

    /// <summary>
    /// The sprite index a client reads: an uploaded sheet's own icons, then the generated ones below it.
    /// </summary>
    /// <param name="packing">The generated icons' places.</param>
    /// <param name="ratio">Which sheet: 1, or 2 for <c>@2x</c>.</param>
    /// <param name="top">How far down the generated block starts: the uploaded picture's height, or 0.</param>
    /// <param name="uploaded">The uploaded index as it was stored, or null for none.</param>
    /// <returns>The index, as JSON text.</returns>
    /// <remarks>
    /// <para>
    /// <b>The uploaded icons keep their rectangles and every member they were sent with</b>, because
    /// the uploaded picture is drawn at the top-left of the merged sheet unmoved. An uploaded icon
    /// under the reserved prefix — possible only for a sheet stored before ADR-099 refused one — gives
    /// way to the generated icon of that name, which is the one the derived style means.
    /// </para>
    /// <para>
    /// <b>Each generated icon states its own <c>pixelRatio</c></b>, so a @2x sheet that fell back to
    /// an uploaded 1x picture still carries the generated icons at 2 while the uploaded ones stay at
    /// the ratio their own index gave them. MapLibre reads the ratio per icon.
    /// </para>
    /// </remarks>
    public static string Index(SpritePacking packing, int ratio, int top, string? uploaded)
    {
        ArgumentNullException.ThrowIfNull(packing);

        JsonObject index = [];

        if (uploaded is { Length: > 0 })
        {
            try
            {
                if (JsonNode.Parse(uploaded) is JsonObject stored)
                {
                    foreach (KeyValuePair<string, JsonNode?> icon in stored)
                    {
                        if (!icon.Key.StartsWith(MarkerPicture.NamePrefix, StringComparison.Ordinal))
                        {
                            index[icon.Key] = icon.Value?.DeepClone();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // An uploaded index was checked when it was stored; one that no longer reads adds nothing.
            }
        }

        foreach (SpriteSlot slot in packing.Slots)
        {
            index[slot.Picture.Name] = new JsonObject
            {
                ["width"] = slot.Picture.SheetWidth * ratio,
                ["height"] = slot.Picture.SheetHeight * ratio,
                ["x"] = slot.X * ratio,
                ["y"] = top + (slot.Y * ratio),
                ["pixelRatio"] = ratio,
            };
        }

        return index.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

/// <summary>The generated icons' places, at pixel ratio 1.</summary>
/// <param name="Slots">One per picture, in packing order.</param>
/// <param name="Width">How wide the packed block is.</param>
/// <param name="Height">How tall the packed block is.</param>
public sealed record SpritePacking(IReadOnlyList<SpriteSlot> Slots, int Width, int Height);

/// <summary>Where one picture sits in the packed block, at pixel ratio 1.</summary>
/// <param name="Picture">The picture.</param>
/// <param name="X">Its left edge.</param>
/// <param name="Y">Its top edge, counted from the top of the generated block.</param>
public readonly record struct SpriteSlot(MarkerPicture Picture, int X, int Y);
