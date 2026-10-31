using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// The upload door's checks, apart from the request that used to carry them — D-324: a raster a user's tool wrote is a
/// stranger's file, and is published through the same isolation, bounds, read and pyramids as an uploaded one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split, not copied.</b> <see cref="AdmitAsync"/> and <see cref="WithOverviewsAsync"/> are what the upload door ran
/// inline, word for word, and the upload door now calls them; a check added to one is a check at both.
/// </para>
/// <para>
/// <b>What this side trusts of the tool: nothing but that it ran.</b> The file is found where the server says it
/// is, not where the tool says, and opened through no link (<see cref="RunOutputs"/>), copied under a name of
/// the server's, opened first in the import reader's process, and published private to the person who ran the tool.
/// </para>
/// </remarks>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>
    /// Opens a file this server now holds — first in the import reader's process, then here — and says what it is, or
    /// why it is refused, worded for whoever sent it as <paramref name="named"/>.
    /// </summary>
    internal static async Task<(CoverageInfo? Info, string? Refusal)> AdmitAsync(
        IServiceProvider services, ICoverageReaderFactory readers, string path, string named, CancellationToken cancellation)
    {
        // ADR-009 condition 1: opened in the import reader's process first, and bounded there.
        if (await OpenedApartAsync(services, path, named, cancellation).ConfigureAwait(false) is { } apart)
        {
            return (null, apart);
        }

        CoverageInfo read;

        try
        {
            using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);
            read = reader.Info;
        }
        catch (InvalidDataException refused)
        {
            // The file is named as its sender named it: where this server keeps it is not theirs to read.
            string why = refused.Message.Replace(path, named, StringComparison.Ordinal);
            return (null, why.Contains("is not a TIFF", StringComparison.Ordinal)
                ? $"{named} is not a GeoTIFF this server can read. Check that it opens in a GIS, then try again."
                : $"{named} could not be read as imagery: {why}");
        }

        if (read.Srid == 0)
        {
            return (null, $"{named} carries no EPSG code in its GeoKey directory, so this server cannot "
                + "say what its coordinates mean. Save it with its coordinate system and upload it again.");
        }

        return (read, null);
    }

    /// <summary>
    /// ADR-139: an image without overviews is given them, so a zoomed-out picture of it reads a small level instead of
    /// every pixel. It is servable without them, so a failure here is logged, not refused.
    /// </summary>
    internal static async Task<CoverageInfo> WithOverviewsAsync(
        IServiceProvider services, ICoverageReaderFactory readers, string path, CoverageInfo read, CancellationToken cancellation)
    {
        if (read.Overviews.Count > 0)
        {
            return read;
        }

        try
        {
            if (await services.GetRequiredService<ICoveragePyramidBuilder>().BuildAsync(path, cancellation).ConfigureAwait(false) > 0)
            {
                using ICoverageReader reader = await readers.OpenAsync(path, cancellation).ConfigureAwait(false);
                return reader.Info;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.PyramidNotBuilt(
                services.GetRequiredService<ILoggerFactory>().CreateLogger("Graticula.Coverages"), Path.GetFileName(path), e.Message);
        }

        return read;
    }

    /// <summary>
    /// A raster a user's tool wrote, published as a private image service of <paramref name="owner"/>'s — D-324.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <param name="source">The file, opened by <see cref="RunOutputs"/>: a plain file reached through no link.</param>
    /// <param name="named">What the file is called in messages: the tool's parameter, never a path.</param>
    /// <param name="name">The service's name, the server's or the caller's, already checked against the name rule.</param>
    /// <param name="owner">Who ran the tool.</param>
    /// <param name="job">The run, for the audit record.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="ownerName">Their name when they submitted the run, which the audit record carries (D-325).</param>
    /// <param name="address">Where they submitted it from, or null.</param>
    /// <returns>The service as published, or why the file was refused.</returns>
    internal static async Task<(PublishedCoverage? Published, string? Refusal)> PublishProducedAsync(
        IServiceProvider services, FileStream source, string named, string name, Guid owner, Guid job, CancellationToken cancellation,
        string? ownerName = null, string? address = null)
    {
        ICoverageCatalog coverages = services.GetRequiredService<ICoverageCatalog>();
        ICoverageReaderFactory readers = services.GetRequiredService<ICoverageReaderFactory>();
        HostSettings settings = services.GetRequiredService<HostSettings>();
        const string folder = "hosted";

        if (await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false) is not null)
        {
            return (null, $"There is already an image service '{Qualify(name, folder)}'. Choose another name for {named}.");
        }

        long size = source.Length;

        if (size > MaximumUploadBytes)
        {
            return (null, $"{named} is {size / (1024 * 1024)} MB; an image service is made from at most "
                + $"{MaximumUploadBytes / (1024 * 1024 * 1024)} GB.");
        }

        // D-325 (a): one organisation's runs cannot fill the volume every organisation is served from.
        if (await OverQuotaAsync(services, coverages, settings, named, size, cancellation).ConfigureAwait(false) is { } over)
        {
            return (null, over);
        }

        string directory = ToolOutputsDirectory(settings);
        Directory.CreateDirectory(directory);

        // The server's own name, so nothing the tool chose becomes a path; a copy, so the run's directory can go.
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.tif");
        bool registered = false;

        try
        {
            await using (FileStream into = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await source.CopyToAsync(into, cancellation).ConfigureAwait(false);

                // Measured again after the copy: the length read before it is the file's then, not its now.
                if (into.Length > MaximumUploadBytes)
                {
                    return (null, $"{named} grew past {MaximumUploadBytes / (1024 * 1024 * 1024)} GB while it was read.");
                }
            }

            (CoverageInfo? read, string? refusal) = await AdmitAsync(services, readers, path, named, cancellation).ConfigureAwait(false);

            if (refusal is not null)
            {
                return (null, refusal);
            }

            // A tool's raster is held to what a tool is given: the reader bounded the header, not what a pyramid of it
            // costs, and a small file can describe billions of cells (security review, 2026-10-30).
            if ((long)read!.Width * read.Height > ToolRunner.RasterCellLimit)
            {
                return (null, $"{named} has {(long)read.Width * read.Height:N0} cells; a raster a tool answers is published "
                    + $"with at most {ToolRunner.RasterCellLimit:N0}.");
            }

            CoverageInfo info = await WithOverviewsAsync(services, readers, path, read!, cancellation).ConfigureAwait(false);
            PublishedCoverage published = await coverages.RegisterAsync(folder, name, path, info, owner, cancellation).ConfigureAwait(false);

            try
            {
                await coverages.SetImagesAsync(
                    published.Folder, published.ServiceName, [new CoverageImageEntry(1, Path.GetFileName(path), named + ".tif", null)], null, cancellation)
                    .ConfigureAwait(false);

                // Under the person's name, as every other publish is; the run is in the detail (D-325 (c)).
                await services.GetRequiredService<IAuditLog>().RecordAsync(
                    new AuditEvent(
                        owner,
                        ownerName ?? $"tool job {job:N}",
                        address,
                        "coverage.tool-output",
                        published.QualifiedName,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            job, width = info.Width, height = info.Height, bands = info.Bands.Count, srid = info.Srid, bytes = size,
                        }),
                        true),
                    cancellation).ConfigureAwait(false);
            }
            catch
            {
                // Published and then failed: removed again, so no image service is left that the run's results do not name.
                await coverages.RemoveAsync(folder, name, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            registered = true;
            return (published, null);
        }
        finally
        {
            if (!registered)
            {
                // Refused or failed part way: nothing is left behind, in no catalogue entry that would remove it.
                Forget([path]);
            }
        }
    }

    /// <summary>
    /// Where the rasters tools publish are kept: beside the uploads, under the imagery directory, and apart from them so
    /// that what an organisation's tools hold can be measured (D-325).
    /// </summary>
    internal static string ToolOutputsDirectory(HostSettings settings) => Path.Combine(ImageryDirectory(settings), "tool-outputs");

    /// <summary>
    /// Withdraws a raster a run published when a later output of the same run failed — D-325 (d): a run either answers
    /// every output or leaves none of them behind.
    /// </summary>
    internal static async Task WithdrawProducedAsync(IServiceProvider services, PublishedCoverage published)
    {
        ICoverageCatalog coverages = services.GetRequiredService<ICoverageCatalog>();

        if (await coverages.RemoveAsync(published.Folder, published.ServiceName, CancellationToken.None).ConfigureAwait(false))
        {
            Forget([published.Path]);
        }
    }

    /// <summary>
    /// Why <paramref name="size"/> more bytes would take the organisation's tools past their limit, or null — measured
    /// from the files its tool outputs hold now, so what was removed no longer counts.
    /// </summary>
    private static async Task<string?> OverQuotaAsync(
        IServiceProvider services, ICoverageCatalog coverages, HostSettings settings, string named, long size, CancellationToken cancellation)
    {
        ToolSettings? tools = services.GetService<ToolSettings>();
        string? key = null;

        if (tools?.OutputBytes is not null && services.GetService<Graticula.Platform.Tenancy.ITenantStore>() is { } tenants)
        {
            Guid current = Graticula.Platform.Tenancy.TenantScope.Effective;
            key = (await tenants.ListAsync(cancellation).ConfigureAwait(false)).FirstOrDefault(t => t.Id == current)?.Key;
        }

        if (tools?.OutputLimitFor(key) is not long limit)
        {
            return null;
        }

        string root = Path.GetFullPath(ToolOutputsDirectory(settings)) + Path.DirectorySeparatorChar;
        long held = 0;

        // The catalogue's rows are the organisation's own (ADR-204), so these are its tools' files and nobody else's.
        foreach (PublishedCoverage coverage in await coverages.ListAsync(cancellation).ConfigureAwait(false))
        {
            string path = Path.GetFullPath(coverage.Path);

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            {
                // With the overviews built beside it.
                foreach (string file in Directory.EnumerateFiles(root, Path.GetFileNameWithoutExtension(path) + "*"))
                {
                    held += new FileInfo(file).Length;
                }
            }
        }

        return held + size <= limit
            ? null
            : $"{named} is {size / (1024 * 1024)} MB, and this organisation's tools already hold {held / (1024 * 1024)} MB of the "
                + $"{limit / (1024 * 1024)} MB they may; remove a tool's earlier output, or ask an administrator to raise the limit.";
    }
}
