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
internal static class CoverageAdminEndpoints
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

        if (sent.FirstOrDefault(f => Path.GetExtension(f.FileName).ToLowerInvariant() is not (".tif" or ".tiff")) is { } other)
        {
            await Refuse(context, 400,
                $"{Path.GetFileName(other.FileName)}: an imagery layer is published from GeoTIFFs or Cloud Optimized GeoTIFFs "
                + "(.tif or .tiff).")
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
        bool registered = false;
        PublishedCoverage published;
        CoverageInfo info;

        try
        {
            List<CoverageInfo> infos = [];

            foreach (IFormFile file in sent)
            {
                string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");
                kept.Add(path);

                await using (FileStream into = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (Stream from = file.OpenReadStream())
                {
                    await from.CopyToAsync(into, cancellation).ConfigureAwait(false);
                }

                CoverageInfo read;

                try
                {
                    using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);
                    read = reader.Info;
                }
                catch (InvalidDataException refused)
                {
                    // The file is named as its uploader named it: where this server keeps it is not theirs to read.
                    string named = Path.GetFileName(file.FileName);
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
                        $"{Path.GetFileName(file.FileName)} carries no EPSG code in its GeoKey directory, so this server cannot "
                        + "say what its coordinates mean. Save it with its coordinate system and upload it again.").ConfigureAwait(false);
                    return;
                }

                infos.Add(read);
            }

            // ADR-140: tiles that do not make one grid are refused from their headers, before any pyramid is built, and
            // every one of them is named — not the first, after an hour's work on the rest.
            if (sent.Count > 1 && Graticula.Raster.Tiff.VrtMosaicReader.Misfits(infos) is { Count: > 0 } misfits)
            {
                string Name(int i) => Path.GetFileName(sent[i].FileName);
                string reasons = Graticula.Raster.Tiff.VrtMosaicReader.Say(misfits, sent.Count, Name);
                await Refuse(context, 400, misfits.Count == 1
                        ? $"{Name(misfits[0].Image)} does not fit with the other images, so nothing was published: {reasons}. "
                            + "Tiles of one image share a coordinate system, bands and pixel size and line up pixel for pixel. "
                            + $"Remove or correct {Name(misfits[0].Image)} and upload again."
                        : $"{misfits.Count} of {sent.Count} images do not fit with the rest, so nothing was published: {reasons}. "
                            + "Tiles of one image share a coordinate system, bands and pixel size and line up pixel for pixel. "
                            + "Remove or correct these and upload again.")
                    .ConfigureAwait(false);
                return;
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
        }
        finally
        {
            if (!registered)
            {
                // An upload stopped, refused or failed part way leaves nothing, in no catalogue entry that would remove it.
                Forget(kept);
            }
        }

        await RecordAsync(context, audit, "coverage.upload", published.QualifiedName,
            new { width = info.Width, height = info.Height, bands = info.Bands.Count, srid = info.Srid, bytes = sent.Sum(f => f.Length), images = sent.Count },
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
                images = sent.Count,
                sharing = "private",
                note = "Published private, as every service starts. Share it from its page.",
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
            function = RasterFunction.FromStyleText(coverage.Style).Kind is var shownAs && shownAs != RasterFunctionKind.None
                ? shownAs.ToString().ToLowerInvariant()
                : "none",
            functions = RasterFunction.Names,
            bands = coverage.Info.Bands.Count,
            kind = coverage.Info.Bands.Count > 0 ? coverage.Info.Bands[0].Kind.ToString() : null,
            // Whether deleting the service deletes the file — an upload — or leaves it, being registered in place.
            uploaded = Uploaded(settings, coverage.Path),
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

        if (function is not null)
        {
            if (!Enum.TryParse(function, ignoreCase: true, out RasterFunctionKind kind) || kind == RasterFunctionKind.None)
            {
                error = $"'{function}' is not a raster function this server applies; {string.Join(", ", RasterFunction.Names)} are.";
                return null;
            }

            if (coverage.Info.Bands.Count >= 3)
            {
                error = "A raster function works on one band of measurements, an elevation model; this image is a colour image.";
                return null;
            }
        }

        string text = ramp is null ? stretch : $"{stretch};ramp:{ramp}";
        return function is null ? text : $"{text};function:{function}";
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
