using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// A classified image's raster attribute table — ADR-154: its classes' names and colours, from its owner or from the
/// <c>.aux.xml</c> GDAL and ArcGIS write beside an image, answered as ArcGIS's <c>rasterAttributeTable</c>, named by
/// <c>identify</c>, listed by the legend and drawn.
/// </summary>
internal static partial class ImageServerEndpoints
{
    /// <summary>The table an image service has: its owner's classes, else the file's own, else none.</summary>
    internal static RasterAttributeTable? AttributeTableOf(PublishedCoverage coverage)
    {
        if (coverage.Classes is { Count: > 0 } named)
        {
            return new RasterAttributeTable(named.Select(c => new AttributeClass(c.Value, c.Name, RasterAttributeTable.FromHex(c.Colour))));
        }

        return coverage.Info.Bands.Count == 1 && !Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path)
            ? Graticula.Raster.Tiff.PamAttributeTable.TryRead(coverage.Path)
            : null;
    }

    /// <summary>ArcGIS's <c>rasterAttributeTable</c>: a row per class — value, pixel count where known, name and colour.</summary>
    private static async Task RasterAttributeTableAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (AttributeTableOf(coverage) is not { } table)
        {
            await RefuseAsync(context, 400, "This image service has no raster attribute table: its values are measurements, "
                + "not classes, or its owner has not named them.").ConfigureAwait(false);
            return;
        }

        bool counted = table.Classes.Any(c => c.Count is not null);
        List<object> fields =
        [
            new { name = "OBJECTID", type = "esriFieldTypeOID", alias = "OBJECTID" },
            new { name = "Value", type = "esriFieldTypeInteger", alias = "Value" },
        ];

        if (counted)
        {
            fields.Add(new { name = "Count", type = "esriFieldTypeDouble", alias = "Count" });
        }

        fields.Add(new { name = "ClassName", type = "esriFieldTypeString", alias = "ClassName" });
        fields.Add(new { name = "Red", type = "esriFieldTypeInteger", alias = "Red" });
        fields.Add(new { name = "Green", type = "esriFieldTypeInteger", alias = "Green" });
        fields.Add(new { name = "Blue", type = "esriFieldTypeInteger", alias = "Blue" });

        await Microsoft.AspNetCore.Http.Results.Ok(new
        {
            objectIdFieldName = "OBJECTID",
            fields,
            features = table.Classes.Select((c, i) =>
            {
                Dictionary<string, object?> attributes = new()
                {
                    ["OBJECTID"] = i + 1,
                    ["Value"] = c.Value,
                };

                if (counted)
                {
                    attributes["Count"] = c.Count;
                }

                attributes["ClassName"] = c.Name;
                attributes["Red"] = c.Colour?.R;
                attributes["Green"] = c.Colour?.G;
                attributes["Blue"] = c.Colour?.B;
                return new { attributes };
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }
}

/// <summary>Studio's half of ADR-154: an image's classes, read and named by its owner.</summary>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>A class as Studio sends it.</summary>
    internal sealed record ClassRequest(double Value, string? Name, string? Colour);

    /// <summary>The classes Studio saves; null or none clears them.</summary>
    internal sealed record ClassesRequest(IReadOnlyList<ClassRequest>? Classes);

    internal static void MapClasses(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app, "/admin/coverages/{name}/classes", GetClassesAsync);
        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPut(app, "/admin/coverages/{name}/classes", SetClassesAsync);
    }

    /// <summary>
    /// The classes, where they come from, and the values the image holds — read from its coarsest sample, so its owner
    /// can name them without knowing them.
    /// </summary>
    private static async Task GetClassesAsync(
        HttpContext context, string name, string? folder, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        RasterAttributeTable? table = ImageServerEndpoints.AttributeTableOf(coverage);
        BandInfo? band = coverage.Info.Bands.Count == 1 ? coverage.Info.Bands[0] : null;
        bool integers = band is { Kind: SampleKind.Unsigned8 or SampleKind.Unsigned16 or SampleKind.Signed16 or SampleKind.Signed32 };
        List<double> values = [];

        if (integers)
        {
            CoverageWindow? sample = await ImageServerEndpoints.SampleOfAsync(coverage, readers, cancellation).ConfigureAwait(false);
            HashSet<double> seen = [];

            for (int i = 0; sample is not null && i < sample.Samples.Length && seen.Count <= 256; i += sample.Bands)
            {
                double v = sample.Samples[i];

                if (!double.IsNaN(v) && v != band!.NoData)
                {
                    seen.Add(v);
                }
            }

            values = [.. seen.Order()];
        }

        await Results.Json(new
        {
            // Classes mean something for one band of whole numbers; a measurement has none.
            classifiable = integers,
            source = coverage.Classes is { Count: > 0 } ? "owner" : table is not null ? "file" : "none",
            classes = (table?.Classes ?? []).Select(c => new
            {
                value = c.Value,
                name = c.Name,
                colour = c.Colour is { } colour ? RasterAttributeTable.Hex(colour) : null,
            }),
            values = values.Count > 256 ? [] : values,
            tooMany = values.Count > 256,
            // Where the values come from — the coarsest sample, which can miss a rare one (ux review 8).
            sampled = true,
            // Whether clearing brings back the table written beside the file.
            fileTable = coverage.Info.Bands.Count == 1 && !Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path)
                && Graticula.Raster.Tiff.PamAttributeTable.TryRead(coverage.Path) is not null,
            minimum = band?.Kind switch { SampleKind.Unsigned8 or SampleKind.Unsigned16 => 0, SampleKind.Signed16 => short.MinValue, _ => (double?)null },
            maximum = band?.Kind switch { SampleKind.Unsigned8 => 255, SampleKind.Signed16 => short.MaxValue, SampleKind.Unsigned16 => ushort.MaxValue, _ => (double?)null },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetClassesAsync(
        HttpContext context, string name, string? folder, ClassesRequest request, ICoverageCatalog coverages, Graticula.Platform.Admin.IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        List<CoverageClassEntry>? classes = null;

        if (request.Classes is { Count: > 0 } sent)
        {
            if (coverage.Info.Bands.Count != 1)
            {
                await Refuse(context, 400, "Classes name the values of one band; this image has several.").ConfigureAwait(false);
                return;
            }

            if (sent.Count > 1000)
            {
                await Refuse(context, 400, "An image has at most 1,000 classes.").ConfigureAwait(false);
                return;
            }

            if (sent.GroupBy(c => c.Value).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                await Refuse(context, 400, $"Value {twice.Key.ToString(CultureInfo.InvariantCulture)} is used twice; each value is one class.")
                    .ConfigureAwait(false);
                return;
            }

            // A class is a value the band can hold: a whole number in its pixel type's range (ux review 8).
            (double low, double high) = coverage.Info.Bands[0].Kind switch
            {
                SampleKind.Unsigned8 => (0d, 255d),
                SampleKind.Signed16 => (short.MinValue, short.MaxValue),
                SampleKind.Unsigned16 => (0d, ushort.MaxValue),
                _ => (int.MinValue, (double)int.MaxValue),
            };

            if (sent.FirstOrDefault(c => c.Value != Math.Floor(c.Value) || c.Value < low || c.Value > high) is { } outside)
            {
                await Refuse(context, 400, $"Value {outside.Value.ToString(CultureInfo.InvariantCulture)} is not one this image can hold: "
                    + $"a whole number from {low.ToString(CultureInfo.InvariantCulture)} to {high.ToString(CultureInfo.InvariantCulture)}.")
                    .ConfigureAwait(false);
                return;
            }

            if (sent.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 100) is { } unnamed)
            {
                await Refuse(context, 400, $"The class of value {unnamed.Value.ToString(CultureInfo.InvariantCulture)} needs a name of at most 100 characters.")
                    .ConfigureAwait(false);
                return;
            }

            if (sent.FirstOrDefault(c => c.Colour is { Length: > 0 } && RasterAttributeTable.FromHex(c.Colour) is null) is { } badColour)
            {
                await Refuse(context, 400, $"The colour of value {badColour.Value.ToString(CultureInfo.InvariantCulture)} is #rrggbb.").ConfigureAwait(false);
                return;
            }

            classes = [.. sent.Select(c => new CoverageClassEntry(c.Value, c.Name!.Trim(), string.IsNullOrEmpty(c.Colour) ? null : c.Colour.ToLowerInvariant()))];
        }

        await coverages.SetClassesAsync(at, name, classes, cancellation).ConfigureAwait(false);
        await RecordAsync(context, audit, "coverage.classes", Qualify(name, at), new { classes = classes?.Count ?? 0 }, cancellation)
            .ConfigureAwait(false);
        await Results.Ok(new { classes = classes?.Count ?? 0 }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
