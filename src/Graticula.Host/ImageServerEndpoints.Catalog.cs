using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Coverages;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// An image service's catalog — ADR-152: its images as rows, ArcGIS's <c>query</c> over them, and <c>mosaicRule</c>
/// choosing which are drawn and which is on top; ADR-153: <c>time</c> choosing them by when they were taken.
/// </summary>
internal static partial class ImageServerEndpoints
{
    /// <summary>The catalog's fields, as ArcGIS lists a mosaic dataset's.</summary>
    private static readonly object[] CatalogFields =
    [
        new { name = "OBJECTID", type = "esriFieldTypeOID", alias = "OBJECTID" },
        new { name = "Name", type = "esriFieldTypeString", alias = "Name", length = 200 },
        new { name = "AcquisitionDate", type = "esriFieldTypeDate", alias = "Acquisition Date", length = 8 },
        new { name = "ZOrder", type = "esriFieldTypeInteger", alias = "ZOrder" },
        new { name = "LowPS", type = "esriFieldTypeDouble", alias = "LowPS" },
        new { name = "CenterX", type = "esriFieldTypeDouble", alias = "CenterX" },
        new { name = "CenterY", type = "esriFieldTypeDouble", alias = "CenterY" },
        new { name = "Shape_Area", type = "esriFieldTypeDouble", alias = "Shape_Area" },
    ];

    private static readonly Dictionary<string, FieldType> CatalogTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OBJECTID"] = FieldType.Integer,
        ["Name"] = FieldType.Text,
        ["AcquisitionDate"] = FieldType.Date,
        ["ZOrder"] = FieldType.Integer,
        ["LowPS"] = FieldType.Double,
        ["CenterX"] = FieldType.Double,
        ["CenterY"] = FieldType.Double,
        ["Shape_Area"] = FieldType.Double,
    };

    /// <summary>
    /// The catalog's rows: a mosaic's images in its order, or the one image of a service over one file — each with the
    /// object id, name and date its catalog recorded, or its position from one and its file's name where none was.
    /// </summary>
    internal static async Task<List<CatalogImage>> CatalogAsync(
        PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;
        IReadOnlyList<(string Path, Envelope Extent)> placed;

        if (Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path))
        {
            using ICoverageReader reader = await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
            placed = reader is Graticula.Raster.Tiff.VrtMosaicReader mosaic ? mosaic.Placed : [(coverage.Path, info.Extent)];
        }
        else
        {
            placed = [(coverage.Path, info.Extent)];
        }

        Dictionary<string, CoverageImageEntry> recorded = (coverage.Images ?? [])
            .GroupBy(i => i.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        double pixel = info.Extent.Width / info.Width;

        return [.. placed.Select((p, position) => recorded.TryGetValue(Path.GetFileName(p.Path), out CoverageImageEntry? entry)
            ? new CatalogImage(entry.Id, entry.Name, p.Extent, pixel, entry.Acquired, position)
            : new CatalogImage(position + 1, UnrecordedName(p.Path, position + 1), p.Extent, pixel, null, position))];
    }

    /// <summary>
    /// ArcGIS's <c>timeInfo</c> when any of the service's images says when it was taken — ADR-153 — else null, and a
    /// service without time has none.
    /// </summary>
    private static object? TimeInfoOf(PublishedCoverage coverage)
    {
        DateTimeOffset[] dates = [.. (coverage.Images ?? []).Where(i => i.Acquired is not null).Select(i => i.Acquired!.Value)];
        return dates.Length == 0
            ? null
            : new
            {
                startTimeField = "AcquisitionDate",
                endTimeField = (string?)null,
                timeExtent = new[] { dates.Min().ToUnixTimeMilliseconds(), dates.Max().ToUnixTimeMilliseconds() },
                timeReference = (object?)null,
                defaultTimeInterval = 1,
                defaultTimeIntervalUnits = "esriTimeUnitsDays",
            };
    }

    /// <summary>
    /// A name for an image no catalog named: its file's, unless the file is one this server named for an upload — 32
    /// hexadecimal characters — when it is "Image" and its number, since the name it was sent as was not kept then.
    /// </summary>
    private static string UnrecordedName(string path, int id)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length == 32 && stem.All(Uri.IsHexDigit)
            ? "Image " + id.ToString(CultureInfo.InvariantCulture)
            : Path.GetFileName(path);
    }

    /// <summary>The catalog as it is stored, from the rows, for writing back after a change.</summary>
    internal static List<CoverageImageEntry> Entries(PublishedCoverage coverage, IReadOnlyList<CatalogImage> images, IReadOnlyList<string> files) =>
        [.. images.Select(i => new CoverageImageEntry(i.Id, Path.GetFileName(files[i.Position]), i.Name, i.Acquired))];

    /// <summary>
    /// The object ids a <c>where</c> keeps — evaluated by the platform store over the rows given to it as JSON, through
    /// the same parser every other <c>where</c> on this server goes through, so the whole of ArcGIS's grammar works and
    /// nothing a caller writes is pasted into a statement.
    /// </summary>
    internal static async Task<(HashSet<int>? Kept, string? Error)> WhereAsync(
        HttpContext context, IReadOnlyList<CatalogImage> images, string? where, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(where) || where.Trim() == "1=1")
        {
            return (null, null);
        }

        if (!WhereClause.TryParse(where, [.. CatalogTypes.Keys], LayerDefinition.Quote, out ParsedWhere parsed, out string? error, CatalogTypes))
        {
            return (null, $"The catalog's where could not be parsed. {error}");
        }

        NpgsqlDataSource db = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
        string rows = JsonSerializer.Serialize(images.Select(i => new Dictionary<string, object?>
        {
            ["OBJECTID"] = i.Id,
            ["Name"] = i.Name,
            ["AcquisitionDate"] = i.Acquired,
            ["ZOrder"] = i.Position,
            ["LowPS"] = i.PixelSize,
            ["CenterX"] = (i.Extent.MinX + i.Extent.MaxX) / 2,
            ["CenterY"] = (i.Extent.MinY + i.Extent.MaxY) / 2,
            ["Shape_Area"] = i.Extent.Width * i.Extent.Height,
        }));

        await using NpgsqlCommand command = db.CreateCommand(
            string.Create(CultureInfo.InvariantCulture,
                $"""
                select "OBJECTID" from jsonb_to_recordset(@catalog_rows::jsonb)
                    as t("OBJECTID" integer, "Name" text, "AcquisitionDate" timestamptz, "ZOrder" integer,
                         "LowPS" double precision, "CenterX" double precision, "CenterY" double precision,
                         "Shape_Area" double precision)
                 where {parsed.Sql}
                """));

        // The parser's own names, @w0 and on, as every PostgreSQL face binds them.
        for (int i = 0; i < parsed.Parameters.Count; i++)
        {
            command.Parameters.AddWithValue("w" + i.ToString(CultureInfo.InvariantCulture), parsed.Parameters[i] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("catalog_rows", rows);
        HashSet<int> kept = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            kept.Add(reader.GetInt32(0));
        }

        return (kept, null);
    }

    /// <summary>
    /// ADR-153: <c>time</c> — one instant or two, epoch milliseconds — keeps the images taken in it: within the two, or,
    /// for one, those taken by then, the nearest on top. An image nobody dated is in no time.
    /// </summary>
    internal static bool TryTime(string? text, out (long From, long To)? window, out bool instant, out string? error)
    {
        window = null;
        instant = false;
        error = null;

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        long?[] values = [.. parts.Select(p => p.Equals("null", StringComparison.OrdinalIgnoreCase) || p.Length == 0
            ? (long?)null
            : long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : long.MinValue)];

        if (parts.Length > 2 || values.Any(v => v == long.MinValue))
        {
            error = $"`time={text}` is one instant or two, as milliseconds since 1970, separated by a comma.";
            return false;
        }

        instant = parts.Length == 1;
        window = instant ? (long.MinValue, values[0]!.Value) : (values[0] ?? long.MinValue, values[1] ?? long.MaxValue);
        return true;
    }

    /// <summary>
    /// Whether a request's <c>mosaicRule</c> and <c>time</c> are ones this server reads — checked before anything else,
    /// so a point off the image is not answered NoData with a refusal still owed (the conformance suite, 2026-10-03).
    /// </summary>
    internal static string? MosaicParametersError(Func<string, string?> parameter) =>
        !MosaicRule.TryParse(parameter("mosaicRule"), out _, out string? rule) ? rule
        : !TryTime(parameter("time"), out _, out _, out string? time) ? time
        : null;

    /// <summary>
    /// The readers a request draws through: the service's own, or, under a <c>mosaicRule</c> or a <c>time</c>, its
    /// images chosen and ordered — ADR-152, ADR-153.
    /// </summary>
    internal static async Task<(ICoverageReaderFactory Readers, string? Error)> MosaicReadersAsync(
        HttpContext context,
        Func<string, string?> parameter,
        PublishedCoverage coverage,
        ICoverageReaderFactory readers,
        Envelope view,
        CancellationToken cancellation)
    {
        if (!MosaicRule.TryParse(parameter("mosaicRule"), out MosaicRule? rule, out string? error))
        {
            return (readers, error);
        }

        if (!TryTime(parameter("time"), out (long From, long To)? window, out bool instant, out error))
        {
            return (readers, error);
        }

        if (rule is null && window is null)
        {
            return (readers, null);
        }

        List<CatalogImage> images = await CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);

        // A service whose images say no time has none: `time` is refused, as before ADR-153, saying how to give it one.
        if (window is not null && images.All(i => i.Acquired is null))
        {
            return (readers, "`time` asks for a moment, and this image service has no time: none of its images says when it "
                + "was taken. Date them under Settings › Images, or with ArcGIS's update and AcquisitionDate.");
        }

        (HashSet<int>? kept, string? refused) = await WhereAsync(context, images, rule?.Where, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return (readers, refused);
        }

        if (kept is not null)
        {
            images = [.. images.Where(i => kept.Contains(i.Id))];
        }

        if (window is { } span)
        {
            images = [.. images.Where(i => i.Acquired is { } when
                && when.ToUnixTimeMilliseconds() >= span.From && when.ToUnixTimeMilliseconds() <= span.To)];
        }

        // An instant with no rule of its own puts the image taken nearest it on top.
        rule ??= instant && window is { } at
            ? new MosaicRule { Method = MosaicMethod.Attribute, SortField = "AcquisitionDate", SortValue = at.To.ToString(CultureInfo.InvariantCulture) }
            : new MosaicRule { FirstOnTop = false };

        return (new ArrangedReaders(readers, coverage.Path, rule.DrawingOrder(images, view)), null);
    }

    /// <summary>A coverage's reader with its mosaic's images drawn in an order, or none of them — ADR-152.</summary>
    private sealed class ArrangedReaders(ICoverageReaderFactory inner, string path, IReadOnlyList<int> order) : ICoverageReaderFactory
    {
        public async Task<ICoverageReader> OpenAsync(string opened, CancellationToken cancellationToken)
        {
            ICoverageReader reader = await inner.OpenAsync(opened, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(opened, path, StringComparison.Ordinal))
            {
                return reader;
            }

            if (reader is Graticula.Raster.Tiff.VrtMosaicReader mosaic)
            {
                Graticula.Raster.Tiff.VrtMosaicReader arranged = mosaic.Arranged(order);
                mosaic.Dispose();
                return arranged;
            }

            // One image: drawn when the rule keeps it, nothing when it does not.
            return order.Count > 0 ? reader : new NothingReader(reader);
        }
    }

    /// <summary>An image read as having nothing in it: what a rule that keeps none of a service's images draws.</summary>
    private sealed class NothingReader(ICoverageReader inner) : ICoverageReader
    {
        public CoverageInfo Info => inner.Info;

        public Task<CoverageWindow> ReadAsync(int overview, int x, int y, int width, int height, CancellationToken cancellationToken)
        {
            int bands = Info.Bands.Count;
            double[] samples = new double[width * height * bands];
            Array.Fill(samples, Info.Bands[0].NoData ?? double.NaN);
            return Task.FromResult(new CoverageWindow(width, height, bands, samples));
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// ArcGIS's <c>query</c> on an image service: its catalog's rows — ADR-152 — with their footprints, filtered by
    /// <c>objectIds</c>, <c>where</c>, an extent and <c>time</c>, as ids, a count, or features.
    /// </summary>
    private static async Task CatalogQueryAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        List<CatalogImage> images = await CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);

        if (parameter("objectIds") is { Length: > 0 } ids)
        {
            HashSet<int> wanted = [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)];
            images = [.. images.Where(i => wanted.Contains(i.Id))];
        }

        (HashSet<int>? kept, string? error) = await WhereAsync(context, images, parameter("where"), cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await RefuseAsync(context, 400, error).ConfigureAwait(false);
            return;
        }

        if (kept is not null)
        {
            images = [.. images.Where(i => kept.Contains(i.Id))];
        }

        if (!TryTime(parameter("time"), out (long From, long To)? window, out _, out error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        if (window is { } span)
        {
            images = [.. images.Where(i => i.Acquired is { } when
                && when.ToUnixTimeMilliseconds() >= span.From && when.ToUnixTimeMilliseconds() <= span.To)];
        }

        // An extent, in the service's reference: the images whose footprints touch it.
        if (parameter("geometry") is { Length: > 0 } geometry)
        {
            if (!TryCatalogExtent(geometry, coverage.Info.Srid, parameter("inSR"), out Envelope? box, out error))
            {
                await RefuseAsync(context, 400, error!).ConfigureAwait(false);
                return;
            }

            Envelope touching = box!.Value;
            images = [.. images.Where(i => i.Extent.MinX <= touching.MaxX && i.Extent.MaxX >= touching.MinX
                && i.Extent.MinY <= touching.MaxY && i.Extent.MaxY >= touching.MinY)];
        }

        int srid = coverage.Info.Srid;

        if (string.Equals(parameter("returnCountOnly"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await Microsoft.AspNetCore.Http.Results.Ok(new { count = images.Count }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (string.Equals(parameter("returnIdsOnly"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await Microsoft.AspNetCore.Http.Results.Ok(new { objectIdFieldName = "OBJECTID", objectIds = images.Select(i => i.Id) })
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        bool geometries = !string.Equals(parameter("returnGeometry"), "false", StringComparison.OrdinalIgnoreCase);
        HashSet<string>? fields = parameter("outFields") is { Length: > 0 } outFields && outFields.Trim() != "*"
            ? [.. outFields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
            : null;

        Dictionary<string, object?> Attributes(CatalogImage i)
        {
            Dictionary<string, object?> all = new(StringComparer.OrdinalIgnoreCase)
            {
                ["OBJECTID"] = i.Id,
                ["Name"] = i.Name,
                ["AcquisitionDate"] = i.Acquired?.ToUnixTimeMilliseconds(),
                ["ZOrder"] = i.Position,
                ["LowPS"] = i.PixelSize,
                ["CenterX"] = (i.Extent.MinX + i.Extent.MaxX) / 2,
                ["CenterY"] = (i.Extent.MinY + i.Extent.MaxY) / 2,
                ["Shape_Area"] = i.Extent.Width * i.Extent.Height,
            };

            return fields is null ? all : all.Where(p => fields.Contains(p.Key, StringComparer.OrdinalIgnoreCase) || p.Key == "OBJECTID")
                .ToDictionary(p => p.Key, p => p.Value);
        }

        await Microsoft.AspNetCore.Http.Results.Ok(new
        {
            objectIdFieldName = "OBJECTID",
            geometryType = "esriGeometryPolygon",
            spatialReference = new { wkid = srid, latestWkid = srid },
            fields = CatalogFields,
            features = images.Select(i => new
            {
                attributes = Attributes(i),
                geometry = geometries
                    ? new
                    {
                        rings = new[]
                        {
                            new[]
                            {
                                new[] { i.Extent.MinX, i.Extent.MinY }, new[] { i.Extent.MinX, i.Extent.MaxY },
                                new[] { i.Extent.MaxX, i.Extent.MaxY }, new[] { i.Extent.MaxX, i.Extent.MinY },
                                new[] { i.Extent.MinX, i.Extent.MinY },
                            },
                        },
                        spatialReference = new { wkid = srid },
                    }
                    : null,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>An extent or point a catalog query names, in the service's own reference.</summary>
    private static bool TryCatalogExtent(string text, int srid, string? inSR, out Envelope? box, out string? error)
    {
        box = null;
        error = null;

        if (inSR is { Length: > 0 } && !inSR.Trim().Equals(srid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && !inSR.Contains($":{srid.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal))
        {
            error = $"The catalog is queried in the service's own reference, EPSG:{srid.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement g = document.RootElement;

            if (g.TryGetProperty("xmin", out JsonElement xmin))
            {
                box = new Envelope(xmin.GetDouble(), g.GetProperty("ymin").GetDouble(), g.GetProperty("xmax").GetDouble(), g.GetProperty("ymax").GetDouble());
                return true;
            }

            if (g.TryGetProperty("x", out JsonElement x))
            {
                double px = x.GetDouble(), py = g.GetProperty("y").GetDouble();
                box = new Envelope(px, py, px, py);
                return true;
            }

            if (g.TryGetProperty("rings", out JsonElement rings))
            {
                double[] xs = [.. rings.EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(p => p[0].GetDouble())];
                double[] ys = [.. rings.EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(p => p[1].GetDouble())];
                box = new Envelope(xs.Min(), ys.Min(), xs.Max(), ys.Max());
                return true;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
        }

        if (text.Split(',') is { Length: 4 } parts
            && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            double[] v = [.. parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture))];
            box = new Envelope(v[0], v[1], v[2], v[3]);
            return true;
        }

        error = "The catalog's `geometry` is an envelope, a point or a polygon, as ArcGIS JSON, or xmin,ymin,xmax,ymax.";
        return false;
    }
}
