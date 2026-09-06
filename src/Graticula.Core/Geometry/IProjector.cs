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
    /// Projects into a reference written out, for one EPSG has no code for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision, 2026-09-06:</b> <i>"epsg güzel ama wkt de kabul etmemiz lazım."</i> A
    /// national grid or a local system may have no number, and then the definition is the only
    /// way to name it.
    /// </para>
    /// <para>
    /// <b>No default implementation, and the first hour of this feature is why.</b> It was
    /// written as a default returning null — *this projector cannot* — which every implementer
    /// then inherited for free. `BreakingProjector` decorates the real one and inherited it too,
    /// so the server refused every definition PostGIS had just accepted by hand, and the message
    /// it refused with was about PROJ. **A default answer makes *cannot* and *nobody wrote this*
    /// the same word.** Every implementer answers now, and the compiler is what asks.
    /// </para>
    /// </remarks>
    /// <param name="geometries">What to move.</param>
    /// <param name="fromSrid">The reference they are in.</param>
    /// <param name="definition">The reference to put them in, written out.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The moved geometries, or null when this projector cannot.</returns>
    Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
        IReadOnlyList<Geometry> geometries,
        int fromSrid,
        string definition,
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

    /// <summary>
    /// What a reference can represent, in longitude and latitude, or null when unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-27 for [D-165](../../../docs/architecture-debt.md).</b>
    /// <see cref="ProjectionDomain"/> answers this for two families — geographic and Web
    /// Mercator — from arithmetic, and null for everything else. Null means *do not clamp*,
    /// so a caller asking a layer in a national grid for `bbox=-180,-90,180,90` had its
    /// filter passed to `st_transform` unclamped, which is what the OGC suite sends at every
    /// collection. The reference's own area of use is the answer, and this is where a
    /// deployment's projection authority is asked for it.
    /// </para>
    /// <para>
    /// <b>Null is a complete answer and not a failure.</b> A projection database that does
    /// not publish areas of use, a code it has never heard of, and a reference that genuinely
    /// has no bound are one answer here: *this server does not know, so do not clamp*. That
    /// is the behaviour before this method existed, which is what makes adding it safe.
    /// </para>
    /// <para>
    /// <b>Cached, for the same reason <see cref="KnowsAsync"/> is.</b> The area of use of
    /// EPSG:2180 changes when somebody edits the projection database, which is close enough
    /// to never.
    /// </para>
    /// </remarks>
    /// <param name="srid">The EPSG code of the reference.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The area of use in degrees, or null when this deployment cannot say.</returns>
    Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken);

    /// <summary>
    /// What a transformation between two references would be, without moving anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-25 for [Q-141](../../../docs/open-questions.md), whose answer is that
    /// the datum caution goes to the operator.</b> The FeatureServer's <c>outSR</c> and the
    /// tile path both reproject in SQL — <c>st_transform</c> inside the query the datastore
    /// runs — so neither passes through <see cref="ProjectAsync"/> and neither has ever seen
    /// a provenance record. That is [D-32](../../../docs/architecture-debt.md)'s remaining
    /// exposure, and it is a gap in *who gets told*, not in the mechanism: the decision about
    /// whether a pair of references crosses a datum is already made and already cached.
    /// </para>
    /// <para>
    /// <b>So this asks the question the geometry does not have to be present for.</b> The
    /// caller has a layer and a target reference and wants to know whether serving one from
    /// the other is exact by construction or a shift of unstated size. Asking through
    /// <see cref="ProjectAsync"/> with an empty list already answers it — that is what this
    /// makes explicit rather than incidental, and an empty list is not an obvious way to ask
    /// a question.
    /// </para>
    /// <para>
    /// <b>Same caching contract as <see cref="KnowsAsync"/>.</b> The answer depends only on
    /// the two references' definitions, which change when somebody edits the projection
    /// database. An implementation is expected to answer the second call without a round
    /// trip, because a request-path caller will ask on every request.
    /// </para>
    /// </remarks>
    /// <param name="fromSrid">The reference the data is in.</param>
    /// <param name="toSrid">The reference it would be served in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The provenance that <see cref="ProjectAsync"/> would report for this pair.
    /// </returns>
    Task<ProjectionProvenance> DescribeAsync(
        int fromSrid, int toSrid, CancellationToken cancellationToken);
}
