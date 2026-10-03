using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>What a caller sends to register imagery.</summary>
/// <param name="Name">What to call the service.</param>
/// <param name="Folder">Where to put it, or null for the root.</param>
/// <param name="Path">Where the file already lives, and stays.</param>
public sealed record RegisterCoverageRequest(string? Name, string? Folder, string? Path);

/// <summary>
/// Registering imagery, which is the only write this face has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration opens the file once and never again</b>
/// ([ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.3). What
/// it reads — size, bands, sample kind, no-data, extent, reference, the pyramid — is
/// stored, so the service document is answerable without touching a disk that may be
/// an object store on the other side of a network.
/// </para>
/// <para>
/// <b>Refusing at registration is the whole value of opening it then.</b> A file that
/// is not a GeoTIFF, or has no georeference, or is rotated, is refused to the person
/// publishing it — who can do something about it — rather than to a client six months
/// later, who cannot.
/// </para>
/// </remarks>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/coverages", RegisterAsync);

        // ADR-123 condition 2: a GeoTIFF or COG sent from Studio, kept by this server and registered.
        app.MapPost("/admin/coverages/upload", UploadAsync).DisableAntiforgery();

        // ADR-123 condition 3: how it is drawn.
        app.MapGet("/admin/coverages/{name}/style", GetStyleAsync);
        app.MapPut("/admin/coverages/{name}/style", SetStyleAsync);
        app.MapGet("/admin/coverages/{name}/preview", PreviewAsync);

        // ADR-143: the file uploaded for an image service, given back to its owner.
        app.MapGet("/admin/coverages/{name}/file", DownloadAsync);

        // ADR-147: more images for an uploaded image service, which becomes or grows a mosaic.
        app.MapPost("/admin/coverages/{name}/images", AddImagesAsync).DisableAntiforgery();

        // ADR-148: ArcGIS's Download capability, on or off.
        app.MapPut("/admin/coverages/{name}/download", SetDownloadAsync);
        app.MapGet("/admin/coverages", ListAsync);

        // <b>Here rather than on `/admin/services`, and sharing deliberately stays
        // there.</b> Sharing already works for a coverage through the generic service
        // route, because a coverage's scope lives on its `service` row like everything
        // else — duplicating it would be two ways to do one thing, and the second one
        // is the one that drifts. Stopping and removing do not work there: the generic
        // stop is for system services and the ordinary one is per layer, which a
        // coverage does not have. So they arrive here, keyed the way registration is.
        app.MapPost("/admin/coverages/{name}/start", (
            HttpContext c, string name, string? folder, ICoverageCatalog k, IAuditLog l,
            CancellationToken t) => StatusAsync(c, name, folder, ServiceStatus.Started, k, l, t));

        app.MapPost("/admin/coverages/{name}/stop", (
            HttpContext c, string name, string? folder, ICoverageCatalog k, IAuditLog l,
            CancellationToken t) => StatusAsync(c, name, folder, ServiceStatus.Stopped, k, l, t));

        app.MapDelete("/admin/coverages/{name}", RemoveAsync);

        // ADR-152: a mosaic's images one by one — listed, renamed and dated, removed.
        MapCatalog(app);

        // ADR-154: a classified image's classes, named by its owner.
        MapClasses(app);
    }

    private static async Task StatusAsync(
        HttpContext context,
        string name,
        string? folder,
        ServiceStatus status,
        ICoverageCatalog coverages,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(coverages);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await coverages.SetStatusAsync(at, name, status, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, Missing(name, at)).ConfigureAwait(false);
            return;
        }

        await RecordAsync(
            context, audit, "coverage.status", Qualify(name, at),
            new { status = status == ServiceStatus.Started ? "started" : "stopped" },
            cancellation).ConfigureAwait(false);

        await Results.Ok(new
        {
            name = Qualify(name, at),
            status = status == ServiceStatus.Started ? "started" : "stopped",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RemoveAsync(
        HttpContext context,
        string name,
        string? folder,
        ICoverageCatalog coverages,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(coverages);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        // ADR-123: an uploaded file is this server's, kept under its own directory, and goes with the service; a file
        // registered in place is somebody else's and is never touched.
        PublishedCoverage? before = await coverages.FindAsync(at, name, cancellation).ConfigureAwait(false);
        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();
        bool ours = before is not null && Uploaded(settings, before.Path);

        if (!await coverages.RemoveAsync(at, name, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, Missing(name, at)).ConfigureAwait(false);
            return;
        }

        bool fileRemoved = false;

        if (ours)
        {
            // With the overviews built beside it (ADR-139), and a mosaic's images (ADR-140). Removed from the catalogue
            // either way; a file that will not delete is found by an operator later.
            fileRemoved = Forget([before!.Path]);
        }

        await RecordAsync(
            context, audit, "coverage.remove", Qualify(name, at),
            new { fileRemoved },
            cancellation).ConfigureAwait(false);

        if (ours)
        {
            await Results.Ok(new
            {
                name = Qualify(name, at),
                removed = true,
                fileRemoved,
                note = fileRemoved
                    ? "The service and the file uploaded for it are gone."
                    : "The service is gone. The uploaded file could not be deleted and is still on this server's disk.",
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // <b>Said out loud, because *remove* means two different things here.</b>
        // Imagery is registered in place, so this removes the registration and leaves
        // the file exactly where it was. An administrator who expected the other
        // meaning should find that out from the response rather than from a backup.
        await Results.Ok(new
        {
            name = Qualify(name, at),
            removed = true,
            fileRemoved = false,
            note = "The registration is gone. The file was never copied here and has not "
                + "been touched.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Where uploaded imagery is kept: beside the serving certificate, on the volume that survives.</summary>
    internal static string ImageryDirectory(HostSettings settings) => Path.Combine(settings.StatePath, "imagery");

    /// <summary>Whether a coverage's file is one this server keeps, rather than one registered where it lives.</summary>
    internal static bool Uploaded(HostSettings settings, string path)
    {
        string root = Path.GetFullPath(ImageryDirectory(settings)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fits a mosaic's images to one grid — ADR-147: refuses, naming each, those with other bands or another sample type,
    /// and resamples the rest onto the grid of the image at <paramref name="reference"/>, replacing each file in
    /// <paramref name="kept"/> and its description in <paramref name="infos"/>.
    /// </summary>
    /// <returns>Why they cannot be one mosaic, or null; and how many were resampled.</returns>
    private static async Task<(string? Refusal, IReadOnlyList<int> Conformed)> ConformAsync(
        HttpContext context, List<string> kept, List<CoverageInfo> infos, int reference, Func<int, string> name,
        string directory, ICoverageReaderFactory readers, CancellationToken cancellation, int firstNew = 0)
    {
        // Adding to an image service, the words are about this service and these images, not a publish.
        bool adding = firstNew > 0;
        // Images already in a mosaic are on its grid; only the new ones are measured, and only theirs are ever replaced.
        IReadOnlyList<Graticula.Raster.Tiff.MosaicMisfit> misfits = [.. Graticula.Raster.Tiff.VrtMosaicReader.Misfits(infos, reference)
            .Where(m => m.Image >= firstNew)];
        List<Graticula.Raster.Tiff.MosaicMisfit> refused = [.. misfits.Where(m => !m.Conformable)];

        if (refused.Count > 0 && adding)
        {
            string reasons = Graticula.Raster.Tiff.VrtMosaicReader.Say(refused, infos.Count, name, "this image service");
            string listed = string.Join(", ", refused.Select(m => name(m.Image)));

            // The reason once — the page says "Not added" itself — and the others offered only when there are others.
            return ($"{reasons}. Bands and pixel type cannot be resampled; another coordinate system or cell size can."
                + (refused.Count < infos.Count - firstNew ? $" Add the others without {listed}." : ""), []);
        }

        if (refused.Count > 0)
        {
            string reasons = Graticula.Raster.Tiff.VrtMosaicReader.Say(refused, infos.Count, name);
            return (refused.Count == 1
                ? $"{name(refused[0].Image)} cannot join the other images, so nothing was published: {reasons}. The images of a "
                    + "mosaic have the same bands and sample type; another coordinate system or pixel size is resampled onto "
                    + $"the mosaic's grid, but bands cannot be. Remove {name(refused[0].Image)} and upload again."
                : $"{refused.Count} of {infos.Count} images cannot join the rest, so nothing was published: {reasons}. The images "
                    + "of a mosaic have the same bands and sample type; another coordinate system or pixel size is resampled "
                    + "onto the mosaic's grid, but bands cannot be. Remove these and upload again.", []);
        }

        IProjector projector = context.RequestServices.GetRequiredService<IProjector>();
        List<int> conformed = [];

        foreach (Graticula.Raster.Tiff.MosaicMisfit misfit in misfits)
        {
            string target = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");

            try
            {
                infos[misfit.Image] = await MosaicConforming.ConformAsync(
                    kept[misfit.Image], infos[misfit.Image], infos[reference], target, readers, projector, cancellation)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException why)
            {
                File.Delete(target);
                return ($"{name(misfit.Image)} could not be put on the mosaic's grid, so {(adding ? "none were added" : "nothing was published")}: "
                    + $"{why.Message.TrimEnd('.')}.", conformed);
            }

            File.Delete(kept[misfit.Image]);
            kept[misfit.Image] = target;
            conformed.Add(misfit.Image);
        }

        return (null, conformed);
    }

    /// <summary>
    /// Adds images to an uploaded image service — ADR-147, by owner decision: one image becomes a mosaic, a mosaic grows.
    /// New images are drawn over the ones it had, each measured against its grid and resampled onto it when it is not
    /// on it; other bands or another sample type are refused, naming each. The service keeps its name, sharing and style.
    /// </summary>
    private static async Task AddImagesAsync(
        HttpContext context, string name, string? folder, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        IAuditLog audit, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

        if (!Uploaded(settings, coverage.Path) || !File.Exists(coverage.Path))
        {
            await Refuse(context, 400,
                $"'{Qualify(name, at)}' was registered from a file on this server; images are added only to one uploaded here.")
                .ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaximumUploadBytes;
        }

        if (!context.Request.HasFormContentType)
        {
            await Refuse(context, 400, "Send the images as multipart/form-data, each as `file`.").ConfigureAwait(false);
            return;
        }

        context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(
            context.Request, new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = MaximumUploadBytes }));

        IFormCollection form;

        try
        {
            form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            await Refuse(context, 413, $"An upload of at most {MaximumUploadBytes / (1024 * 1024 * 1024)} GB in all is accepted.")
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<Incoming> fromForm = [.. form.Files.GetFiles("file").Select(f => new Incoming(f.FileName, async (path, token) =>
        {
            await using FileStream into = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using Stream from = f.OpenReadStream();
            await from.CopyToAsync(into, token).ConfigureAwait(false);
        }))];

        if (await AddIncomingAsync(context, coverage, at, name, fromForm, coverages, readers, audit, cancellation).ConfigureAwait(false) is { } done)
        {
            await Results.Ok(new
            {
                name = Qualify(name, at),
                images = done.Images,
                added = done.NewIds.Count,
                resampled = done.Resampled.Count,
                resampledFiles = done.Resampled,
                width = done.Info.Width,
                height = done.Info.Height,
                overviews = done.Info.Overviews.Count,
            }).ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>An image on its way in: the name it was sent as, and how to write it to a path.</summary>
    internal sealed record Incoming(string Name, Func<string, CancellationToken, Task> WriteTo);

    /// <summary>What adding images made: how many the service now has, the new ones' object ids, which were resampled.</summary>
    internal sealed record AddedImages(int Images, IReadOnlyList<int> NewIds, IReadOnlyList<string> Resampled, CoverageInfo Info);

    /// <summary>
    /// Adds images to an uploaded image service — ADR-147's core, shared since ADR-152 by Studio's Add images and
    /// ArcGIS's <c>add</c>. Refuses in its own answer and returns null when it does not add them.
    /// </summary>
    internal static async Task<AddedImages?> AddIncomingAsync(
        HttpContext context, PublishedCoverage coverage, string? at, string name, IReadOnlyList<Incoming> sent,
        ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation)
    {
        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

        if (!Uploaded(settings, coverage.Path) || !File.Exists(coverage.Path))
        {
            await Refuse(context, 400,
                $"'{Qualify(name, at)}' was registered from a file on this server; images are added only to one uploaded here.")
                .ConfigureAwait(false);
            return null;
        }

        bool mosaic = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path);
        List<string> existing = mosaic ? [.. Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path)] : [coverage.Path];

        if (sent.Count == 0 || existing.Count + sent.Count > MaximumMosaicImages)
        {
            await Refuse(context, 400, sent.Count == 0
                    ? "Send at least one image as `file`."
                    : $"A mosaic holds at most {MaximumMosaicImages} images; this one has {existing.Count}.")
                .ConfigureAwait(false);
            return null;
        }

        if (sent.FirstOrDefault(f => !Receivable(f.Name) && Path.GetExtension(f.Name).ToLowerInvariant() is not (".sid" or ".ecw")) is { } other)
        {
            await Refuse(context, 400, $"{Path.GetFileName(other.Name)}: images are {AcceptedFormats}.").ConfigureAwait(false);
            return null;
        }

        string directory = ImageryDirectory(settings);
        List<string> added = [];
        List<string> received = [];
        string? written = null;
        bool replaced = false;

        try
        {
            List<string> files = [.. existing];
            List<CoverageInfo> infos = [];

            foreach (string file in existing)
            {
                using ICoverageReader reader = await readers.OpenAsync(file, cancellation).ConfigureAwait(false);
                infos.Add(reader.Info);
            }

            // ADR-157: each file written as the GeoTIFFs it is, or becomes.
            if (await ReceiveAsync(context, sent, directory, received, cancellation).ConfigureAwait(false) is not { } arrived)
            {
                return null;
            }

            if (existing.Count + arrived.Count > MaximumMosaicImages)
            {
                await Refuse(context, 400, $"A mosaic holds at most {MaximumMosaicImages} images; this one has {existing.Count} "
                    + $"and these make {arrived.Count}.").ConfigureAwait(false);
                return null;
            }

            foreach (Arrived file in arrived)
            {
                string path = file.Path;
                added.Add(path);

                try
                {
                    using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);

                    if (reader.Info.Srid == 0)
                    {
                        await Refuse(context, 400, $"{Path.GetFileName(file.Name)} carries no EPSG code, so this server cannot "
                            + "say where it is. Save it with its coordinate system and add it again.").ConfigureAwait(false);
                        return null;
                    }

                    infos.Add(reader.Info);
                }
                catch (InvalidDataException)
                {
                    await Refuse(context, 400, $"{Path.GetFileName(file.Name)} is not a GeoTIFF this server can read.")
                        .ConfigureAwait(false);
                    return null;
                }

                files.Add(path);
            }

            int firstNew = existing.Count;
            string Name(int i) => i < firstNew ? $"image {i + 1} of the mosaic" : arrived[i - firstNew].Name;
            List<string> working = [.. files];
            (string? refusal, IReadOnlyList<int> conformedImages) = await ConformAsync(
                    context, working, infos, 0, Name, directory, readers, cancellation, firstNew)
                .ConfigureAwait(false);
            int resampled = conformedImages.Count;

            // A conformed image replaced the one sent; the one sent is gone, the conformed one is this upload's to clean up.
            for (int i = firstNew; i < working.Count; i++)
            {
                added[i - firstNew] = working[i];
            }

            if (refusal is not null)
            {
                await Refuse(context, 400, refusal).ConfigureAwait(false);
                return null;
            }

            foreach (string path in working.Skip(firstNew))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<ICoveragePyramidBuilder>().BuildAsync(path, cancellation)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.PyramidNotBuilt(
                        context.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Graticula.Coverages"),
                        Path.GetFileName(path), e.Message);
                }
            }

            written = Path.Combine(directory, $"{Guid.NewGuid():N}.vrt");

            try
            {
                Graticula.Raster.Tiff.VrtMosaicReader.Write(written, working);
            }
            catch (InvalidDataException why)
            {
                // A backstop: whatever the measuring above missed is refused in words, not a 500.
                await Refuse(context, 400, $"The images cannot be one mosaic, so none were added: {why.Message.TrimEnd('.')}.")
                    .ConfigureAwait(false);
                return null;
            }

            // ADR-152: the images it had keep their object ids, names and dates; the new ones follow, named as they were sent.
            List<CatalogImage> before = await ImageServerEndpoints.CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);
            // Never an id an image removed had (ADR-152).
            int next = Math.Max(coverage.NextImageId ?? 1, before.Count == 0 ? 1 : before.Max(i => i.Id) + 1);
            List<CoverageImageEntry> catalog =
            [
                .. before.Select(i => new CoverageImageEntry(i.Id, Path.GetFileName(existing[i.Position]), i.Name, i.Acquired)),
                .. working.Skip(firstNew).Select((file, k) => new CoverageImageEntry(next + k, Path.GetFileName(file), arrived[k].Name, arrived[k].Acquired)),
            ];

            using ICoverageReader grown = await readers.OpenAsync(written, cancellation).ConfigureAwait(false);
            await coverages.ReplaceImageAsync(at, name, written, grown.Info, cancellation).ConfigureAwait(false);
            replaced = true;
            await coverages.SetImagesAsync(at, name, catalog, next + arrived.Count, cancellation).ConfigureAwait(false);

            // The virtual raster it had is replaced; the images it placed are the new one's.
            if (mosaic)
            {
                File.Delete(coverage.Path);
            }

            await RecordAsync(context, audit, "coverage.images", Qualify(name, at),
                new { added = arrived.Count, images = working.Count, resampled }, cancellation).ConfigureAwait(false);

            return new AddedImages(working.Count, [.. Enumerable.Range(next, arrived.Count)], [.. conformedImages.Select(Name)], grown.Info);
        }
        finally
        {
            if (!replaced)
            {
                Forget([.. added, .. received]);

                if (written is not null)
                {
                    File.Delete(written);
                }
            }
        }
    }

    /// <summary>The most images one upload makes a mosaic of — ADR-140.</summary>
    private const int MaximumMosaicImages = 500;

    /// <summary>
    /// Deletes files this server wrote for imagery, with the overviews beside each; a mosaic's virtual raster takes the
    /// images it places with it, where they are this server's.
    /// </summary>
    private static bool Forget(IEnumerable<string> paths)
    {
        bool all = true;

        foreach (string path in paths)
        {
            try
            {
                if (Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(path) && File.Exists(path))
                {
                    string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
                    all &= Forget(Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(path)
                        .Where(f => string.Equals(Path.GetDirectoryName(f), root, StringComparison.OrdinalIgnoreCase)));
                }

                File.Delete(path);
                File.Delete(Graticula.Raster.Tiff.TiffCoverageReader.PyramidPath(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                all = false;
            }
        }

        return all;
    }

    /// <summary>The largest image an upload may carry.</summary>
    private const long MaximumUploadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// A GeoTIFF or COG sent from Studio — ADR-123 condition 2. Kept under this server's imagery directory, opened
    /// once as a registration opens it, and published private; refused and deleted when it is not imagery this server
    /// can place.
    /// </summary>
    /// <remarks>
    /// <b>Streamed to disk, not read into memory</b>: an orthophoto is gigabytes. The name is the server's own, never
    /// the uploaded file's, so nothing the client sent becomes a path.
    /// </remarks>
    private static async Task UploadAsync(
        HttpContext context,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        HostSettings settings,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaximumUploadBytes;
        }

        if (!context.Request.HasFormContentType)
        {
            await Refuse(context, 400, "Send the image as multipart/form-data with `name` and `file`.").ConfigureAwait(false);
            return;
        }

        // The form's own ceiling is 128 MB by default, which an orthophoto passes; this one is the upload's.
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(
            context.Request, new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = MaximumUploadBytes }));

        IFormCollection form;

        try
        {
            form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        }
        catch (InvalidDataException tooLarge)
        {
            _ = tooLarge;
            await Refuse(context, 413, $"An upload of at most {MaximumUploadBytes / (1024 * 1024 * 1024)} GB in all is accepted.")
                .ConfigureAwait(false);
            return;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException tooLarge) when (tooLarge.StatusCode == 413)
        {
            // Kestrel's own ceiling, met before the form's: the same sentence, not the generic one.
            await Refuse(context, 413, $"An upload of at most {MaximumUploadBytes / (1024 * 1024 * 1024)} GB in all is accepted.")
                .ConfigureAwait(false);
            return;
        }

        string name = form["name"].ToString().Trim();
        IReadOnlyList<IFormFile> sent = form.Files.GetFiles("file");

        if (name.Length == 0 || sent.Count == 0)
        {
            await Refuse(context, 400, "A `name` for the service and a `file` — a GeoTIFF or COG — are both needed.")
                .ConfigureAwait(false);
            return;
        }

        // ADR-140: more than one file is a mosaic, served as one image.
        if (sent.Count > MaximumMosaicImages)
        {
            await Refuse(context, 400, $"A mosaic is made of at most {MaximumMosaicImages} images in one upload.")
                .ConfigureAwait(false);
            return;
        }

        // ADR-157: GeoTIFFs, and the formats GDAL writes as GeoTIFFs on the way in; MrSID and ECW are refused by name there.
        if (sent.FirstOrDefault(f => !Receivable(f.FileName) && Path.GetExtension(f.FileName).ToLowerInvariant() is not (".sid" or ".ecw")) is { } other)
        {
            await Refuse(context, 400,
                $"{Path.GetFileName(other.FileName)}: an imagery layer is published from {AcceptedFormats}.")
                .ConfigureAwait(false);
            return;
        }

        string folder = form["folder"].ToString().Trim() is { Length: > 0 } asked ? asked : "hosted";

        if (await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false) is not null)
        {
            await Refuse(context, 409, $"There is already an image service '{Qualify(name, folder)}'. Choose another name.")
                .ConfigureAwait(false);
            return;
        }

        string directory = ImageryDirectory(settings);
        Directory.CreateDirectory(directory);

        // Everything written for this upload, removed unless it is published — a refusal, a stop or a failure alike.
        List<string> kept = [];
        List<string> written = [];
        List<Arrived> arrived = [];
        List<CoverageImageEntry> catalog = [];
        bool registered = false;
        int resampled = 0;
        PublishedCoverage published;
        CoverageInfo info;

        try
        {
            List<CoverageInfo> infos = [];

            // ADR-157: each file written as the GeoTIFFs it is, or becomes — a NetCDF of time steps becomes several.
            List<Arrived>? received = await ReceiveAsync(context, [.. sent.Select(f => new Incoming(f.FileName, async (path, token) =>
            {
                await using FileStream into = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using Stream from = f.OpenReadStream();
                await from.CopyToAsync(into, token).ConfigureAwait(false);
            }))], directory, written, cancellation).ConfigureAwait(false);

            if (received is null)
            {
                return;
            }

            arrived = received;

            if (arrived.Count > MaximumMosaicImages)
            {
                await Refuse(context, 400, $"A mosaic is made of at most {MaximumMosaicImages} images in one upload; these make {arrived.Count}.")
                    .ConfigureAwait(false);
                return;
            }

            foreach (Arrived file in arrived)
            {
                string path = file.Path;
                kept.Add(path);

                CoverageInfo read;

                try
                {
                    using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);
                    read = reader.Info;
                }
                catch (InvalidDataException refused)
                {
                    // The file is named as its uploader named it: where this server keeps it is not theirs to read.
                    string named = Path.GetFileName(file.Name);
                    string why = refused.Message.Replace(path, named, StringComparison.Ordinal);
                    await Refuse(context, 400, why.Contains("is not a TIFF", StringComparison.Ordinal)
                            ? $"{named} is not a GeoTIFF this server can read. Check that it opens in a GIS, then try again."
                            : $"{named} could not be read as imagery: {why}")
                        .ConfigureAwait(false);
                    return;
                }

                if (read.Srid == 0)
                {
                    await Refuse(context, 400,
                        $"{Path.GetFileName(file.Name)} carries no EPSG code in its GeoKey directory, so this server cannot "
                        + "say what its coordinates mean. Save it with its coordinate system and upload it again.").ConfigureAwait(false);
                    return;
                }

                infos.Add(read);
            }

            // ADR-140, ADR-147: images are measured against the mosaic's grid from their headers, before any pyramid is built.
            // Other bands or another sample type cannot be made to fit and are refused, every one named; another reference,
            // pixel size or grid is resampled onto the grid.
            if (arrived.Count > 1)
            {
                (string? refusal, IReadOnlyList<int> conformed) = await ConformAsync(
                    context, kept, infos, Graticula.Raster.Tiff.VrtMosaicReader.ReferenceOf(infos), i => arrived[i].Name,
                    directory, readers, cancellation).ConfigureAwait(false);

                if (refusal is not null)
                {
                    await Refuse(context, 400, refusal).ConfigureAwait(false);
                    return;
                }

                resampled = conformed.Count;
            }

            for (int index = 0; index < kept.Count; index++)
            {
                string path = kept[index];
                CoverageInfo read = infos[index];

                // ADR-139: an image uploaded without overviews is given them, so a zoomed-out picture of it reads a small
                // level instead of every pixel. It is servable without them, so a failure here is logged, not refused.
                if (read.Overviews.Count == 0)
                {
                    try
                    {
                        if (await context.RequestServices.GetRequiredService<ICoveragePyramidBuilder>()
                                .BuildAsync(path, cancellation).ConfigureAwait(false) > 0)
                        {
                            using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);
                            read = reader.Info;
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Log.PyramidNotBuilt(
                            context.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Graticula.Coverages"),
                            Path.GetFileName(path),
                            e.Message);
                    }
                }

                infos[index] = read;
            }

            string served = kept[0];
            info = infos[0];

            // ADR-152: the catalog names each image by the file it was sent as.
            catalog = [.. kept.Select((file, i) => new CoverageImageEntry(i + 1, Path.GetFileName(file), arrived[i].Name, arrived[i].Acquired))];

            if (kept.Count > 1)
            {
                served = Path.Combine(directory, $"{Guid.NewGuid():N}.vrt");
                string[] images = [.. kept];
                kept.Add(served);

                try
                {
                    Graticula.Raster.Tiff.VrtMosaicReader.Write(served, images);
                }
                catch (InvalidDataException refused)
                {
                    // Checked above from the same headers; reached only if a file changed underneath.
                    await Refuse(context, 400, $"These images could not be joined into one: {refused.Message}").ConfigureAwait(false);
                    return;
                }

                using ICoverageReader mosaic = await readers.OpenAsync(served, cancellation).ConfigureAwait(false);
                info = mosaic.Info;
            }

            RequestPrincipal principal = context.Features.Get<RequestPrincipal>()!;
            published = await coverages.RegisterAsync(
                folder, name, served, info, principal.Principal.IsAnonymous ? null : principal.Principal.Id, cancellation)
                .ConfigureAwait(false);
            registered = true;
            await coverages.SetImagesAsync(published.Folder, published.ServiceName, catalog, null, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (!registered)
            {
                // An upload stopped, refused or failed part way leaves nothing, in no catalogue entry that would remove it.
                Forget([.. kept, .. written]);
            }
        }

        await RecordAsync(context, audit, "coverage.upload", published.QualifiedName,
            new { width = info.Width, height = info.Height, bands = info.Bands.Count, srid = info.Srid, bytes = sent.Sum(f => f.Length), images = arrived.Count },
            cancellation).ConfigureAwait(false);

        await Results.Created(
            $"/rest/services/{published.QualifiedName}/ImageServer",
            new
            {
                name = published.QualifiedName,
                srid = info.Srid,
                width = info.Width,
                height = info.Height,
                bands = info.Bands.Count,
                overviews = info.Overviews.Count,
                images = arrived.Count,
                // ADR-147: how many were put on the mosaic's grid from another reference, pixel size or grid.
                resampled,
                sharing = "private",
                note = "Published private, as every service starts. Share it from its page.",
            }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The file uploaded for an image service, given back to whoever may manage it — ADR-143: the GeoTIFF as it was sent,
    /// or a mosaic's tiles and a virtual raster that places them, as one zip. A file registered in place is not this
    /// server's to hand out, and is refused with where it is kept.
    /// </summary>
    internal static async Task DownloadAsync(
        HttpContext context, string name, string? folder, string? check, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        // ADR-148: whoever manages it; anyone it is shared with when its Download capability is on. The same refusal
        // for not seeing it and not being let, so nothing tells a caller whether a service it may not see exists.
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        PublishedCoverage? coverage = await coverages.FindAsync(at, name, cancellation).ConfigureAwait(false);

        if (coverage is null
            || !(LayerAccess.MayManage(coverage.Owner, current.Principal, current.Authorization)
                || (coverage.Download && LayerAccess.Evaluate(
                    coverage.Sharing, coverage.Owner, current.Principal, current.Authorization, coverage.SharedWith).IsAllowed())))
        {
            await Refuse(context, 404, $"No image service '{Qualify(name, at)}' whose file you may download.").ConfigureAwait(false);
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

        if (!Uploaded(settings, coverage.Path) || !File.Exists(coverage.Path))
        {
            await Refuse(context, 400,
                $"'{Qualify(name, at)}' was registered from a file on this server, not uploaded; that file is the server "
                + "administrator's, and is not handed out here.").ConfigureAwait(false);
            return;
        }

        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

        // `check=1` asks whether there is a file to give, its name and its size, without sending it — so Studio can say a
        // refusal in place and then hand the transfer itself to the browser, which shows its progress and writes it to
        // disk as it comes (ADR-143's review).
        if (check is { Length: > 0 })
        {
            bool mosaic = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path);
            IReadOnlyList<string> parts = mosaic ? Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path) : [coverage.Path];
            await Results.Ok(new
            {
                fileName = mosaic ? $"{safe}.zip" : $"{safe}.tif",
                bytes = parts.Where(File.Exists).Sum(f => new FileInfo(f).Length),
                images = parts.Count,
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path))
        {
            await Results.File(coverage.Path, "image/tiff", $"{safe}.tif", enableRangeProcessing: true)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // A mosaic: its tiles under names of their own, and the virtual raster rewritten to place them by those names.
        IReadOnlyList<string> tiles = Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path);
        Dictionary<string, string> named = tiles
            .Select((tile, i) => (tile, entry: $"{safe}_{(i + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture)}.tif"))
            .ToDictionary(p => Path.GetFileName(p.tile), p => p.entry, StringComparer.OrdinalIgnoreCase);
        System.Xml.Linq.XElement vrt = System.Xml.Linq.XElement.Load(coverage.Path);

        foreach (System.Xml.Linq.XElement source in vrt.Descendants("SourceFilename"))
        {
            source.Value = named.TryGetValue(Path.GetFileName(source.Value.Trim()), out string? entry) ? entry : source.Value;
            source.SetAttributeValue("relativeToVRT", 1);
        }

        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{safe}.zip\"";

        // A zip writes its directory synchronously when it is closed, and Kestrel refuses synchronous writes unless the
        // request says otherwise; this one does, for this response alone.
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } control)
        {
            control.AllowSynchronousIO = true;
        }

        using System.IO.Compression.ZipArchive zip = new(context.Response.Body, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);

        await using (Stream into = zip.CreateEntry($"{safe}.vrt", System.IO.Compression.CompressionLevel.Optimal).Open())
        {
            vrt.Save(into);
        }

        foreach (string tile in tiles)
        {
            // Already compressed or not, a GeoTIFF gains little from deflate and costs a CPU on the way out.
            await using Stream into = zip.CreateEntry(named[Path.GetFileName(tile)], System.IO.Compression.CompressionLevel.NoCompression).Open();
            await using FileStream from = File.OpenRead(tile);
            await from.CopyToAsync(into, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>What <c>PUT …/download</c> reads — ADR-148.</summary>
    /// <param name="Download">Whether everyone it is shared with may download its file.</param>
    internal sealed record DownloadRequest(bool Download);

    /// <summary>Turns an image service's Download capability on or off — ADR-148; whoever manages it may.</summary>
    private static async Task SetDownloadAsync(
        HttpContext context, string name, string? folder, DownloadRequest body, ICoverageCatalog coverages,
        IAuditLog audit, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        HostSettings settings = context.RequestServices.GetRequiredService<HostSettings>();

        if (body.Download && !Uploaded(settings, coverage.Path))
        {
            await Refuse(context, 400,
                $"'{Qualify(name, at)}' was registered from a file on this server; that file is the administrator's and is "
                + "not offered for download.").ConfigureAwait(false);
            return;
        }

        await coverages.SetDownloadAsync(at, name, body.Download, cancellation).ConfigureAwait(false);
        await RecordAsync(context, audit, "coverage.download", Qualify(name, at), new { download = body.Download }, cancellation)
            .ConfigureAwait(false);
        await Results.Ok(new
        {
            name = Qualify(name, at),
            download = body.Download,
            note = body.Download
                ? "Everyone this image service is shared with may download its file."
                : "Only whoever manages this image service may download its file.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What <c>PUT …/style</c> reads.</summary>
    /// <param name="Stretch">auto (from the data), full (the format's range) or fixed.</param>
    /// <param name="Minimum">The low end of a fixed stretch.</param>
    /// <param name="Maximum">The high end of a fixed stretch.</param>
    /// <param name="Ramp">A colour ramp for a single band, or null for greyscale.</param>
    /// <param name="Function">The raster function it is shown through — ADR-136 — or null or none.</param>
    internal sealed record CoverageStyleRequest(string? Stretch, double? Minimum, double? Maximum, string? Ramp, string? Function = null);

    /// <summary>The coverage, when the caller owns it or administers content; otherwise the refusal is written.</summary>
    private static async Task<PublishedCoverage?> ManagedAsync(
        HttpContext context, ICoverageCatalog coverages, string name, string? folder, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false) is not { } coverage
            || !LayerAccess.MayManage(coverage.Owner, current.Principal, current.Authorization))
        {
            await Refuse(context, 404, $"No image service '{Qualify(name, folder)}', or not one you may change.").ConfigureAwait(false);
            return null;
        }

        return coverage;
    }

    /// <summary>How a coverage is drawn, with the values it holds — what the Display settings start from.</summary>
    private static async Task GetStyleAsync(
        HttpContext context, string name, string? folder, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        HostSettings settings, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        CoverageStyle drawn = await ImageServerEndpoints.StyleOfAsync(coverage, readers, cancellation).ConfigureAwait(false);
        var statistics = await ImageServerEndpoints.StatisticsAsync(coverage, readers, cancellation).ConfigureAwait(false);
        string stretch = string.IsNullOrWhiteSpace(coverage.Style) || CoverageStyle.IsAuto(coverage.Style) ? "auto"
            : drawn.Stretch == StretchKind.Fixed ? "fixed" : "full";

        await Results.Json(new
        {
            name = Qualify(name, at),
            stretch,
            minimum = drawn.Minimum,
            maximum = drawn.Maximum,
            ramp = drawn.RampName,
            ramps = CoverageStyle.NamedRamps.Keys,
            // ADR-136: the raster function it is shown through, and the ones it may be.
            // ADR-151: with its arguments, as stored — ndvi:2:3, extractband:3,2,1, bandarithmetic:(B4-B3)/(B4+B3).
            function = RasterFunction.FromStyleText(coverage.Style).ToStyleText() is { } shownAs
                ? shownAs["function:".Length..]
                : "none",
            functions = RasterFunction.Names,
            bands = coverage.Info.Bands.Count,
            kind = coverage.Info.Bands.Count > 0 ? coverage.Info.Bands[0].Kind.ToString() : null,
            // Whether deleting the service deletes the file — an upload — or leaves it, being registered in place.
            uploaded = Uploaded(settings, coverage.Path),
            // ADR-148: whether everyone it is shared with may download its file.
            download = coverage.Download,
            // ADR-140: how many images it was made of — a mosaic's delete takes them all.
            images = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path) && File.Exists(coverage.Path)
                ? Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path).Count
                : 1,
            statistics = statistics.Select(b => new { minimum = b.Minimum, maximum = b.Maximum, mean = b.Mean, standardDeviation = b.StandardDeviation }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Sets how a coverage is drawn — ADR-123 — by its owner or an administrator.</summary>
    private static async Task SetStyleAsync(
        HttpContext context, string name, string? folder, CoverageStyleRequest request, ICoverageCatalog coverages,
        IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (StyleText(request, coverage, out string? error) is not { } text)
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        string? stored = text == "stretch:auto" ? null : text;

        await coverages.SetStyleAsync(at, name, stored, cancellation).ConfigureAwait(false);
        await RecordAsync(context, audit, "coverage.style", Qualify(name, at), new { style = stored }, cancellation).ConfigureAwait(false);
        await Results.Json(new { name = Qualify(name, at), style = stored ?? "stretch:auto" }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The stored text of a requested style, or null with why it is refused.</summary>
    private static string? StyleText(CoverageStyleRequest request, PublishedCoverage coverage, out string? error)
    {
        error = null;
        string? ramp = string.IsNullOrWhiteSpace(request.Ramp) ? null : request.Ramp.Trim().ToLowerInvariant();

        if (ramp is not null && !CoverageStyle.NamedRamps.ContainsKey(ramp))
        {
            error = $"'{ramp}' is not a colour ramp; {string.Join(", ", CoverageStyle.NamedRamps.Keys)} are.";
            return null;
        }

        if (ramp is not null && coverage.Info.Bands.Count >= 3)
        {
            error = "A colour ramp colours one band of measurements. This image has three or more bands, which are "
                + "already red, green and blue.";
            return null;
        }

        string? stretch = (request.Stretch ?? "auto").Trim().ToLowerInvariant() switch
        {
            "auto" => "stretch:auto",
            "full" => "stretch:full",
            "fixed" when request.Minimum is { } low && request.Maximum is { } high && high > low && double.IsFinite(low) && double.IsFinite(high)
                => new CoverageStyle(StretchKind.Fixed, low, high).ToText(),
            _ => null,
        };

        if (stretch is null)
        {
            error = "`stretch` is auto, full or fixed; a fixed stretch needs a `minimum` below its `maximum`.";
            return null;
        }

        // ADR-136: the raster function the service is shown through, by default — of one band of measurements.
        string? function = string.IsNullOrWhiteSpace(request.Function) || request.Function.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : request.Function.Trim().ToLowerInvariant();

        string? stored = null;

        if (function is not null)
        {
            // ADR-151: a function and its arguments, as the style stores them; each says what it needs of the bands.
            string asked = request.Function!.Trim();

            // An expression that does not read says what in it did not, in the analyst's words (ux review 6).
            if (asked.StartsWith("bandarithmetic:", StringComparison.OrdinalIgnoreCase)
                && !BandExpression.TryParse(asked["bandarithmetic:".Length..], out _, out string? unread))
            {
                error = unread;
                return null;
            }

            if (RasterFunction.FromStyleSegment(asked) is not { } chosen)
            {
                error = $"'{request.Function!.Trim()}' is not a raster function this server applies with what it needs: "
                    + "hillshade, slope, aspect, ndvi:red:infrared (bands from zero), extractband:3,2,1 or bandarithmetic:(B4-B3)/(B4+B3).";
                return null;
            }

            if (!chosen.FitsBands(coverage.Info.Bands.Count, out error))
            {
                return null;
            }

            stored = chosen.ToStyleText();
        }

        string text = ramp is null ? stretch : $"{stretch};ramp:{ramp}";
        return stored is null ? text : $"{text};{stored}";
    }

    /// <summary>
    /// The coverage's whole extent drawn under a style not yet saved — the Display settings' picture, redrawn as the
    /// controls change (ADR-123). Its owner's only: it reads the same file the service does.
    /// </summary>
    private static async Task PreviewAsync(
        HttpContext context, string name, string? folder, string? stretch, double? minimum, double? maximum, string? ramp,
        string? function, int? width, int? height, ICoverageCatalog coverages, ICoverageReaderFactory readers, IMapCanvasFactory canvases,
        IProjector projector, ConnectionBudget budget, HostSettings settings, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await ManagedAsync(context, coverages, name, at, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (StyleText(new CoverageStyleRequest(stretch, minimum, maximum, ramp, function), coverage, out string? error) is not { } text)
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        await ImageServerEndpoints.PreviewAsync(
                context, coverage, text, Math.Clamp(width ?? 360, 16, 720), Math.Clamp(height ?? 240, 16, 720),
                readers, canvases, projector, budget, settings, cancellation)
            .ConfigureAwait(false);
    }

    private static string Qualify(string name, string? folder) =>
        string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";

    private static string Missing(string name, string? folder) =>
        $"No image service '{Qualify(name, folder)}'. Coverages are addressed by the service "
        + "name with the folder as a `folder` parameter, which is how `/admin/services` "
        + "addresses one too.";

    private static Task RecordAsync(
        HttpContext context,
        IAuditLog audit,
        string action,
        string resource,
        object detail,
        CancellationToken cancellation)
    {
        RequestPrincipal principal = context.Features.Get<RequestPrincipal>()
            ?? new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing);

        return audit.RecordAsync(
            new AuditEvent(
                principal.Principal.Id,
                principal.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                action,
                resource,
                System.Text.Json.JsonSerializer.Serialize(detail),
                true),
            cancellation);
    }

    private static async Task ListAsync(
        HttpContext context, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        var listing = await coverages.ListAsync(cancellation).ConfigureAwait(false);

        // <b>The path is not in this listing either.</b> An administrator can read it
        // out of the store; a route that returns it makes it one misconfigured
        // privilege away from a client, and ADR-043 §3.3's proxy exists so the location
        // never has to leave here.
        await Results.Ok(new
        {
            coverages = Array.ConvertAll([.. listing], c => new
            {
                name = c.QualifiedName,
                srid = c.Info.Srid,
                width = c.Info.Width,
                height = c.Info.Height,
                bands = c.Info.Bands.Count,
                overviews = c.Info.Overviews.Count,
                sharing = c.Sharing.ToString().ToLowerInvariant(),

                // <b>Status, because an administrator listing coverages is usually
                // looking for the one that stopped answering.</b> It was absent until
                // 2026-08-21 and the only way to find out was to ask the face and read
                // the refusal, which is the wrong direction round.
                status = c.Status == ServiceStatus.Started ? "started" : "stopped",
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RegisterAsync(
        HttpContext context,
        RegisterCoverageRequest request,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Path))
        {
            await Refuse(context, 400,
                "A coverage registration needs a `name` for the service and a `path` to the "
                + "file. The file is not copied — ADR-043 §3.3 registers imagery where it "
                + "already lives — so the path has to be one this server can still read later.")
                .ConfigureAwait(false);

            return;
        }

        if (!File.Exists(request.Path))
        {
            await Refuse(context, 400,
                $"This server cannot see '{request.Path}'. Imagery is registered in place, so "
                + "the path is read at every request rather than once at upload: a path that is "
                + "not readable now will not be readable later either.")
                .ConfigureAwait(false);

            return;
        }

        CoverageInfo info;

        try
        {
            using ICoverageReader reader =
                await readers.OpenAsync(request.Path, cancellation).ConfigureAwait(false);

            info = reader.Info;
        }
        catch (InvalidDataException refused)
        {
            // <b>The reader's own sentence, not a summary of it.</b> It says which of
            // several specific things is wrong — no georeference, a rotation this
            // server will not place, not a TIFF at all — and rewording it here would
            // lose the part that tells the publisher what to fix.
            await Refuse(context, 400, refused.Message).ConfigureAwait(false);
            return;
        }

        if (info.Srid == 0)
        {
            await Refuse(context, 400,
                "This file carries no EPSG code in its GeoKey directory, so this server cannot "
                + "say what its coordinates mean. A coverage with an unknown reference cannot "
                + "be drawn beside anything else.")
                .ConfigureAwait(false);

            return;
        }

        RequestPrincipal principal = context.Features.Get<RequestPrincipal>()
            ?? new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing);

        PublishedCoverage published = await coverages.RegisterAsync(
            string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder,
            request.Name,
            request.Path,
            info,
            principal.Principal.IsAnonymous ? null : principal.Principal.Id,
            cancellation).ConfigureAwait(false);

        // ADR-148: a registered image without overviews is given them beside its file, in the background.
        context.RequestServices.GetService<CoveragePyramids>()?.Ask();

        await audit.RecordAsync(
            new AuditEvent(
                principal.Principal.Id,
                principal.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                "coverage.register",
                published.QualifiedName,
                // <b>JSON, because the column is.</b> A plain sentence here is a
                // `22P02` from PostgreSQL at the moment of the write, which the error
                // classifier then reports as an unreachable database — so a registration
                // that failed for a formatting reason reads as an outage. Caught on the
                // first end-to-end registration.
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    width = info.Width,
                    height = info.Height,
                    bands = info.Bands.Count,
                    srid = info.Srid,
                    overviews = info.Overviews.Count,
                    inPlace = true,
                }),
                true),
            cancellation).ConfigureAwait(false);

        await Results.Created(
            $"/rest/services/{published.QualifiedName}/ImageServer",
            new
            {
                name = published.QualifiedName,
                srid = info.Srid,
                width = info.Width,
                height = info.Height,
                bands = info.Bands.Count,
                overviews = info.Overviews.Count,

                // <b>Private, and said out loud.</b> Every service this server creates
                // starts private; saying so in the response is what stops a publisher
                // assuming otherwise and discovering it from a colleague.
                sharing = "private",
            }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static Task Refuse(HttpContext context, int code, string message) =>
        Results.Json(
            new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code).ExecuteAsync(context);
}
