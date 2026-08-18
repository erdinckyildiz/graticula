using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Where an uploaded archive waits for the job that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file on a disk, because the reader cannot take a stream.</b> Every other import in this server
/// reads the upload as it arrives and never keeps it — a shapefile is assembled from an archive held in
/// memory under `ArchiveLimits.ForShapefile`, and GeoJSON is parsed straight off the
/// request. A geodatabase cannot work that way: GDAL opens a path, and the job that opens it runs after
/// the request has been answered. So this is a new capability rather than a helper, and it comes with
/// its own bound and its own note.
/// </para>
/// <para>
/// <b>Not under <see cref="HostSettings.StatePath"/>, and `HostSettings` already says why:</b> nothing
/// here must survive a container replacement. An archive whose job never ran is a job that failed, and
/// the honest recovery is to upload it again rather than to back up somebody's geodatabase on the
/// volume that carries the serving certificate.
/// </para>
/// <para>
/// <b>What the budget bounds is the directory, not the upload.</b> The upload is already bounded twice
/// — by <c>MaximumBytes</c> on the request and by
/// <see cref="HostSettings.ImportScratchBudgetBytes"/> on what may be resident here at once. The
/// second is the one that matters for a server left running: without it, ten uploads whose jobs all
/// failed is ten archives nobody deletes. Refusing the eleventh is not elegant, and it is better than
/// a full disk on a server that also holds the tile cache.
/// </para>
/// <para>
/// <b>ADR-024's exception is not silently widened.</b> That decision opened an archive under stated
/// bounds and its condition 3 is explicit that a second format does not inherit them — *"we already
/// decompress" is not an argument.* Nothing here decompresses: GDAL reads inside the ZIP through
/// <c>/vsizip/</c>, so what lands on the disk is the bytes that were uploaded and no more. The
/// withdrawal of the extraction step is real; the storage is the new thing, and this is it.
/// </para>
/// </remarks>
internal sealed class ImportScratch
{
    private readonly HostSettings _settings;
    private readonly ILogger<ImportScratch> _log;

    public ImportScratch(HostSettings settings, ILogger<ImportScratch> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _log = log;
    }

    /// <summary>Where the archives are, whether or not any exist yet.</summary>
    public string Directory => _settings.ImportScratchPath;

    /// <summary>
    /// Writes an upload where a job can find it, and returns the path.
    /// </summary>
    /// <param name="file">The uploaded archive.</param>
    /// <param name="job">The job that will read it, which names the file.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The absolute path written.</returns>
    /// <exception cref="IOException">The directory is already at its budget.</exception>
    /// <remarks>
    /// <para>
    /// <b>Named by job id and nothing else.</b> Not by the uploaded file name, which is a string
    /// somebody else chose: <c>..\..\state\certificate.pfx</c> is a file name, and a server that
    /// composes a path out of one has written the traversal itself. A <see cref="Guid"/> cannot
    /// traverse, cannot collide, and reads back as the job it belongs to — so a directory left dirty
    /// can be reconciled against the job table instead of guessed at.
    /// </para>
    /// <para>
    /// <b>The extension is kept, and only from a fixed set.</b> GDAL picks a driver partly by
    /// extension, so a `.gdb.zip` that arrives as a `.bin` is a `.bin`. It is chosen from what the
    /// recogniser already established rather than copied from the upload.
    /// </para>
    /// </remarks>
    public async Task<string> KeepAsync(IFormFile file, Guid job, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(file);

        System.IO.Directory.CreateDirectory(_settings.ImportScratchPath);

        long resident = Resident();

        if (resident + file.Length > _settings.ImportScratchBudgetBytes)
        {
            throw new IOException(
                $"The import scratch directory holds {Megabytes(resident)} MB and this archive is "
                + $"{Megabytes(file.Length)} MB, which is past the "
                + $"{Megabytes(_settings.ImportScratchBudgetBytes)} MB budget "
                + "(GisServer:ImportScratchBudgetMB). An archive is deleted when its job finishes, so a "
                + "full directory means jobs are failing without cleaning up, or the budget is smaller "
                + "than the work. Both are worth knowing before another upload is accepted.");
        }

        string path = PathFor(job);

        await using (FileStream keeping = new(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        await using (Stream arriving = file.OpenReadStream())
        {
            await arriving.CopyToAsync(keeping, cancellation).ConfigureAwait(false);
        }

        // Computed into a local, because CA1873 flags an argument the logger may never format. It is
        // one division, and the analyser is right in general: this is the rule that keeps a log call
        // from doing work on a level nobody enabled.
        long megabytes = Megabytes(file.Length);

        Log.ImportArchiveKept(_log, job, megabytes, path);

        return path;
    }

    /// <summary>
    /// Where the archive for a job is, computed rather than remembered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the id, so the path never travels.</b> <c>GET /admin/jobs/{id}</c> returns a
    /// job's <c>detail</c> to its owner verbatim — the endpoint's own note says nothing reads inside it
    /// — so a path stored there would hand every publisher this server's directory layout. It is not a
    /// secret and it is not theirs, and the alternative costs one function.
    /// </para>
    /// <para>
    /// This is the second thing naming files by job id buys, after not composing a path out of a string
    /// somebody else chose.
    /// </para>
    /// </remarks>
    public string PathFor(Guid job) => Path.Combine(
        _settings.ImportScratchPath,
        job.ToString("N", CultureInfo.InvariantCulture) + ".zip");

    /// <summary>
    /// Deletes an archive whose job has finished, either way.
    /// </summary>
    /// <remarks>
    /// <b>Both ways, and the failure path is the one that matters.</b> A job that succeeded is easy to
    /// remember to clean up after; a job that threw is where an archive stays forever, and the budget
    /// above then refuses the next upload for a reason nobody can see. Swallowed rather than thrown
    /// because a file that will not delete must not turn a finished job into a failed one.
    /// </remarks>
    public void Release(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException held)
        {
            Log.ImportArchiveHeld(_log, path, held);
        }
        catch (UnauthorizedAccessException refused)
        {
            Log.ImportArchiveHeld(_log, path, refused);
        }
    }

    /// <summary>How many bytes are waiting in the directory now.</summary>
    /// <remarks>
    /// <b>Measured rather than counted, on every accept.</b> A running total held in memory would be
    /// wrong after a restart with archives still on the disk, and wrong in the direction that lets the
    /// disk fill. The directory holds one file per unfinished job, which at this server's scale is a
    /// handful.
    /// </remarks>
    public long Resident()
    {
        if (!System.IO.Directory.Exists(_settings.ImportScratchPath))
        {
            return 0;
        }

        return new DirectoryInfo(_settings.ImportScratchPath)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Sum(static entry => entry.Length);
    }

    private static long Megabytes(long bytes) => bytes / 1048576;
}
