using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Asks the geodatabase reader one question, in a process of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>A process per question, and not a pool.</b> <see cref="GeometryWorkerPool"/> keeps workers
/// because an overlay is a request-path operation measured in milliseconds and the launch dominates
/// it. Reading a geodatabase is neither: it happens when somebody uploads a file, it is the slowest
/// thing this server does on purpose, and a launch beside it is not measurable. What a fresh process
/// buys instead is that nothing survives between two archives — no GDAL state, no cached driver
/// handle, no memory a previous file left behind.
/// </para>
/// <para>
/// <b>The isolation is the point, and it is what ADR-037 §5a spent an image boundary to keep.</b>
/// GDAL parses a file somebody else chose. [ADR-009](../../docs/adr/ADR-009-raster-engine.md) §2.2's
/// own words for keeping it out of the serving process are that it *"removes an untrusted-file parser
/// from the process that serves public requests"* — and with the package now in the solution, a child
/// process is the whole of what keeps that true. `NativeDependencyTests` checks it in both directions.
/// </para>
/// <para>
/// <b>What the deadline does and does not bound.</b> It bounds time, and that is all it is claimed to
/// do. `DOTNET_GCHeapHardLimit` is set the way the overlay worker sets it, but GDAL allocates
/// natively, outside the managed heap, so the limit does not bound a malicious archive's memory —
/// only the process being killable does, together with
/// <see cref="HostSettings.ImportScratchBudgetBytes"/> bounding what reaches the disk in the first
/// place. Saying so here rather than letting the environment variable imply otherwise.
/// </para>
/// </remarks>
internal sealed class GeodatabaseReader
{
    /// <summary>
    /// The managed heap ceiling for the child.
    /// </summary>
    /// <remarks>
    /// Half the overlay worker's, because this process reads a file and reports what is in it rather
    /// than building a geometry graph. It is not the memory bound — see the class note.
    /// </remarks>
    public const long HeapLimitBytes = 512L << 20;

    private readonly string _executable;
    private readonly ILogger<GeodatabaseReader> _log;

    public GeodatabaseReader(string executable, ILogger<GeodatabaseReader> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(log);

        _executable = executable;
        _log = log;
    }

    /// <summary>
    /// The reader executable, in its own directory beside the server's.
    /// </summary>
    /// <remarks>
    /// <b>Beside the host rather than configurable, for the reason the overlay worker's note gives:</b>
    /// the two are built and shipped together and the wire between them is private, so a setting for
    /// the path would create a way to pair mismatched builds and nothing else. Its own directory
    /// because it has its own dependency closure — and this one is large, since GDAL's native payloads
    /// for two platforms live in it.
    /// </remarks>
    public static string ExecutableBesideThisOne()
    {
        string name = OperatingSystem.IsWindows()
            ? "Graticula.Import.Reader.exe"
            : "Graticula.Import.Reader";

        return Path.Combine(AppContext.BaseDirectory, "importer", name);
    }

    /// <summary>Whether the reader is installed where this server expects it.</summary>
    /// <remarks>
    /// <b>Asked before an upload is accepted, not after.</b> A deployment that did not ship the reader
    /// should refuse a geodatabase with a sentence about the deployment, rather than open a job that
    /// will fail in a minute with a message about a missing file.
    /// </remarks>
    public bool Available => File.Exists(_executable);

    /// <summary>
    /// Sends one request and returns the answer.
    /// </summary>
    /// <param name="request">The request object, serialised as the reader's one JSON line.</param>
    /// <param name="deadline">How long the child has to answer before it is killed.</param>
    /// <param name="cancellation">The caller's own cancellation.</param>
    /// <exception cref="InvalidOperationException">
    /// The reader is not installed, did not start, answered nothing, or ran past its deadline.
    /// </exception>
    public async Task<JsonDocument> AskAsync(
        object request,
        TimeSpan deadline,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Available)
        {
            throw new InvalidOperationException(
                $"The geodatabase reader is not installed at '{_executable}'. Importing a File "
                + "Geodatabase needs it; a zipped shapefile and a GeoJSON FeatureCollection do not, "
                + "and are unaffected. It is built and copied beside the server by the solution, so a "
                + "deployment missing it was assembled by hand.");
        }

        ProcessStartInfo start = new(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,

            // Left attached, so GDAL's own diagnosis reaches this server's stderr rather than
            // vanishing. The reader routes its warnings through the answer; a stack trace goes here.
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.Environment["DOTNET_GCHeapHardLimit"] =
            HeapLimitBytes.ToString("X", CultureInfo.InvariantCulture);

        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_gcConcurrent"] = "0";

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The geodatabase reader at '{_executable}' did not start.");

        using CancellationTokenSource timer =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        timer.CancelAfter(deadline);

        try
        {
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request).AsMemory(), timer.Token).ConfigureAwait(false);

            await process.StandardInput.FlushAsync(timer.Token).ConfigureAwait(false);

            // <b>Closed, so the child finishes rather than waiting for a second question.</b> Its loop
            // reads until stdin ends; leaving the pipe open would leave a process per upload alive
            // until something killed it.
            process.StandardInput.Close();

            string? answer = await process.StandardOutput
                .ReadLineAsync(timer.Token).ConfigureAwait(false);

            if (answer is null)
            {
                throw new InvalidOperationException(
                    "The geodatabase reader exited without answering. Its stderr is on this server's "
                    + "own error stream — a missing native payload looks like this, and so does a "
                    + "crash inside GDAL.");
            }

            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);

            return JsonDocument.Parse(answer);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Kill(process);

            throw new InvalidOperationException(
                $"The geodatabase reader ran past its {deadline.TotalSeconds:0.#} s deadline and was "
                + "killed. This is the designed bound rather than a fault: no property of an archive "
                + "predicts what GDAL will do with it, so the limit is on execution.");
        }
        catch (OperationCanceledException)
        {
            // The caller gave up — a cancelled job, a stopping server. Same kill, and the exception
            // is theirs to see rather than ours to relabel.
            Kill(process);
            throw;
        }
        catch (JsonException malformed)
        {
            Kill(process);

            throw new InvalidOperationException(
                "The geodatabase reader answered something that is not JSON. The wire between the two "
                + "is one line of JSON in and one out, so this is a version mismatch or a write to "
                + "stdout that should have gone to stderr.",
                malformed);
        }
        finally
        {
            // A process that answered has already exited; one that threw on the way may not have.
            Kill(process);
        }
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException swept)
        {
            // Already gone between the check and the kill, which is the ordinary race and not news.
            Log.ImportReaderAlreadyGone(_log, swept);
        }
        catch (System.ComponentModel.Win32Exception refused)
        {
            Log.ImportReaderAlreadyGone(_log, refused);
        }
    }
}
