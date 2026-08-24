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
/// <b>What each bound bounds.</b> The deadline bounds time and that is all it is claimed to do.
/// `DOTNET_GCHeapHardLimit` is set the way the overlay worker sets it, but GDAL allocates natively,
/// outside the managed heap, so it does not bound a malicious archive's memory either.
/// <see cref="HostSettings.ImportScratchBudgetBytes"/> bounds what reaches the disk in the first
/// place. Saying so here rather than letting the environment variable imply otherwise.
/// </para>
/// <para>
/// <b>What was missing until 2026-08-24, and is [D-94](../../docs/architecture-debt.md)'s own
/// account of itself: a CPU or memory bound.</b> There are two now, and both are the parent's
/// rather than the child's, because the child is the part that cannot be trusted.
/// <see cref="MemoryCeilingBytes"/> is polled from outside and counts native allocation;
/// <see cref="ProcessPriorityClass.BelowNormal"/> leaves the import competing for the machine but
/// losing. Neither is a container, and the row still says a container is what ADR-016 §2 asks for.
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

    /// <summary>
    /// How much of the machine the child may hold, native allocation included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-94](../../docs/architecture-debt.md): the loop competes for the machine with the
    /// requests this process is serving</b>, and the row's own account of what was missing is a
    /// CPU or memory bound. `DOTNET_GCHeapHardLimit` bounds the managed heap and GDAL allocates
    /// outside it, so until now the only memory bound was the process being killable — by a
    /// deadline, which is a bound on time.
    /// </para>
    /// <para>
    /// <b>Two gigabytes, and the number is a judgement rather than a measurement.</b> Four times
    /// the managed ceiling, because the whole point is that the native side is the part nobody
    /// has measured; a limit close to the managed one would kill archives that are merely large.
    /// The owner's three real archives never approach it — the largest listing was 0.06 s — so
    /// what this bounds is the case nobody has met, which is what a bound is for.
    /// </para>
    /// <para>
    /// <b>Working set, not commit.</b> It is what both platforms report through
    /// <see cref="System.Diagnostics.Process.WorkingSet64"/> without a P/Invoke, and a runaway
    /// parser touches what it allocates. A job object on Windows and a cgroup on Linux would be
    /// stricter and would be two platform-specific paths to keep in step; recorded here as the
    /// stricter thing this is not.
    /// </para>
    /// </remarks>
    public const long MemoryCeilingBytes = 2L << 30;

    /// <summary>How often the child's memory is looked at.</summary>
    /// <remarks>
    /// <b>Four times a second, which is a compromise stated rather than tuned.</b> A parser can
    /// allocate a gigabyte between two samples, so this does not make the ceiling exact; it makes
    /// a runaway process die in a second rather than in two minutes.
    /// </remarks>
    private static readonly TimeSpan MemoryPoll = TimeSpan.FromMilliseconds(250);

    private readonly string _executable;
    private readonly ILogger<GeodatabaseReader> _log;
    private readonly long _ceiling;

    public GeodatabaseReader(string executable, ILogger<GeodatabaseReader> log)
        : this(executable, log, MemoryCeilingBytes)
    {
    }

    /// <summary>Creates the reader with a memory ceiling of its own.</summary>
    /// <param name="executable">Where the child is.</param>
    /// <param name="log">Where its diagnosis goes.</param>
    /// <param name="ceiling">
    /// How much the child may hold. <b>A parameter so that the guard can be tested against a
    /// ceiling a healthy child exceeds</b> — a test that waited for a real archive to allocate
    /// two gigabytes would be a test nobody runs.
    /// </param>
    internal GeodatabaseReader(string executable, ILogger<GeodatabaseReader> log, long ceiling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1L << 20);

        _executable = executable;
        _log = log;
        _ceiling = ceiling;
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

        /*
          <b>GDAL's careful ring organisation, and the corpus is why.</b>
          `OGR_ORGANIZE_POLYGONS` decides how the Shapefile driver turns a polygon record's
          rings into shells and holes. Its default for shapefiles is the fast one — trust
          the winding — and **some real files wind their rings the OGC way rather than the
          shapefile way**, which is what an ordinary export tool chain produces. Read by
          winding, a hole becomes a second overlapping shell and PostGIS says *nested
          shells*.

          <b>Measured on this repository's own corpus, both ways.</b> Fifty real OSM
          polygons: without this, PostGIS reported **47 of 50 valid** — *hole lies outside
          shell; nested shells*. With `DEFAULT`, which is the containment analysis, GDAL
          returns rings already grouped. The parser this replaced grouped by containment for
          exactly this reason and its own tests record the same number.

          <b>In the environment rather than as an open option, because it is not
          per-request.</b> Unlike `SHAPE_ENCODING` — see the reader's own `Open` — there is
          no case where this server wants the fast reading: a wrong hole is a wrong area and
          a failed intersection, for every caller of that layer, for as long as it is
          published.
        */
        start.Environment["OGR_ORGANIZE_POLYGONS"] = "DEFAULT";


        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The geodatabase reader at '{_executable}' did not start.");

        using CancellationTokenSource timer =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        timer.CancelAfter(deadline);

        Yield(process);

        using MemoryGuard memory = new(process, _ceiling, timer, MemoryPoll);

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

            throw new InvalidOperationException(TooMuch(memory) ??
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

    /// <summary>
    /// Sends one request and reads every line it answers with, until the trailer.
    /// </summary>
    /// <param name="request">The request object.</param>
    /// <param name="deadline">How long the whole stream has before the child is killed.</param>
    /// <param name="take">Called for each line that is neither the header nor the trailer.</param>
    /// <param name="cancellation">The caller's own cancellation.</param>
    /// <returns>The header and the trailer, so a caller can read the schema and the count.</returns>
    /// <remarks>
    /// <para>
    /// <b>Until the trailer, not until the pipe closes</b> — ADR-038 §5. A reader that dies halfway
    /// closes its pipe too, and *the stream ended* would then be indistinguishable from *the layer has no
    /// features*. The trailer is what separates them, and its absence is an error rather than an empty
    /// result.
    /// </para>
    /// <para>
    /// <b>One deadline for the whole stream rather than one per line.</b> A per-line clock would let a
    /// reader that answers a line a second run for ever on a large layer, which is the shape of hang this
    /// bound exists to stop. The number is the caller's because only the caller knows how much work it
    /// asked for.
    /// </para>
    /// </remarks>
    public async Task<(JsonDocument? Header, JsonDocument? Trailer)> StreamAsync(
        object request,
        TimeSpan deadline,
        Action<JsonDocument> take,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(take);

        if (!Available)
        {
            throw new InvalidOperationException(
                $"The geodatabase reader is not installed at '{_executable}'.");
        }

        ProcessStartInfo start = new(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.Environment["DOTNET_GCHeapHardLimit"] =
            HeapLimitBytes.ToString("X", CultureInfo.InvariantCulture);

        // The same reasoning as above, on the spawn the features path uses.
        start.Environment["OGR_ORGANIZE_POLYGONS"] = "DEFAULT";

        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_gcConcurrent"] = "0";

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The geodatabase reader at '{_executable}' did not start.");

        using CancellationTokenSource timer =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        timer.CancelAfter(deadline);

        Yield(process);

        using MemoryGuard memory = new(process, _ceiling, timer, MemoryPoll);

        JsonDocument? header = null;
        JsonDocument? trailer = null;

        try
        {
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request).AsMemory(), timer.Token).ConfigureAwait(false);

            await process.StandardInput.FlushAsync(timer.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            while (true)
            {
                string? line = await process.StandardOutput
                    .ReadLineAsync(timer.Token).ConfigureAwait(false);

                if (line is null)
                {
                    throw new InvalidOperationException(
                        "The geodatabase reader stopped before it finished. Its stderr is on this "
                        + "server's own error stream. A stream that ends without its trailer is a "
                        + "failure rather than an empty layer, which is the whole reason there is a "
                        + "trailer.");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument one = JsonDocument.Parse(line);

                if (one.RootElement.TryGetProperty("ok", out JsonElement ok) && !ok.GetBoolean())
                {
                    string why = one.RootElement.TryGetProperty("error", out JsonElement said)
                        ? said.GetString() ?? "no reason given"
                        : "no reason given";

                    one.Dispose();

                    throw new InvalidOperationException(why);
                }

                if (one.RootElement.TryGetProperty("header", out _))
                {
                    header = one;
                    continue;
                }

                if (one.RootElement.TryGetProperty("done", out _))
                {
                    trailer = one;
                    break;
                }

                using (one)
                {
                    take(one);
                }
            }

            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);

            return (header, trailer);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Kill(process);

            throw new InvalidOperationException(TooMuch(memory) ??
                $"The geodatabase reader ran past its {deadline.TotalSeconds:0.#} s deadline while "
                + "streaming and was killed. The bound is on execution because no property of an "
                + "archive predicts what GDAL will do with it.");
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>
    /// Watches a child's memory and stops it, so a bound on time is not the only bound.
    /// </summary>
    /// <remarks>
    /// <b>[D-94](../../docs/architecture-debt.md).</b> The parent polls because the child cannot
    /// be trusted to report on itself: the allocation being bounded is GDAL's, inside a process
    /// parsing a file somebody else chose. Cancelling the caller's timer is what surfaces it —
    /// the awaiting code already turns cancellation into a refusal, and <see cref="Exceeded"/> is
    /// how it tells this apart from the deadline.
    /// </remarks>
    private sealed class MemoryGuard : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public MemoryGuard(Process process, long ceiling, CancellationTokenSource cancel, TimeSpan poll)
        {
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            return;
                        }

                        process.Refresh();

                        long held = process.WorkingSet64;

                        if (held > Peak)
                        {
                            Peak = held;
                        }

                        if (held > ceiling)
                        {
                            Exceeded = held;

                            // <b>Cancel rather than kill from here.</b> The caller's `finally`
                            // owns the killing, and two paths killing one process is how a
                            // confusing exception gets logged instead of a clear refusal.
                            await cancel.CancelAsync().ConfigureAwait(false);

                            return;
                        }

                        await Task.Delay(poll, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // The process ended between HasExited and Refresh. Nothing to watch.
                        return;
                    }
                }
            });
        }

        /// <summary>What it was holding when it went past the ceiling, or null.</summary>
        public long? Exceeded { get; private set; }

        /// <summary>The most it was seen holding, for a message that can say so.</summary>
        public long Peak { get; private set; }

        public void Dispose()
        {
            _stop.Cancel();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // The loop's own faults are not the caller's problem; it watches, it does not act.
            }

            _stop.Dispose();
        }
    }

    /// <summary>
    /// Puts the child below the server in the scheduler's order.
    /// </summary>
    /// <remarks>
    /// <b>The other half of [D-94](../../docs/architecture-debt.md), and the cheaper half.</b> The
    /// row's complaint is that an import competes for the machine with the requests this process
    /// is serving. It still competes; it now loses. A parse that takes twice as long on a busy
    /// server is the right trade, because the requests are what somebody is waiting for.
    /// <para>
    /// <b>Best effort.</b> A process that has already exited throws, a platform that does not
    /// support it throws, and neither is a reason to fail an import. Logged at debug and dropped.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The refusal to write when memory was what ended the child, or null when it was time.
    /// </summary>
    /// <remarks>
    /// <b>Both bounds arrive as the same cancellation, and a caller cannot act on
    /// <i>something stopped it</i>.</b> An archive that is too big for this machine and an archive
    /// that is slow are different problems with different answers -- a bigger machine, or patience
    /// -- so the message names which one happened and how much was held when it did.
    /// </remarks>
    private static string? TooMuch(MemoryGuard memory)
    {
        if (memory.Exceeded is not { } held)
        {
            return null;
        }

        return $"The geodatabase reader held {held / (1024.0 * 1024.0):0} MB, past the "
            + $"{MemoryCeilingBytes / (1024.0 * 1024.0):0} MB this server allows one archive, and "
            + "was killed. The bound counts native allocation, which is where a GDAL driver spends "
            + "most of what it takes, and it exists so that one upload cannot take the machine away "
            + "from the requests this process is serving.";
    }

    private static void Yield(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // Nothing to do about it and nothing worth failing for.
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
