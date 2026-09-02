using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>
/// A source that can say what version a row is at, maintained by the database rather than by
/// us.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-186](../../../docs/architecture-debt.md), and
/// [ADR-005](../../../docs/adr/ADR-005-api-architecture.md) §3.8 chose the shape.</b> Its
/// condition 4 requires optimistic concurrency to be built on database-maintained state,
/// never on our own record of events, and §3.8 says why in one sentence: *anyone with
/// database credentials — QGIS, a script, a DBA — can write rows directly … our bookkeeping
/// cannot assume it sees every change.* A version this server remembers would be wrong the
/// first time somebody edits around it, and wrong in the direction that loses data quietly.
/// </para>
/// <para>
/// <b>A separate interface, not a member of <c>IFeatureSource</c>.</b> Whether a source can
/// version a row is a property of the source: PostgreSQL has `xmin` on every row and a file
/// format has nothing of the kind. Putting it on the main interface would oblige every future
/// provider to answer a question it may have no answer to, and the honest answers — a null,
/// or a throw — are both worse than not being asked.
/// </para>
/// <para>
/// <b>An opaque string, deliberately.</b> The caller compares it and never parses it. Today
/// it is PostgreSQL's transaction id; on a hosted layer it could become a version column
/// without any caller noticing, which is the point of it having no shape.
/// </para>
/// </remarks>
public interface IFeatureVersions
{
    /// <summary>
    /// The version of one row, or null when there is no such row.
    /// </summary>
    /// <param name="identity">The row's identity, as the layer's identity column holds it.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>An opaque version, or null when the row is not there.</returns>
    Task<string?> VersionOfAsync(long identity, CancellationToken cancellationToken);
}
