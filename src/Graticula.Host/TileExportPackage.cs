using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Graticula.Tiles.Packages;

namespace Graticula.Host;

/// <summary>
/// What goes into an exported package besides its tiles, how large it will be, and the writing of it from the
/// staged tiles — ADR-098 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The documents are the served ones</b>: the service document from <see cref="VectorTileEndpoints.ServiceDocumentAsync"/>,
/// the style the style route would serve (the stored one while it fits, else the generated one), the sprite sheet the
/// sprite routes serve and the glyph ranges the style names. A package that carried a second opinion about any of them
/// would draw differently offline from online, and nobody would find out until they were offline.
/// </para>
/// <para>
/// <b>Three things differ inside a VTPK, on purpose.</b> Its tiles are gzip-compressed, so its service document says
/// <c>tileCompression: gzip</c> where the live one says <c>none</c>; it has no tile map and cannot export, so
/// <c>tileMap</c> goes and <c>capabilities</c> is <c>TilesOnly</c>; and its levels end at the highest level exported,
/// so a client over-zooms the last one rather than asking for tiles the package does not hold.
/// </para>
/// </remarks>
internal static class TileExportPackage
{
    /// <summary>What the documents of a package are allowed for in its estimate: glyphs, sprites and JSON.</summary>
    /// <remarks>
    /// <b>Eight megabytes</b>: the shipped glyphs are 4.3 MB for every range of the one stack, and an uploaded sprite
    /// sheet is at most 8 MB at each ratio (migration 60) — so this is a fair allowance for most packages and an
    /// underestimate for one with two large sheets, which the budget's margin absorbs.
    /// </remarks>
    internal const long DocumentAllowance = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The four files the sprite routes answer, by name, pixel ratio and whether it is the picture.</summary>
    private static readonly (string Name, int Ratio, bool Image)[] SpriteFiles =
    [
        ("sprite.json", 1, false), ("sprite.png", 1, true), ("sprite@2x.json", 2, false), ("sprite@2x.png", 2, true),
    ];

    /// <summary>The package's file extension.</summary>
    internal static string Extension(TileExportFormat format) => format == TileExportFormat.Vtpk ? ".vtpk" : ".pmtiles";

    /// <summary>
    /// The download's media type. PMTiles has a recommended one; a VTPK has none registered or documented, so it is sent
    /// as what it is to a browser — bytes to save — which is <b>INFERRED</b> to be what a client downloading one expects.
    /// </summary>
    internal static string MediaType(TileExportFormat format) =>
        format == TileExportFormat.Vtpk ? "application/octet-stream" : PmTiles.MediaType;

    /// <summary>The name a download is saved under: the service's name, safe for a file system, and the extension.</summary>
    internal static string DownloadName(PublishedService service, TileExportFormat format)
    {
        StringBuilder name = new();

        foreach (char c in service.Name)
        {
            name.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return (name.Length == 0 ? "tiles" : name.ToString()) + Extension(format);
    }

    /// <summary>
    /// How many bytes a package is expected to take: the tiles' own bytes and each format's overhead.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <param name="levels">Each level's rectangle.</param>
    /// <param name="tileBytes">The tiles' bytes as the cache would hold them — uncompressed, so this errs high.</param>
    /// <returns>Bytes.</returns>
    /// <remarks>
    /// <b>A VTPK pays a 131,136-byte header and index for every bundle it writes</b> and four bytes before every tile;
    /// counting every 128 × 128 block the rectangles touch is an upper bound, since a block with no tile in it is not
    /// written. A PMTiles archive pays about a directory entry per tile. The tiles are counted uncompressed although both
    /// formats store them gzip-compressed, which makes the estimate pessimistic by whatever gzip saves — typically half
    /// or more for vector tiles, not measured here.
    /// </remarks>
    internal static long EstimateBytes(TileExportFormat format, IReadOnlyList<TileRange> levels, long tileBytes)
    {
        long tiles = levels.Sum(level => level.Count);

        if (format == TileExportFormat.PmTiles)
        {
            return Saturate(tileBytes, Saturate(tiles * 16, PmTiles.HeaderSize + 64 * 1024));
        }

        long bundles = levels.Sum(level =>
            ((long)(level.MaxX / CompactCacheBundle.PacketSize) - (level.MinX / CompactCacheBundle.PacketSize) + 1)
            * ((long)(level.MaxY / CompactCacheBundle.PacketSize) - (level.MinY / CompactCacheBundle.PacketSize) + 1));

        return Saturate(
            tileBytes, Saturate(tiles * 4, Saturate(bundles * CompactCacheBundle.DataStart, DocumentAllowance)));

        static long Saturate(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    }

    /// <summary>The documents a VTPK carries.</summary>
    /// <param name="Service">The service document, <c>p12/root.json</c>.</param>
    /// <param name="Style">The style, <c>p12/resources/styles/root.json</c>.</param>
    /// <param name="Resources">Everything else under <c>p12/resources/</c>, by path below it.</param>
    /// <param name="ItemInfo"><c>esriinfo/iteminfo.xml</c>.</param>
    /// <param name="PackageInfo"><c>esriinfo/item.pkinfo</c>.</param>
    internal sealed record VtpkDocuments(
        byte[] Service,
        byte[] Style,
        IReadOnlyDictionary<string, byte[]> Resources,
        byte[] ItemInfo,
        byte[] PackageInfo);

    /// <summary>Writes a VTPK from the staged tiles.</summary>
    /// <param name="output">Where to — the package file.</param>
    /// <param name="staging">The staged tiles.</param>
    /// <param name="documents">The documents.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task WriteVtpkAsync(
        Stream output, TileExportStaging staging, VtpkDocuments documents, CancellationToken cancellationToken)
    {
        using VectorTilePackage package = new(output);

        package.Add(VectorTilePackage.PackageInfo, documents.PackageInfo);
        package.Add(VectorTilePackage.ItemInfo, documents.ItemInfo);
        package.Add(VectorTilePackage.ServiceDocument, documents.Service);
        package.Add(VectorTilePackage.StyleDocument, documents.Style);

        foreach ((string path, byte[] bytes) in documents.Resources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            package.Add(VectorTilePackage.ResourcePath(path), bytes);
        }

        // One bundle per level and 128 × 128 block, lowest level first and then row by row, so the package's order
        // is the walk's.
        IEnumerable<IGrouping<(int Z, int Row, int Column), StagedTile>> bundles = staging.Tiles
            .GroupBy(tile =>
            {
                (int row, int column) = CompactCacheBundle.OriginOf(tile.Address.Y, tile.Address.X);
                return (Z: tile.Address.Z, Row: row, Column: column);
            })
            .OrderBy(group => group.Key.Z).ThenBy(group => group.Key.Row).ThenBy(group => group.Key.Column);

        foreach (IGrouping<(int Z, int Row, int Column), StagedTile> bundle in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<StagedTile> tiles = [.. bundle];

            CompactCacheBundle.Layout layout = CompactCacheBundle.Plan(
                bundle.Key.Row,
                bundle.Key.Column,
                [.. tiles.Select((tile, i) => new BundleTile(tile.Address.Y, tile.Address.X, tile.Length, i))]);

            await package.AddBundleAsync(
                    bundle.Key.Z,
                    layout,
                    (tile, token) => staging.ReadAsync(tiles[(int)tile.Key].Offset, tile.Length, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Writes a PMTiles archive from the staged tiles.</summary>
    /// <param name="output">Where to.</param>
    /// <param name="staging">The staged tiles.</param>
    /// <param name="description">The header's and the metadata's facts.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task WritePmTilesAsync(
        Stream output, TileExportStaging staging, PmTilesDescription description, CancellationToken cancellationToken)
    {
        IReadOnlyList<StagedTile> staged = staging.Tiles;

        PmTiles.Layout layout = PmTiles.Plan(
            [.. staged.Select((tile, i) => new PmTile(
                tile.Address.Z, tile.Address.X, tile.Address.Y, tile.Length, tile.Content, i))],
            description,
            PmTiles.CompressionGzip);

        await PmTiles.WriteAsync(
                output,
                layout,
                (tile, token) => staging.ReadAsync(staged[(int)tile.Key].Offset, tile.Length, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The documents a VTPK of this service carries — the served ones, adjusted as the type's remarks say.</summary>
    internal static async Task<VtpkDocuments> VtpkDocumentsAsync(
        PublishedService service,
        TileSeedPlan plan,
        Guid id,
        DateTimeOffset now,
        ServiceContexts contexts,
        IProjector projector,
        GlyphStore glyphs,
        StyleOriginList origins,
        PostgresLayerCatalog? store,
        Graticula.Cartography.IMapCanvasFactory canvases,
        CancellationToken cancellationToken)
    {
        VectorTileScheme scheme = service.TileScheme;
        int highest = plan.Levels[^1].Z;

        JsonObject document = (JsonObject)JsonSerializer.SerializeToNode(
            await VectorTileEndpoints.ServiceDocumentAsync(service, contexts, projector, cancellationToken)
                .ConfigureAwait(false),
            Web)!;

        document.Remove("tileMap");
        document["capabilities"] = "TilesOnly";
        document["exportTilesAllowed"] = false;
        document.Remove("maxExportTilesCount");
        document["maxzoom"] = highest;

        if (document.ContainsKey("maxLOD"))
        {
            document["maxLOD"] = highest;
        }

        if (document["tileInfo"] is JsonObject tileInfo && tileInfo["lods"] is JsonArray lods)
        {
            while (lods.Count > highest + 1)
            {
                lods.RemoveAt(lods.Count - 1);
            }
        }

        // The area exported is where a client opens it; the full extent stays the service's.
        document["initialExtent"] = new JsonObject
        {
            ["xmin"] = plan.Area.MinX,
            ["ymin"] = plan.Area.MinY,
            ["xmax"] = plan.Area.MaxX,
            ["ymax"] = plan.Area.MaxY,
            ["spatialReference"] = scheme.IsWebMercator
                ? new JsonObject { ["wkid"] = 102100, ["latestWkid"] = 3857 }
                : new JsonObject { ["wkid"] = scheme.Srid, ["latestWkid"] = scheme.Srid },
        };

        // <b>gzip inside the package, none on the wire.</b> The field describes the stored tiles; the live service
        // stores none. The storage block is the documented service resource's: compact cache V2, packets of 128.
        // Where it sits in the document is INFERRED (ADR-098 §4).
        document["resourceInfo"] = new JsonObject
        {
            ["styleVersion"] = 8,
            ["tileCompression"] = "gzip",
            ["cacheInfo"] = new JsonObject
            {
                ["storageInfo"] = new JsonObject { ["packetSize"] = CompactCacheBundle.PacketSize, ["storageFormat"] = "compactV2" },
            },
        };

        string style = await StyleOfAsync(service, glyphs, origins, store, cancellationToken).ConfigureAwait(false);

        Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);

        await SpritesAsync(service, store, canvases, resources, cancellationToken).ConfigureAwait(false);
        Fonts(style, glyphs, resources);

        // <b>The resource list the documented <c>resources/info</c> resource answers, from the route's own code</b> —
        // a package's `p12` is the service tree file for file (ADR-098 §4), so its list is the live one. Until
        // 2026-09-29 this wrote its own: the style first and every path relative to `resources/`, where the resource
        // sits one folder deeper, so each named a file beside the list that was not there (D-285).
        resources["info/root.json"] = JsonSerializer.SerializeToUtf8Bytes(VectorTileEndpoints.ResourceInfo(style, glyphs), Web);

        Envelope degrees = await DegreesAsync(plan.Area, scheme, projector, cancellationToken).ConfigureAwait(false);

        string levels = plan.Levels.Count == 1
            ? $"level {plan.Levels[0].Z}"
            : $"levels {plan.Levels[0].Z} to {highest}";

        return new VtpkDocuments(
            Encoding.UTF8.GetBytes(document.ToJsonString()),
            Encoding.UTF8.GetBytes(style),
            resources,
            VectorTilePackage.ItemInfoDocument(
                service.Name,
                service.Name,
                $"The vector tiles of {service.QualifiedName}, {levels}, exported {now.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.",
                degrees.MinX,
                degrees.MinY,
                degrees.MaxX,
                degrees.MaxY),
            VectorTilePackage.PackageInfoDocument(id, service.Name, now));
    }

    /// <summary>What a PMTiles archive of this service says about itself — Web Mercator only.</summary>
    internal static async Task<PmTilesDescription> PmTilesDescriptionAsync(
        PublishedService service, TileSeedPlan plan, ServiceContexts contexts, CancellationToken cancellationToken)
    {
        if (!service.TileScheme.IsWebMercator)
        {
            throw new InvalidOperationException(WhyNotPmTiles(service));
        }

        Envelope degrees = ToGeographic(plan.Area);
        int lowest = plan.Levels[0].Z;
        int highest = plan.Levels[^1].Z;

        JsonArray layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            int[] drawn = [.. plan.Levels.Select(level => level.Z).Where(z => service.TileScheme.Draws(layer.VisibleRange, z))];

            if (drawn.Length == 0)
            {
                continue;
            }

            (_, LayerDescription description) = await contexts.GetAsync(layer, cancellationToken).ConfigureAwait(false);

            JsonObject fields = [];

            foreach (FieldDescription field in VectorTileEndpoints.AttributesOf(layer, description))
            {
                fields[field.Name] = field.Type switch
                {
                    FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
                        or FieldType.Single or FieldType.Double => "Number",
                    FieldType.Boolean => "Boolean",
                    _ => "String",
                };
            }

            layers.Add(new JsonObject
            {
                ["id"] = layer.Definition.Name,
                ["fields"] = fields,
                ["minzoom"] = drawn[0],
                ["maxzoom"] = drawn[^1],
            });
        }

        JsonObject metadata = new()
        {
            ["name"] = service.Name,
            ["description"] = $"The vector tiles of {service.QualifiedName}.",
            ["type"] = "overlay",
            ["format"] = "pbf",
            ["vector_layers"] = layers,
        };

        return new PmTilesDescription(
            lowest,
            highest,
            degrees.MinX,
            degrees.MinY,
            degrees.MaxX,
            degrees.MaxY,
            lowest,
            (degrees.MinX + degrees.MaxX) / 2,
            (degrees.MinY + degrees.MaxY) / 2,
            Encoding.UTF8.GetBytes(metadata.ToJsonString()));
    }

    /// <summary>The refusal for a PMTiles export of a service cut on another grid.</summary>
    internal static string WhyNotPmTiles(PublishedService service) =>
        $"'{service.QualifiedName}' is tiled on {service.TileScheme.Id} (EPSG:{service.TileScheme.Srid}), and a PMTiles "
        + "archive addresses tiles by z/x/y on the Web Mercator grid alone — its specification has nowhere to say another "
        + "grid. Export it as a VTPK, which carries its own tiling scheme.";

    /// <summary>The style the style route would serve, as text.</summary>
    internal static async Task<string> StyleOfAsync(
        PublishedService service,
        GlyphStore glyphs,
        StyleOriginList origins,
        PostgresLayerCatalog? store,
        CancellationToken cancellationToken)
    {
        if (service.Style is { Length: > 0 } stored
            && (await VectorTileEndpoints.StoredStyleFitsNowAsync(service, stored, store, origins, cancellationToken)
                .ConfigureAwait(false)).Fits)
        {
            return stored;
        }

        return JsonSerializer.Serialize(VectorTileEndpoints.GeneratedStyle(service, glyphs), Web);
    }

    /// <summary>
    /// The four sprite files, as the sprite routes serve them: the uploaded sheet, or the empty one, with the
    /// layers' picture markers packed in beneath it.
    /// </summary>
    /// <remarks>
    /// <b>The routes' own code, so the style a package carries finds its icons in the sheet beside it</b> — ADR-099
    /// §5.4. Until then this read the uploaded sheet itself, which was the same answer while the routes served
    /// nothing else.
    /// </remarks>
    private static async Task SpritesAsync(
        PublishedService service,
        PostgresLayerCatalog? store,
        Graticula.Cartography.IMapCanvasFactory canvases,
        Dictionary<string, byte[]> resources,
        CancellationToken cancellationToken)
    {
        foreach ((string name, int ratio, bool image) in SpriteFiles)
        {
            resources["sprites/" + name] = await GeneratedSprites
                .FileAsync(service, ratio, image, store, canvases, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Every glyph range of every font stack the style names, under the stack's name as the style asks for it.
    /// </summary>
    /// <remarks>
    /// <b>Every range the server has, not the ones the data needs</b>, because which characters an offline map will
    /// label is not known when it is packed; the shipped font is 31 ranges and 4.3 MB. A stack the server does not have
    /// is answered with the one it does, exactly as the font route answers it.
    /// </remarks>
    internal static void Fonts(string style, GlyphStore glyphs, Dictionary<string, byte[]> resources)
    {
        foreach ((string stack, string range) in FontFiles(style, glyphs))
        {
            if (glyphs.TryRead(stack, range, out byte[] bytes, out _))
            {
                resources[$"fonts/{stack}/{range}.pbf"] = bytes;
            }
        }
    }

    /// <summary>
    /// Which glyph ranges of which stacks <see cref="Fonts"/> packs — split out on 2026-09-29 so the live
    /// <c>resources/info</c> resource lists the same files without reading them (D-285).
    /// </summary>
    /// <param name="style">The style, as served.</param>
    /// <param name="glyphs">The server's glyphs.</param>
    /// <returns>Each stack as the style names it, and each range the server can answer for it.</returns>
    internal static IReadOnlyList<(string Stack, string Range)> FontFiles(string style, GlyphStore glyphs)
    {
        List<(string Stack, string Range)> files = [];

        if (!glyphs.Any)
        {
            return files;
        }

        HashSet<string> stacks = new(StringComparer.Ordinal);
        bool glyphed;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(style);
            JsonElement root = parsed.RootElement;

            glyphed = root.TryGetProperty("glyphs", out JsonElement g)
                && g.ValueKind == JsonValueKind.String
                && g.GetString() is { } url
                && !url.Contains("://", StringComparison.Ordinal);

            if (root.TryGetProperty("layers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement layer in layers.EnumerateArray())
                {
                    if (!layer.TryGetProperty("layout", out JsonElement layout) || layout.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (layout.TryGetProperty("text-font", out JsonElement font) && StackOf(font) is { } stack)
                    {
                        stacks.Add(stack);
                    }
                    else if (layout.TryGetProperty("text-field", out _))
                    {
                        // MapLibre's default stack for a label that names none.
                        stacks.Add("Open Sans Regular,Arial Unicode MS Regular");
                    }
                }
            }
        }
        catch (JsonException)
        {
            return files;
        }

        if (!glyphed)
        {
            return files;
        }

        if (stacks.Count == 0)
        {
            stacks.Add(GlyphStore.Fallback);
        }

        foreach (string stack in stacks)
        {
            if (stack.Contains('/', StringComparison.Ordinal) || stack.Contains('\\', StringComparison.Ordinal)
                || stack.Contains("..", StringComparison.Ordinal) || stack.Any(char.IsControl))
            {
                continue;
            }

            for (int start = 0; start < 65536; start += 256)
            {
                string range = string.Create(CultureInfo.InvariantCulture, $"{start}-{start + 255}");

                if (glyphs.Has(stack, range))
                {
                    files.Add((stack, range));
                }
            }
        }

        return files;

        static string? StackOf(JsonElement font)
        {
            if (font.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // ["literal", ["A", "B"]] is the expression form of a plain list.
            if (font.GetArrayLength() == 2 && font[0].ValueKind == JsonValueKind.String
                && font[0].GetString() == "literal" && font[1].ValueKind == JsonValueKind.Array)
            {
                font = font[1];
            }

            List<string> names = [];

            foreach (JsonElement name in font.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                names.Add(name.GetString()!);
            }

            return names.Count == 0 ? null : string.Join(",", names);
        }
    }

    /// <summary>An area on the service's grid, in degrees — the closed formula for Web Mercator, the projector otherwise.</summary>
    private static async Task<Envelope> DegreesAsync(
        Envelope area, VectorTileScheme scheme, IProjector projector, CancellationToken cancellationToken)
    {
        if (scheme.IsWebMercator)
        {
            return ToGeographic(area);
        }

        try
        {
            return await ServedExtent.InAsync(area, scheme.Srid, 4326, projector, cancellationToken).ConfigureAwait(false)
                ?? new Envelope(-180, -90, 180, 90);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Envelope(-180, -90, 180, 90);
        }
    }

    /// <summary>A Web Mercator box in longitude and latitude — the inverse of <see cref="TileSeedPlan.FromGeographic"/>.</summary>
    internal static Envelope ToGeographic(Envelope mercator)
    {
        return new Envelope(X(mercator.MinX), Y(mercator.MinY), X(mercator.MaxX), Y(mercator.MaxY));

        static double X(double x) => x / TileAddress.WebMercatorHalfExtent * 180.0;

        static double Y(double y) =>
            (2 * Math.Atan(Math.Exp(y / TileAddress.WebMercatorHalfExtent * Math.PI)) - (Math.PI / 2)) * 180.0 / Math.PI;
    }
}
