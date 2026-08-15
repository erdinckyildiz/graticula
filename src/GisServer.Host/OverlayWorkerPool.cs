using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using Microsoft.Extensions.Logging;

namespace GisServer.Host;

/// <summary>
/// Runs overlays in worker processes, and kills the ones that run too long.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-97's answer, chosen by the project owner: a worker process with a
/// pre-flight.</b>
/// <see href="../../benchmarks/geometry-overlay/RESULTS.md">Measurement</see>
/// invalidated A-042 — a 6,408-vertex adversarial input cost 153 seconds and
/// 16.7 GB where a real 72,919-vertex national outline cost 312 ms and 17 MB —
/// and the run that produced that figure took the host into swap and killed the
/// Docker daemon. One unauthenticated request would have done it.
/// </para>
/// <para>
/// <b>Three bounds, and only two of them are real.</b> The pre-flight refuses
/// obviously explosive inputs and is <em>known</em> to leak: finding 16 measured
/// it under-predicting by fourteen times. The deadline is a bound because the
/// process is killed. The memory ceiling is a bound because the worker's GC is
/// given a hard limit it cannot exceed — it dies rather than the machine.
/// </para>
/// <para>
/// <b>A killed worker is not reused.</b> There is no way to know what a process
/// was in the middle of when it died, and a pool that returns a killed worker to
/// service is a pool that hands the next caller somebody else's half-written
/// response. Retire it and start another.
/// </para>
/// <para>
/// <b>The pool is small on purpose.</b> Each worker may allocate up to its
/// ceiling, so the server's exposure is workers × ceiling and nothing else. Two
/// workers at 1 GB is a number an operator can reason about; a worker per
/// request is not.
/// </para>
/// </remarks>
internal sealed class OverlayWorkerPool : IOverlay, IAsyncDisposable
{
    /// <summary>How long an overlay may run before its worker is killed.</summary>
    /// <remarks>
    /// <b>Ten seconds, and the shape of the measurements is why.</b> Every real
    /// case in the corpus finished inside 350 ms, and the smallest adversarial
    /// input that matters took 17 seconds. Ten leaves the real work thirty times
    /// its measured cost and still refuses the attack — and the cost of being
    /// wrong is a refusal, not a wrong answer.
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _deadline;
    private readonly long _maximumCandidatePairs;

    /// <summary>
    /// The pre-flight threshold, in candidate segment pairs.
    /// </summary>
    /// <remarks>
    /// <b>Set above the real corpus and below the attack, which is the only
    /// honest way to place it.</b> The largest real case measured 48,066 pairs
    /// at 62 ms; the 200-teeth comb measured 131,049 at 3.4 seconds. 100,000
    /// admits every real case measured and turns the larger combs away before
    /// any arithmetic happens. It does <em>not</em> catch the 100-teeth comb at
    /// 33,129 pairs and 884 ms — finding 16 says nothing at this layer will —
    /// and that case is what the deadline is for.
    /// </remarks>
    public const long MaximumCandidatePairs = 100_000;

    /// <summary>The heap a worker may not exceed.</summary>
    /// <remarks>
    /// Passed as <c>DOTNET_GCHeapHardLimit</c>, which the runtime enforces by
    /// throwing <see cref="OutOfMemoryException"/> rather than by asking the
    /// operating system for more. That is what turns "16.7 GB" into "this
    /// request failed".
    /// </remarks>
    public const long HeapLimitBytes = 1L << 30;

    /// <summary>
    /// The worker executable, in its own directory beside the server's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Beside the host rather than on the PATH or in configuration.</b> The
    /// two are built and shipped together and their versions have to match — the
    /// pipe protocol is private between them — so making the location
    /// configurable would create a way to pair mismatched builds and nothing
    /// else.
    /// </para>
    /// <para>
    /// <b>In its own subdirectory, and that was a bug before it was a
    /// decision.</b> Copying the executable alone next to the host left
    /// NetTopologySuite behind, and every overlay answered "Could not load file
    /// or assembly" — from inside the worker, where the host could only report
    /// it as an invalid request. The worker has its own dependency closure and
    /// needs somewhere to put it.
    /// </para>
    /// </remarks>
    public static string ExecutableBesideThisOne()
    {
        string name = OperatingSystem.IsWindows()
            ? "GisServer.Overlay.Worker.exe"
            : "GisServer.Overlay.Worker";

        return Path.Combine(AppContext.BaseDirectory, "overlay", name);
    }

    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Worker> _idle = [];
    private readonly string _executable;
    private readonly ILogger _log;
    private bool _disposed;

    /// <summary>Creates a pool.</summary>
    /// <param name="executable">The worker executable.</param>
    /// <param name="workers">How many may run at once.</param>
    /// <param name="loggerFactory">For the kill record.</param>
    /// <param name="deadline">
    /// How long an overlay may run, or null for <see cref="Deadline"/>.
    /// <b>Overridable so the bound can be tested.</b> Proving that a deadline
    /// kills a worker needs an overlay that outlives it, and the cheapest
    /// adversarial input that outlives ten seconds also allocates gigabytes —
    /// so the test shortens the deadline instead of enlarging the input.
    /// </param>
    /// <param name="maximumCandidatePairs">
    /// The pre-flight threshold, or null for <see cref="MaximumCandidatePairs"/>.
    /// </param>
    public OverlayWorkerPool(
        string executable,
        int workers,
        ILoggerFactory loggerFactory,
        TimeSpan? deadline = null,
        long? maximumCandidatePairs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _executable = executable;
        _slots = new SemaphoreSlim(workers, workers);
        _log = loggerFactory.CreateLogger("overlay");
        _deadline = deadline ?? Deadline;
        _maximumCandidatePairs = maximumCandidatePairs ?? MaximumCandidatePairs;
    }

    /// <summary>Whether the worker executable is where it should be.</summary>
    /// <remarks>
    /// Checked once at startup rather than on the first request, so a packaging
    /// mistake is a line in the log at boot instead of a 503 the first time
    /// somebody uses the feature.
    /// </remarks>
    public bool Available => File.Exists(_executable);

    /// <inheritdoc/>
    public async Task<OverlayResult> ComputeAsync(
        OverlayOperation operation,
        IReadOnlyList<Geometry> left,
        IReadOnlyList<Geometry> right,
        int srid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!Available)
        {
            return new OverlayResult(
                [],
                OverlayRefusal.Unavailable,
                $"The overlay worker is not installed at '{_executable}'. Overlay operations "
                + "run in a separate process so they can be killed on a deadline (Q-97), and "
                + "without it they are not offered.",
                0,
                0);
        }

        // Waiting for a slot is bounded by the same deadline as the work: a
        // caller queued behind two long overlays should be told the server is
        // busy, not left holding a connection for a minute.
        if (!await _slots.WaitAsync(_deadline, cancellationToken).ConfigureAwait(false))
        {
            return new OverlayResult(
                [],
                OverlayRefusal.Unavailable,
                "Every overlay worker is busy. Overlay runs in a small pool of processes so that "
                + "its memory cost is bounded; try again shortly.",
                0,
                0);
        }

        Worker? worker = null;

        try
        {
            worker = await RentAsync(cancellationToken).ConfigureAwait(false);

            return await worker
                .ComputeAsync(
                    operation, left, right, srid, _deadline, _maximumCandidatePairs,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (worker is not null)
            {
                if (worker.Healthy)
                {
                    _idle.Add(worker);
                }
                else
                {
                    // Killed, crashed, or out of memory. Whatever it was doing,
                    // it is not a process to hand to the next caller.
                    worker.Dispose();
                }
            }

            _slots.Release();
        }
    }

    /// <summary>
    /// An idle worker, or a new one that has already been warmed.
    /// </summary>
    /// <remarks>
    /// <b>Warmed outside the deadline, because the deadline bounds the overlay
    /// and not the runtime's start-up.</b> Found by a test: after a worker was
    /// killed, the very next request — a trivial two-square intersection — also
    /// hit its deadline, because it was paying for a cold process launch inside
    /// its own budget. At the production deadline of ten seconds that is
    /// invisible; it would have surfaced as a rare, unexplained timeout after
    /// every kill, which is the worst way for it to surface.
    /// </remarks>
    private async Task<Worker> RentAsync(CancellationToken cancellationToken)
    {
        while (_idle.TryTake(out Worker? idle))
        {
            if (idle.Healthy)
            {
                return idle;
            }

            idle.Dispose();
        }

        Worker started = Worker.Start(_executable, _log);

        await started.WarmUpAsync(cancellationToken).ConfigureAwait(false);

        return started;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_idle.TryTake(out Worker? worker))
        {
            worker.Dispose();
        }

        _slots.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>One worker process and the pipe to it.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly ILogger _log;
        private readonly byte[] _header = new byte[4];
        private bool _broken;

        private Worker(Process process, ILogger log)
        {
            _process = process;
            _log = log;
        }

        public bool Healthy => !_broken && !_process.HasExited;

        public static Worker Start(string executable, ILogger log)
        {
            ProcessStartInfo start = new(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,

                // Left attached, so a stack trace from the worker reaches the
                // server's own stderr instead of vanishing.
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // <b>The memory bound, and it is the runtime's rather than ours.</b>
            // GCHeapHardLimit makes the worker throw OutOfMemoryException at the
            // ceiling instead of asking the operating system for more — which is
            // what stops one request taking the machine into swap.
            start.Environment["DOTNET_GCHeapHardLimit"] =
                HeapLimitBytes.ToString("X", CultureInfo.InvariantCulture);

            // Workstation, non-concurrent: a background GC thread per worker is
            // cost with nothing to buy, and server GC would size itself to the
            // machine rather than to the ceiling.
            start.Environment["DOTNET_gcServer"] = "0";
            start.Environment["DOTNET_gcConcurrent"] = "0";

            Process process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    $"The overlay worker at '{executable}' did not start.");

            return new Worker(process, log);
        }

        /// <summary>
        /// How long a newly launched worker has to become responsive.
        /// </summary>
        /// <remarks>
        /// Generous, because it covers process creation and runtime start-up on
        /// a machine that may be busy — and because exceeding it is a broken
        /// installation rather than an expensive request, so a long wait costs
        /// nothing in the case that matters.
        /// </remarks>
        private static readonly TimeSpan StartUp = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Sends one trivial request, so the first real one is not timed
        /// against a cold runtime.
        /// </summary>
        /// <remarks>
        /// An empty request, which the worker answers <c>Invalid</c> — the reply
        /// is not the point, the round trip is. It proves the pipe works and
        /// that the runtime has started before any deadline begins.
        /// </remarks>
        public async Task WarmUpAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ComputeAsync(
                    OverlayOperation.Union, [], [], 0, StartUp, long.MaxValue, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A worker that cannot answer a trivial request is broken, and
                // ComputeAsync has already marked it so. The caller finds out
                // when its own request fails, with a message about the real
                // request rather than about a warm-up it never made.
            }
        }

        public async Task<OverlayResult> ComputeAsync(
            OverlayOperation operation,
            IReadOnlyList<Geometry> left,
            IReadOnlyList<Geometry> right,
            int srid,
            TimeSpan deadline,
            long maximumCandidatePairs,
            CancellationToken cancellationToken)
        {
            Stopwatch clock = Stopwatch.StartNew();

            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Operation = operation.ToString(),
                Left = Encode(left),
                Right = Encode(right),
                Srid = srid,
                MaximumCandidatePairs = maximumCandidatePairs,
            });

            using CancellationTokenSource timer =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timer.CancelAfter(deadline);

            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(_header, request.Length);

                await _process.StandardInput.BaseStream
                    .WriteAsync(_header, timer.Token).ConfigureAwait(false);

                await _process.StandardInput.BaseStream
                    .WriteAsync(request, timer.Token).ConfigureAwait(false);

                await _process.StandardInput.BaseStream.FlushAsync(timer.Token)
                    .ConfigureAwait(false);

                byte[] response = await ReadFrameAsync(timer.Token).ConfigureAwait(false);

                Reply reply = JsonSerializer.Deserialize<Reply>(response)
                    ?? throw new InvalidDataException("The worker replied with null.");

                if (!reply.Healthy)
                {
                    // OutOfMemory leaves the worker's heap in an unknown state,
                    // so it is retired even though it answered.
                    _broken = reply.Refusal == "OutOfMemory";
                }

                return reply.ToResult(clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // <b>The deadline, and this is the only line that is a bound.</b>
                // Killing the process is what stops the work; everything else in
                // this class is bookkeeping around that fact.
                Kill();

                Log.OverlayKilled(_log, (long)deadline.TotalMilliseconds, null);

                return new OverlayResult(
                    [],
                    OverlayRefusal.Deadline,
                    $"The overlay ran longer than {deadline.TotalSeconds:0.##} seconds and was "
                    + "stopped. Overlay cost grows with the number of edge crossings rather than "
                    + "with the size of the input, so a small pair of shapes can be an expensive "
                    + "one — see benchmarks/geometry-overlay. Simplify the inputs, or overlay "
                    + "fewer of them at once.",
                    0,
                    clock.ElapsedMilliseconds);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
            {
                Kill();

                return new OverlayResult(
                    [],
                    OverlayRefusal.Unavailable,
                    "The overlay worker stopped responding. The request was not completed.",
                    0,
                    clock.ElapsedMilliseconds);
            }
        }

        private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            await ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);

            int length = BinaryPrimitives.ReadInt32LittleEndian(_header);

            if (length is < 0 or > 256 * 1024 * 1024)
            {
                throw new InvalidDataException($"The worker announced a {length}-byte frame.");
            }

            byte[] frame = new byte[length];

            await ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);

            return frame;
        }

        private async Task ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
        {
            int filled = 0;

            while (filled < destination.Length)
            {
                int read = await _process.StandardOutput.BaseStream
                    .ReadAsync(destination.AsMemory(filled), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    throw new IOException("The overlay worker closed its output.");
                }

                filled += read;
            }
        }

        private void Kill()
        {
            _broken = true;

            try
            {
                if (!_process.HasExited)
                {
                    // <b>Not entireProcessTree.</b> That overload enumerates
                    // every process on the machine to find descendants, which
                    // allocates enough to matter — under a parallel test run it
                    // threw OutOfMemoryException from inside the kill, which is
                    // a memorable way to learn that a safety mechanism can be
                    // the thing that fails. The worker spawns nothing, so there
                    // is no tree to walk.
                    _process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone, which is the outcome being asked for.
            }
        }

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }

        private static string[] Encode(IReadOnlyList<Geometry> geometries)
        {
            string[] encoded = new string[geometries.Count];

            for (int i = 0; i < geometries.Count; i++)
            {
                encoded[i] = Convert.ToBase64String(WkbWriter.ToArray(geometries[i]));
            }

            return encoded;
        }

        private sealed class Reply
        {
            public List<string> Geometries { get; set; } = [];

            public string Refusal { get; set; } = string.Empty;

            public string? Message { get; set; }

            public long CandidatePairs { get; set; }

            public long Milliseconds { get; set; }

            public bool Healthy => Refusal.Length == 0 || Refusal == "TooLarge";

            public OverlayResult ToResult(long elapsed)
            {
                List<Geometry> geometries = [];

                foreach (string wkb in Geometries)
                {
                    geometries.Add(WkbReader.Read(Convert.FromBase64String(wkb)));
                }

                OverlayRefusal refusal = Refusal switch
                {
                    "" => OverlayRefusal.None,
                    "TooLarge" => OverlayRefusal.TooLarge,
                    "OutOfMemory" => OverlayRefusal.OutOfMemory,
                    _ => OverlayRefusal.Invalid,
                };

                return new OverlayResult(geometries, refusal, Message, CandidatePairs, elapsed);
            }
        }
    }
}
