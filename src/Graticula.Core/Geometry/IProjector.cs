using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Geometries;

/// <summary>
/// What a transformation actually did, so a caller can judge it.
/// </summary>
/// <param name="Engine">Which library performed it, and its version.</param>
/// <param name="Accuracy">
/// The transformation's stated accuracy in metres, or null when the engine does
/// not say.
/// </param>
/// <remarks>
/// <para>
/// <b>The accuracy is still null and the caution is what replaced it.</b>
/// <c>ST_Transform</c> does not say which pipeline PROJ chose or what its stated
/// accuracy is, and getting that out needs PROJ's operation database, which this
/// server does not have (Q-100). What it <em>can</em> tell, from the two
/// references alone, is whether a datum change was required at all — and that
/// is the whole of the difference between a transformation that is exact by
/// construction and one that can silently be metres out. 4326 to 3857 is a
/// closed formula on one datum. A national grid to WGS 84 is a datum change, and
/// when the shift grids for the accurate path are absent PROJ falls back to a
/// ballpark transformation <em>without failing</em>.
/// </para>
/// <para>
/// <b>Saying "a datum change happened and its accuracy is unstated" is not the
/// answer geometry-crs-policy §3 asks for, and it is a great deal better than
/// silence.</b> D-32: the failure it describes has no error, no log line and no
/// visual signature — the map looks right and is in the wrong place. A caution
/// on the response is the first thing that gives it any signature at all.
/// </para>
/// </remarks>
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
/// <param name="DatumShift">
/// Whether the two references sit on different datums, or null when the server
/// could not tell.
/// </param>
/// <param name="Caution">
/// What the caller needs to know about this particular transformation's
/// trustworthiness, or null when there is nothing to say.
/// </param>
public readonly record struct ProjectionProvenance(
    string Engine,
    double? Accuracy,
    bool? DatumShift = null,
    string? Caution = null);

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

    /// <summary>
    /// Whether this deployment can work in a coordinate reference system at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-20 because a syntactically valid CRS code is not a real one, and
    /// finding that out late is expensive.</b> A protocol face can check that
    /// <c>urn:ogc:def:crs:EPSG::999999</c> is spelled correctly; nothing short of asking
    /// the projection authority can tell it that no such system exists. WFS was asking
    /// by trying: it wrote the response header, began streaming, and the database
    /// refused the transform on the first row — leaving a well-formed document that
    /// announced a thousand features and carried none.
    /// </para>
    /// <para>
    /// <b>Cheap enough to ask before every request that names one.</b> The set of
    /// systems a deployment knows changes when somebody edits the projection database,
    /// which is close enough to never that an implementation is expected to cache the
    /// answer. The first request for a given code pays a round trip and the rest pay
    /// nothing.
    /// </para>
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the system is one this deployment can project into.</returns>
    Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken);
}
