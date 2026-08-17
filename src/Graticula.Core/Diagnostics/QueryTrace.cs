using System;
using System.Diagnostics;
using System.Threading;

namespace Graticula.Diagnostics;

/// <summary>
/// Where a feature query spent its time, split at the boundary that matters.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-30, and the reason a fifth black-box probe would not have helped.</b>
/// Four benchmark rounds measured tiles and overlay; the word <em>query</em>
/// appears in neither results document. A probe run for the §66 performance gate
/// found throughput plateauing at five to seven times for twenty-four times the
/// concurrency — and said so from outside the process, where an allocation
/// ceiling, a connection pool limit and a contended host are indistinguishable.
/// The gate's own conclusion was that this needs in-process instrumentation.
/// </para>
/// <para>
/// <b>The split is "waiting for Postgres" against "our own code", because that
/// is the question.</b> If the plateau is time inside Npgsql, the answer is the
/// pool or the database. If it is decode and encode, it is A-037's allocation
/// ceiling and the fix is in this repository. Nothing else in the breakdown
/// distinguishes those two, and everything else is a rounding error beside them.
/// </para>
/// <para>
/// <b>It costs nothing when it is off, and that is a design constraint rather
/// than a hope.</b> The whole mechanism is one null check on an
/// <see cref="AsyncLocal{T}"/> per phase; no stopwatch is started, no object is
/// allocated and no timestamp is read unless a trace is running. Instrumentation
/// that changes the thing it measures is a fifth wrong harness, and this project
/// has had four.
/// </para>
/// <para>
/// <b><see cref="AsyncLocal{T}"/> rather than <c>[ThreadStatic]</c>, and that
/// distinction has already cost this project a bug.</b> An ASP.NET request
/// resumes on a different thread after every await, so thread-static
/// request-scoped state is invisible half the time and belongs to somebody else
/// the rest of it. Here that would mean a query's decode time landing on another
/// query's line.
/// </para>
/// </remarks>
public sealed class QueryTrace
{
    private static readonly AsyncLocal<QueryTrace?> Running = new();

    private long _sqlTicks;
    private long _decodeTicks;
    private long _rows;
    private long _vertices;

    /// <summary>The trace this request is recording into, or null.</summary>
    /// <remarks>
    /// <b>Null is the normal case</b> and every call site is written to make
    /// that free: <c>if (QueryTrace.Current is { } trace)</c> compiles to a
    /// field read and a branch.
    /// </remarks>
    public static QueryTrace? Current => Running.Value;

    /// <summary>Starts recording, and stops when disposed.</summary>
    /// <returns>A scope that clears the trace.</returns>
    /// <remarks>
    /// <b>The caller decides whether to call this at all</b> — normally by
    /// asking whether the logger that would receive the result is enabled. A
    /// trace nobody will read is pure cost.
    /// </remarks>
    public static Scope Begin()
    {
        QueryTrace trace = new();
        Running.Value = trace;

        return new Scope(trace);
    }

    /// <summary>Microseconds spent inside the database driver.</summary>
    /// <remarks>
    /// <b>Execution and every row wait, which is not the same as "time in
    /// Postgres".</b> It includes the wire and the driver's own parsing, and
    /// separating those needs the server's <c>pg_stat_statements</c> rather than
    /// a stopwatch here. What it does separate cleanly is time this process
    /// spent waiting from time it spent working.
    /// </remarks>
    public long SqlMicroseconds => Microseconds(_sqlTicks);

    /// <summary>Microseconds spent turning rows into features.</summary>
    /// <remarks>
    /// WKB into an object graph and column values into a row. This is the half
    /// A-037 predicted would bind, and the half nothing had measured.
    /// </remarks>
    public long DecodeMicroseconds => Microseconds(_decodeTicks);

    /// <summary>Rows read.</summary>
    public long Rows => Interlocked.Read(ref _rows);

    /// <summary>Coordinates decoded, which is the unit the cost scales with.</summary>
    /// <remarks>
    /// <b>Not features.</b> A hundred parcels and a hundred national outlines
    /// are the same row count and three orders of magnitude apart in work, and a
    /// per-request number that cannot tell them apart explains nothing.
    /// </remarks>
    public long Vertices => Interlocked.Read(ref _vertices);

    /// <summary>Adds time spent waiting on the driver.</summary>
    /// <param name="ticks">A <see cref="Stopwatch"/> tick count.</param>
    public void AddSql(long ticks) => Interlocked.Add(ref _sqlTicks, ticks);

    /// <summary>Adds time spent decoding, and what was decoded.</summary>
    /// <param name="ticks">A <see cref="Stopwatch"/> tick count.</param>
    /// <param name="vertices">Coordinates in the row.</param>
    public void AddDecode(long ticks, long vertices)
    {
        Interlocked.Add(ref _decodeTicks, ticks);
        Interlocked.Add(ref _vertices, vertices);
        Interlocked.Increment(ref _rows);
    }

    private static long Microseconds(ref long field) => Microseconds(Interlocked.Read(ref field));

    private static long Microseconds(long ticks) =>
        ticks * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Clears the trace when the request ends.</summary>
    /// <param name="trace">The trace that was started.</param>
    public readonly struct Scope(QueryTrace trace) : IDisposable
    {
        /// <summary>What was recorded.</summary>
        public QueryTrace Trace { get; } = trace;

        /// <summary>Stops recording.</summary>
        public void Dispose() => Running.Value = null;
    }
}
