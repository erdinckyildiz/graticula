using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// A mosaic's images, one by one — ADR-152: listed, renamed and dated (ADR-153), and removed, from Studio and by
/// ArcGIS's own <c>delete</c>, <c>update</c>, <c>uploads</c> and <c>add</c> on the image service.
/// </summary>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>The routes Studio uses for a service's images.</summary>
    internal static void MapCatalog(WebApplication app)
    {
        app.MapGet("/admin/coverages/{name}/images", ListImagesAsync);
        app.MapPut("/admin/coverages/{name}/images/{id:int}", (HttpContext context, string name, int id, string? folder,
                ImageUpdate update, ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation) =>
            UpdateOneAsync(context, name, folder, id, update, coverages, readers, audit, cancellation));
        app.MapDelete("/admin/coverages/{name}/images/{id:int}", (HttpContext context, string name, int id, string? folder,
                ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation) =>
            DeleteOneAsync(context, name, folder, id, coverages, readers, audit, cancellation));
    }

    /// <summary>A change to one image: its name, when it was taken — epoch milliseconds — or both; null leaves either.</summary>
    internal sealed record ImageUpdate(string? Name, long? Acquired, bool? ClearAcquired);

    private static async Task ListImagesAsync(
        HttpContext context, string name, string? folder, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();
        List<CatalogImage> images = await ImageServerEndpoints.CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);
        List<string> files = Files(coverage);

        await Results.Json(new
        {
            // Whether images may be removed and added: only an upload's, the administrator's files being theirs.
            editable = Uploaded(settings, coverage.Path),
            images = images.Select(i => new
            {
                id = i.Id,
                name = i.Name,
                // The file behind the name, so a renamed image can still be told by what it is (ux review 7).
                file = Path.GetFileNameWithoutExtension(files[i.Position]) is { Length: 32 } stem && stem.All(Uri.IsHexDigit)
                    ? null : Path.GetFileName(files[i.Position]),
                acquired = i.Acquired?.ToUnixTimeMilliseconds(),
                position = i.Position,
                extent = new { xmin = i.Extent.MinX, ymin = i.Extent.MinY, xmax = i.Extent.MaxX, ymax = i.Extent.MaxY },
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task UpdateOneAsync(
        HttpContext context, string name, string? folder, int id, ImageUpdate update, ICoverageCatalog coverages,
        ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(update);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        DateTimeOffset? acquired = update.Acquired is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        string? error = await UpdateImageAsync(context, coverage, at, name, id, update.Name, acquired, update.ClearAcquired == true,
            coverages, readers, audit, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await Refuse(context, 400, error).ConfigureAwait(false);
            return;
        }

        await Results.Ok(new { id, saved = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteOneAsync(
        HttpContext context, string name, string? folder, int id, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        IAuditLog audit, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        (IReadOnlyList<int> removed, string? error) = await RemoveImagesAsync(context, coverage, at, name, [id], coverages, readers, audit, cancellation)
            .ConfigureAwait(false);

        if (error is not null || removed.Count == 0)
        {
            await Refuse(context, error is null ? 404 : 400, error ?? $"This image service has no image {id}.").ConfigureAwait(false);
            return;
        }

        await Results.Ok(new { id, removed = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames an image, or says when it was taken — ADR-152, ADR-153. Returns why not, or null.
    /// </summary>
    internal static async Task<string?> UpdateImageAsync(
        HttpContext context, PublishedCoverage coverage, string? at, string name, int id, string? newName, DateTimeOffset? acquired,
        bool clearAcquired, ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        List<CatalogImage> images = await ImageServerEndpoints.CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);

        if (images.FirstOrDefault(i => i.Id == id) is not { } image)
        {
            return $"This image service has no image {id.ToString(CultureInfo.InvariantCulture)}.";
        }

        string? renamed = newName?.Trim();

        if (renamed is { Length: 0 or > 200 })
        {
            return "An image's name is between 1 and 200 characters.";
        }

        List<string> files = Files(coverage);
        List<CoverageImageEntry> entries = [.. images.Select(i => new CoverageImageEntry(
            i.Id,
            Path.GetFileName(files[i.Position]),
            i.Id == id ? renamed ?? i.Name : i.Name,
            i.Id == id ? (clearAcquired ? null : acquired ?? i.Acquired) : i.Acquired))];

        await coverages.SetImagesAsync(at, name, entries, NextOf(coverage, images), cancellation).ConfigureAwait(false);
        await RecordAsync(context, audit, "coverage.image.update", Qualify(name, at),
            new { image = id, name = renamed, acquired = clearAcquired ? null : acquired }, cancellation).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Removes images from an uploaded image service — ADR-152: a mosaic is written again without them, and their files
    /// and overviews are deleted. The last image is not removed: deleting the service is how that is done.
    /// </summary>
    internal static async Task<(IReadOnlyList<int> Removed, string? Error)> RemoveImagesAsync(
        HttpContext context, PublishedCoverage coverage, string? at, string name, IReadOnlyCollection<int> ids,
        ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

        if (!Uploaded(settings, coverage.Path))
        {
            return ([], $"'{Qualify(name, at)}' was registered from files on this server; they are the administrator's, and "
                + "images are removed only from a service uploaded here.");
        }

        List<CatalogImage> images = await ImageServerEndpoints.CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);
        List<CatalogImage> going = [.. images.Where(i => ids.Contains(i.Id))];

        if (going.Count == 0)
        {
            return ([], null);
        }

        List<CatalogImage> staying = [.. images.Where(i => !ids.Contains(i.Id)).OrderBy(i => i.Position)];

        if (staying.Count == 0)
        {
            return ([], "An image service keeps at least one image. To remove them all, delete the service.");
        }

        List<string> files = Files(coverage);
        string[] kept = [.. staying.Select(i => files[i.Position])];
        string target = kept[0];
        string? written = null;

        // One image left is served as itself; more are a mosaic written again.
        if (kept.Length > 1)
        {
            written = Path.Combine(ImageryDirectory(settings), $"{Guid.NewGuid():N}.vrt");
            Graticula.Raster.Tiff.VrtMosaicReader.Write(written, kept);
            target = written;
        }

        CoverageInfo info;

        using (ICoverageReader reader = await readers.OpenAsync(target, cancellation).ConfigureAwait(false))
        {
            info = reader.Info;
        }

        await coverages.ReplaceImageAsync(at, name, target, info, cancellation).ConfigureAwait(false);
        await coverages.SetImagesAsync(at, name,
            [.. staying.Select(i => new CoverageImageEntry(i.Id, Path.GetFileName(files[i.Position]), i.Name, i.Acquired))],
            NextOf(coverage, images), cancellation)
            .ConfigureAwait(false);

        Forget([.. going.Select(i => files[i.Position])]);

        if (Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path))
        {
            File.Delete(coverage.Path);
        }

        await RecordAsync(context, audit, "coverage.images.remove", Qualify(name, at),
            new { removed = going.Select(i => i.Id), images = staying.Count }, cancellation).ConfigureAwait(false);
        return ([.. going.Select(i => i.Id)], null);
    }

    /// <summary>The next object id: never one an image had, removed or not.</summary>
    private static int NextOf(PublishedCoverage coverage, List<CatalogImage> images) =>
        Math.Max(coverage.NextImageId ?? 1, images.Count == 0 ? 1 : images.Max(i => i.Id) + 1);

    private static List<string> Files(PublishedCoverage coverage) =>
        Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path)
            ? [.. Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path)]
            : [coverage.Path];

    // ---------- ArcGIS's own editing operations on an image service (ADR-152) ----------

    /// <summary>Where ArcGIS's <c>uploads/upload</c> keeps a file until <c>add</c> takes it.</summary>
    private static string UploadsDirectory(HostSettings settings) => Path.Combine(ImageryDirectory(settings), "uploads");

    /// <summary>
    /// <c>uploads/upload</c>: a file kept under an item id for <c>add</c> — the two steps ArcGIS's clients take to add a
    /// raster to an image service. Only whoever may manage the service may upload to it.
    /// </summary>
    internal static async Task RestUploadAsync(
        HttpContext context, string? folder, string name, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await ManagedAsync(context, coverages, name, folder, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaximumUploadBytes;
        }

        if (!context.Request.HasFormContentType)
        {
            await Refuse(context, 400, "Send the file as multipart/form-data, as `file`.").ConfigureAwait(false);
            return;
        }

        context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(
            context.Request, new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = MaximumUploadBytes }));
        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (form.Files.GetFile("file") is not { } file)
        {
            await Refuse(context, 400, "Send the file as `file`.").ConfigureAwait(false);
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();
        string directory = UploadsDirectory(settings);
        Directory.CreateDirectory(directory);
        string item = Guid.NewGuid().ToString("N");

        await using (FileStream into = new(Path.Combine(directory, item + ".bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        await using (Stream from = file.OpenReadStream())
        {
            await from.CopyToAsync(into, cancellation).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, item + ".name"), Path.GetFileName(file.FileName), cancellation).ConfigureAwait(false);

        await Results.Ok(new
        {
            success = true,
            item = new { itemID = item, itemName = Path.GetFileName(file.FileName), committed = true, date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>add</c>: the uploaded items added to the mosaic, as Studio's Add images adds them.</summary>
    internal static async Task RestAddAsync(
        HttpContext context, string? folder, string name, Func<string, string?> parameter, ICoverageCatalog coverages,
        ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedAsync(context, coverages, name, folder, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (parameter("rasterType") is { Length: > 0 } type && !type.Equals("Raster Dataset", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 400, $"`rasterType` '{type}' is not one this server adds: it adds GeoTIFFs as \"Raster Dataset\".")
                .ConfigureAwait(false);
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();
        string directory = UploadsDirectory(settings);
        List<string> items = [.. (parameter("itemIds") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        if (items.Count == 0 || items.Any(i => i.Length != 32 || !i.All(Uri.IsHexDigit) || !File.Exists(Path.Combine(directory, i + ".bin"))))
        {
            await Refuse(context, 400, "`itemIds` names files uploaded to this service's `uploads/upload`, and one of these is not one.")
                .ConfigureAwait(false);
            return;
        }

        List<Incoming> incoming = [];

        foreach (string item in items)
        {
            string from = Path.Combine(directory, item + ".bin");
            string named = await File.ReadAllTextAsync(Path.Combine(directory, item + ".name"), cancellation).ConfigureAwait(false);
            incoming.Add(new Incoming(named, (path, token) =>
            {
                File.Copy(from, path);
                return Task.CompletedTask;
            }));
        }

        AddedImages? done = await AddIncomingAsync(context, coverage, folder, name, incoming, coverages, readers, audit, cancellation)
            .ConfigureAwait(false);

        foreach (string item in items)
        {
            File.Delete(Path.Combine(directory, item + ".bin"));
            File.Delete(Path.Combine(directory, item + ".name"));
        }

        if (done is not null)
        {
            await Results.Ok(new { addResults = done.NewIds.Select(id => new { rasterId = id, success = true }) })
                .ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary><c>delete</c>: images removed by object id, each answered.</summary>
    internal static async Task RestDeleteAsync(
        HttpContext context, string? folder, string name, Func<string, string?> parameter, ICoverageCatalog coverages,
        ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedAsync(context, coverages, name, folder, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        List<int> ids = [.. (parameter("rasterIds") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(i => int.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)];

        if (ids.Count == 0 || ids.Contains(-1))
        {
            await Refuse(context, 400, "`rasterIds` is the object ids of the images to delete, separated by commas.").ConfigureAwait(false);
            return;
        }

        (IReadOnlyList<int> removed, string? error) = await RemoveImagesAsync(context, coverage, folder, name, ids, coverages, readers, audit, cancellation)
            .ConfigureAwait(false);

        if (error is not null)
        {
            await Refuse(context, 400, error).ConfigureAwait(false);
            return;
        }

        await Results.Ok(new { deleteResults = ids.Select(id => new { rasterId = id, success = removed.Contains(id) }) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>update</c>: one image's attributes — its <c>Name</c> and <c>AcquisitionDate</c>.</summary>
    internal static async Task RestUpdateAsync(
        HttpContext context, string? folder, string name, Func<string, string?> parameter, ICoverageCatalog coverages,
        ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedAsync(context, coverages, name, folder, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (!int.TryParse(parameter("rasterId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            await Refuse(context, 400, "`rasterId` is the object id of the image to update.").ConfigureAwait(false);
            return;
        }

        string? newName = null;
        DateTimeOffset? acquired = null;
        bool clear = false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(parameter("attributes") ?? "{}");

            foreach (JsonProperty attribute in document.RootElement.EnumerateObject())
            {
                if (attribute.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    newName = attribute.Value.GetString();
                }
                else if (attribute.Name.Equals("AcquisitionDate", StringComparison.OrdinalIgnoreCase))
                {
                    clear = attribute.Value.ValueKind == JsonValueKind.Null;
                    acquired = attribute.Value.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds(attribute.Value.GetInt64())
                        : attribute.Value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(attribute.Value.GetString(),
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset text) ? text : null;
                }
                else
                {
                    await Refuse(context, 400, $"`attributes` sets Name and AcquisitionDate; '{attribute.Name}' is not one an image here has.")
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            await Refuse(context, 400, "`attributes` is a JSON object of the fields to set.").ConfigureAwait(false);
            return;
        }

        string? error = await UpdateImageAsync(context, coverage, folder, name, id, newName, acquired, clear, coverages, readers, audit, cancellation)
            .ConfigureAwait(false);

        if (error is not null)
        {
            await Refuse(context, 400, error).ConfigureAwait(false);
            return;
        }

        await Results.Ok(new { updateResults = new[] { new { rasterId = id, success = true } } }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
