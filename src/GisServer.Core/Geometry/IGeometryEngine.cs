using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Geometries;

/// <summary>What the engine is being asked to compute.</summary>
/// <remarks>
/// <b>This enum was <c>OverlayOperation</c> and had three members.</b> It grew on
/// 2026-08-15, when the project owner overturned the rule that decided which
/// operations this server offers — see <see cref="IGeometryEngine"/>. Overlay is
/// now one thing the engine does rather than the only one, which is why the type
/// no longer carries that name.
/// </remarks>
public enum EngineOperation
{
    /// <summary>What both cover.</summary>
    Intersect,

    /// <summary>What either covers.</summary>
    Union,

    /// <summary>What the first covers and the second does not.</summary>
    Difference,

    /// <summary>The pieces the first splits into along the second.</summary>
    Cut,

    /// <summary>Everything within a distance of the input.</summary>
    Buffer,

    /// <summary>The input's boundary moved sideways by a distance.</summary>
    Offset,

    /// <summary>The input made valid — self-intersections repaired.</summary>
    /// <remarks>
    /// <b>Not vertex reduction.</b> That is
    /// <c>GeometryOperations.Generalize</c>, computed in process, and the two
    /// share a name in no vocabulary but ArcGIS's — where this one is
    /// <c>simplify</c> and the other is <c>generalize</c>.
    /// </remarks>
    Simplify,

    /// <summary>How two geometries are topologically related.</summary>
    Relate,

    /// <summary>The shortest distance between two geometries.</summary>
    Distance,
}

/// <summary>Why a computation did not happen.</summary>
public enum EngineRefusal
{
    /// <summary>It did.</summary>
    None,

    /// <summary>
    /// The pre-flight estimate said the work would be too large.
    /// </summary>
    /// <remarks>
    /// <b>Off by default since 2026-08-15, and kept only as a knob.</b> It was
    /// measured leaky when it was introduced — finding 16 had it under-predicting
    /// an adversarial input by fourteen times — and a filter that is both leaky
    /// and refuses real work is the worst of both. The owner's decision made the
    /// choice: the deadline and the memory ceiling are the bounds, and an
    /// operator who wants a cheaper pre-filter can still set one.
    /// </remarks>
    TooLarge,

    /// <summary>It ran past its deadline and the worker was killed.</summary>
    Deadline,

    /// <summary>It exhausted the worker's memory limit and the worker died.</summary>
    OutOfMemory,

    /// <summary>The input was not something the engine could work with.</summary>
    Invalid,

    /// <summary>No worker could be obtained.</summary>
    Unavailable,
}

/// <summary>One computation, as the caller describes it.</summary>
/// <param name="Operation">Which computation.</param>
/// <param name="Left">The first operand, one or more geometries.</param>
/// <param name="Right">
/// The second operand. Empty for <see cref="EngineOperation.Union"/> of a single
/// set, and for the operations that take one geometry and a number.
/// </param>
/// <param name="Srid">The reference every operand is in.</param>
/// <remarks>
/// <b>A record rather than a parameter list, because the parameter list stopped
/// fitting.</b> <c>Buffer</c> needs a distance, <c>Relate</c> needs a pattern,
/// and the three overlays need neither. Threading optional positional arguments
/// through two process boundaries is how a protocol acquires arguments nobody
/// can account for.
/// </remarks>
public readonly record struct EngineRequest(
    EngineOperation Operation,
    IReadOnlyList<Geometry> Left,
    IReadOnlyList<Geometry> Right,
    int Srid)
{
    /// <summary>
    /// The distance for <see cref="EngineOperation.Buffer"/> and
    /// <see cref="EngineOperation.Offset"/>, in the units of the reference.
    /// </summary>
    /// <remarks>
    /// <b>Negative is meaningful and is not an error.</b> A negative buffer
    /// shrinks a polygon and may empty it; a negative offset puts the curve on
    /// the other side. Rejecting the sign would remove half of each operation.
    /// </remarks>
    public double Distance { get; init; }

    /// <summary>
    /// A DE-9IM pattern for <see cref="EngineOperation.Relate"/>, or null to
    /// return the matrix itself.
    /// </summary>
    public string? Pattern { get; init; }
}

/// <summary>The result, or the reason there is none.</summary>
/// <param name="Geometries">What came back, empty when there is no geometry.</param>
/// <param name="Refusal">Why not, or <see cref="EngineRefusal.None"/>.</param>
/// <param name="Message">A sentence for the caller.</param>
/// <param name="CandidatePairs">What the pre-flight counted, for diagnosis.</param>
/// <param name="Milliseconds">How long it took, including the pre-flight.</param>
/// <remarks>
/// <b>Three of these fields are mutually exclusive and the type does not say
/// so.</b> An operation answers with geometry, with a number, or with a matrix,
/// never with two. A closed union of three shapes would say it in the type; it
/// would also mean three wire formats and three deserialisers for a difference
/// the caller resolves by knowing which operation it asked for. Recorded as the
/// compromise it is rather than defended.
/// </remarks>
public readonly record struct EngineResult(
    IReadOnlyList<Geometry> Geometries,
    EngineRefusal Refusal,
    string? Message,
    long CandidatePairs,
    long Milliseconds)
{
    /// <summary>The answer to <see cref="EngineOperation.Distance"/>.</summary>
    public double? Scalar { get; init; }

    /// <summary>
    /// The nine-character DE-9IM matrix from <see cref="EngineOperation.Relate"/>.
    /// </summary>
    public string? Matrix { get; init; }

    /// <summary>
    /// Which pairs satisfied a <see cref="EngineOperation.Relate"/> pattern.
    /// </summary>
    /// <remarks>
    /// <b>Index pairs into the request's own lists</b> — the left index and the
    /// right index, in that order. Returning the geometries again would repeat
    /// input the caller already has, and for two sets of thirty a full match is
    /// nine hundred of them.
    /// </remarks>
    public IReadOnlyList<int[]>? Pairs { get; init; }
}

/// <summary>
/// The geometry operations that need a topology engine, bounded by something
/// other than trust.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, and the isolation it describes is the whole decision.</b>
/// <see href="../../../benchmarks/geometry-overlay/RESULTS.md">Measurement</see>
/// invalidated A-042: a 6,408-vertex adversarial input cost 153 seconds and
/// 16.7 GB where a real 72,919-vertex national outline cost 312 ms and 17 MB. No
/// property of the input bounds the work — vertex count fails outright, and
/// candidate pairs under-predict by an order of magnitude. The only quantity
/// that bounds the work is the work itself.
/// </para>
/// <para>
/// <b>So the bound is a process, not a number</b> (Q-97, owner's choice). An
/// implementation runs the work somewhere it can be killed, with a memory
/// ceiling it cannot exceed. In-process is not an option: OverlayNG offers no
/// cooperative cancellation, <c>Thread.Abort</c> does not exist on .NET Core,
/// and the run that produced the 16.7 GB figure took the host into swap and
/// killed the Docker daemon with it.
/// </para>
/// <para>
/// <b>What this port is <em>for</em> changed on 2026-08-15, and the owner
/// changed it.</b> It used to hold three operations because the rest were judged
/// too dangerous to offer; six more had been refused with a sentence about
/// overlay, and the surface was described as splitting "by cost shape, not by
/// usefulness". The owner's instruction was that this is not the server's call
/// to make: <em>if a caller wants to do something absurd, let them; we put a
/// timeout on it.</em> That is now the rule. <b>The server bounds cost; it does
/// not decide usefulness.</b> Every operation that needs a topology engine comes
/// through here, and every one of them is bounded the same way — by a deadline
/// that kills the process and a heap limit the process cannot exceed.
/// </para>
/// <para>
/// <b>The bound has to be the process, and a timeout alone would not have
/// been enough.</b> A deadline answers the 153 seconds. It does not answer the
/// 16.7 GB, which is not slow — it is an allocation the host cannot survive, and
/// on a request the server could still be measuring when the machine goes into
/// swap. Both bounds together are what make the owner's rule implementable, and
/// the reason it is implementable today is that Q-97 already built them.
/// </para>
/// <para>
/// <b>Every failure is a value here, not an exception.</b> A deadline and an
/// out-of-memory are ordinary outcomes of these operations rather than faults —
/// they are what the design exists to produce — and a caller that must handle
/// them is better served by a result it cannot ignore than by a catch it can
/// forget.
/// </para>
/// </remarks>
public interface IGeometryEngine
{
    /// <summary>Computes, or refuses.</summary>
    /// <param name="request">What to compute.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, or the refusal.</returns>
    Task<EngineResult> ComputeAsync(EngineRequest request, CancellationToken cancellationToken);
}
