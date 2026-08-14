using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Geometries;

/// <summary>
/// What a transformation actually did, so a caller can judge it.
/// </summary>
/// <param name="Engine">Which library performed it, and its version.</param>
/// <param name="Accuracy">
/// The transformation's stated accuracy in metres, or null when the engine does
/// not say.
/// </param>
/// <remarks>
/// <b>Reported, not assumed</b> —
/// <see href="../../../docs/geometry-crs-policy.md">geometry-crs-policy</see> §3.
/// Several transformation paths usually exist between two coordinate reference
/// systems and they differ by metres; for cadastral, survey and engineering work
/// that difference is legally significant. PROJ picks one, and when the shift
/// grids for the accurate path are absent it silently falls back to a ballpark
/// transformation rather than failing. **A silent default is the problem; a
/// documented default is not.**
/// </remarks>
public readonly record struct ProjectionProvenance(string Engine, double? Accuracy);

/// <summary>
/// Moves geometry between coordinate reference systems.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 2 behind a port</b> (build-vs-adopt §4): projection is on the list of
/// things established libraries may do, and no library type may appear in a
/// Tier 1 signature. Everything here is ours — <see cref="Geometry"/>, an int
/// SRID, and a provenance record.
/// </para>
/// <para>
/// <b>Asynchronous, which looks wrong for arithmetic.</b> It is, if the engine
/// is in-process. The implementation is not: it uses the datastore's PROJ, which
/// is a round trip. Making the port synchronous would force that round trip to
/// block a request thread, and changing it later would touch every caller.
/// </para>
/// </remarks>
public interface IProjector
{
    /// <summary>
    /// Projects geometries into another coordinate reference system.
    /// </summary>
    /// <param name="geometries">What to project.</param>
    /// <param name="fromSrid">The system they are in.</param>
    /// <param name="toSrid">The system to put them in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The projected geometries, in order, and what did it.</returns>
    /// <remarks>
    /// <b>A batch, not one at a time.</b> The whole point of this shape is that a
    /// caller projecting two hundred points pays one round trip rather than two
    /// hundred, and that the transformation is set up once.
    /// </remarks>
    Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
        IReadOnlyList<Geometry> geometries,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken);
}
