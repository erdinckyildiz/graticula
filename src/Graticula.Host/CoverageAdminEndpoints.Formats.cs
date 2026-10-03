using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Imagery in other formats — ADR-157: a file this server does not read itself is written as the GeoTIFFs it does, by
/// GDAL in the import reader's own process, on its way in; a NetCDF of time steps becomes a dated image a step.
/// </summary>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>An image received: where it is now, as a GeoTIFF, what to call it, and when it was taken, if its file said.</summary>
    internal sealed record Arrived(string Path, string Name, DateTimeOffset? Acquired);

    /// <summary>Formats GDAL writes as GeoTIFFs on the way in — the open drivers this build carries (ADR-157).</summary>
    private static readonly Dictionary<string, string> Translated = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jp2"] = "JPEG 2000",
        [".j2k"] = "JPEG 2000",
        [".nc"] = "NetCDF",
        [".nc4"] = "NetCDF",
        [".cdf"] = "NetCDF",
        [".h5"] = "HDF5",
        [".hdf5"] = "HDF5",
        [".he5"] = "HDF-EOS5",
        [".hdf"] = "HDF4",
        [".img"] = "ERDAS Imagine",
        [".asc"] = "ASCII grid",
    };

    /// <summary>The accepted formats, said where an upload is refused.</summary>
    internal static string AcceptedFormats =>
        "GeoTIFF (.tif, .tiff), JPEG 2000 (.jp2), NetCDF (.nc), HDF (.h5, .hdf), ERDAS Imagine (.img) and ASCII grids (.asc)";

    /// <summary>Whether a file name is one an image service takes.</summary>
    internal static bool Receivable(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".tif" or ".tiff" || Translated.ContainsKey(Path.GetExtension(fileName));

    /// <summary>
    /// Writes what was sent into the imagery directory as GeoTIFFs: a GeoTIFF as it is, another format through GDAL.
    /// Refuses in its own answer, naming the file, and returns null when one cannot be taken; every file written is
    /// in <paramref name="written"/>, for the caller to remove if it does not publish.
    /// </summary>
    internal static async Task<List<Arrived>?> ReceiveAsync(
        HttpContext context, IReadOnlyList<Incoming> sent, string directory, List<string> written, CancellationToken cancellation)
    {
        List<Arrived> arrived = [];

        foreach (Incoming file in sent)
        {
            string named = Path.GetFileName(file.Name);
            string extension = Path.GetExtension(named).ToLowerInvariant();

            if (extension is ".sid" or ".ecw")
            {
                await Refuse(context, 400, $"{named}: {(extension == ".sid" ? "MrSID" : "ECW")} is read only with its maker's "
                    + "SDK, whose licence keeps it out of an open build, so this server does not read it. Export it as a GeoTIFF "
                    + "or JPEG 2000 — ArcGIS Pro and the maker's own tools do — and upload that.").ConfigureAwait(false);
                return null;
            }

            if (extension is ".tif" or ".tiff")
            {
                string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");
                written.Add(path);
                await file.WriteTo(path, cancellation).ConfigureAwait(false);
                arrived.Add(new Arrived(path, named, null));
                continue;
            }

            if (!Translated.TryGetValue(extension, out string? format))
            {
                await Refuse(context, 400, $"{named}: an image service is published from {AcceptedFormats}.").ConfigureAwait(false);
                return null;
            }

            GeodatabaseReader reader = context.RequestServices.GetRequiredService<GeodatabaseReader>();

            if (!reader.Available)
            {
                await Refuse(context, 400, $"{named} is {format}, which this server reads through its import reader, and the "
                    + "reader is not installed beside it. Upload a GeoTIFF, or install the server with its import reader.").ConfigureAwait(false);
                return null;
            }

            string raw = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
            written.Add(raw);
            await file.WriteTo(raw, cancellation).ConfigureAwait(false);

            using JsonDocument answer = await reader.AskAsync(
                new { op = "raster", @in = raw, @out = directory, name = named }, TimeSpan.FromMinutes(15), cancellation).ConfigureAwait(false);
            JsonElement root = answer.RootElement;
            File.Delete(raw);

            if (!root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
            {
                string why = root.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? "it could not be read" : "it could not be read";
                await Refuse(context, 400, $"{named} could not be read as {format}: {why.TrimEnd('.')}.").ConfigureAwait(false);
                return null;
            }

            foreach (JsonElement image in root.GetProperty("images").EnumerateArray())
            {
                string path = image.GetProperty("path").GetString()!;
                written.Add(path);
                DateTimeOffset? acquired = image.TryGetProperty("acquired", out JsonElement when) && when.ValueKind == JsonValueKind.String
                    ? when.GetDateTimeOffset() : null;
                arrived.Add(new Arrived(path, image.GetProperty("name").GetString() ?? named, acquired));
            }
        }

        return arrived;
    }
}
