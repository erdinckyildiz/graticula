using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Geometries;

/// <summary>Which overlay to compute.</summary>
public enum OverlayOperation
{
    /// <summary>What both cover.</summary>
    Intersect,

    /// <summary>What either covers.</summary>
    Union,

    /// <summary>What the first covers and the second does not.</summary>
    Difference,
}

/// <summary>Why an overlay was not computed.</summary>
public enum OverlayRefusal
{
    /// <summary>It was.</summary>
    None,

    /// <summary>
    /// The pre-flight estimate said the work would be too large.
    /// </summary>
    /// <remarks>
    /// Cheap and leaky: benchmark finding 16 measured 83 ms foreseeing a
    /// 17-second operation, and also measured it under-predicting an
    /// adversarial input by fourteen times. It is a filter, never the bound.
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

/// <summary>The result of an overlay, or the reason there is none.</summary>
/// <param name="Geometries">What came back, empty when refused.</param>
/// <param name="Refusal">Why not, or <see cref="OverlayRefusal.None"/>.</param>
/// <param name="Message">A sentence for the caller.</param>
/// <param name="CandidatePairs">What the pre-flight counted, for diagnosis.</param>
/// <param name="Milliseconds">How long it took, including the pre-flight.</param>
public readonly record struct OverlayResult(
    IReadOnlyList<Geometry> Geometries,
    OverlayRefusal Refusal,
    string? Message,
    long CandidatePairs,
    long Milliseconds);

/// <summary>
/// General polygon overlay, bounded by something other than trust.
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
/// implementation runs the overlay somewhere it can be killed, with a memory
/// ceiling it cannot exceed. In-process is not an option: OverlayNG offers no
/// cooperative cancellation, <c>Thread.Abort</c> does not exist on .NET Core,
/// and the run that produced the 16.7 GB figure took the host into swap and
/// killed the Docker daemon with it.
/// </para>
/// <para>
/// <b>Every failure is a value here, not an exception.</b> A deadline and an
/// out-of-memory are ordinary outcomes of this operation rather than faults —
/// they are what the design exists to produce — and a caller that must handle
/// them is better served by a result it cannot ignore than by a catch it can
/// forget.
/// </para>
/// </remarks>
public interface IOverlay
{
    /// <summary>Computes an overlay, or refuses.</summary>
    /// <param name="operation">Which overlay.</param>
    /// <param name="left">The first operand, one or more geometries.</param>
    /// <param name="right">
    /// The second operand. Empty for <see cref="OverlayOperation.Union"/> of a
    /// single set.
    /// </param>
    /// <param name="srid">The reference both are in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, or the refusal.</returns>
    Task<OverlayResult> ComputeAsync(
        OverlayOperation operation,
        IReadOnlyList<Geometry> left,
        IReadOnlyList<Geometry> right,
        int srid,
        CancellationToken cancellationToken);
}
