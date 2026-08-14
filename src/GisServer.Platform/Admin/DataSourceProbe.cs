using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Platform.Admin;

/// <summary>
/// How far a connection attempt got.
/// </summary>
/// <remarks>
/// ADR-017 §3.3 requires these to be distinguishable: <em>one generic failure
/// covering all three is what makes registration hostile.</em> They are ordered,
/// and each one is reached only if the previous succeeded.
/// </remarks>
public enum ProbeOutcome
{
    /// <summary>Connected, privileged, and the data is usable.</summary>
    Usable,

    /// <summary>
    /// Could not connect at all — host, port, TLS, or credentials.
    /// </summary>
    /// <remarks>
    /// The administrator's next move is network or password. Nothing about the
    /// data is known yet, and claiming otherwise would send them to the wrong
    /// place.
    /// </remarks>
    CannotConnect,

    /// <summary>Connected, but lacking the rights to do anything useful.</summary>
    /// <remarks>
    /// A different person fixes this — usually a DBA, with a <c>grant</c>. Told
    /// apart from the above because the credential is <em>correct</em>.
    /// </remarks>
    InsufficientPrivilege,

    /// <summary>
    /// Connected and privileged, but the geometry cannot be served.
    /// </summary>
    /// <remarks>
    /// No PostGIS, no geometry columns, or geometry with no usable SRID. The
    /// credential and the grants are fine; the data is the problem.
    /// </remarks>
    UnusableGeometry,
}

/// <summary>A candidate layer found on a data source.</summary>
/// <param name="SchemaName">Its schema.</param>
/// <param name="TableName">Its table or view.</param>
/// <param name="GeometryColumn">The geometry column.</param>
/// <param name="Srid">Its SRID, or 0 if undeclared.</param>
/// <param name="GeometryType">The declared geometry type, or null if mixed.</param>
/// <param name="CandidateObjectIdColumn">
/// A unique integer column suitable as an ArcGIS object id, or null if there is
/// none — ADR-013 §2a's requirement, answered before publishing rather than at
/// the first query.
/// </param>
/// <param name="Writable">Whether our credential may write to it.</param>
public readonly record struct SourceTable(
    string SchemaName,
    string TableName,
    string GeometryColumn,
    int Srid,
    string? GeometryType,
    string? CandidateObjectIdColumn,
    bool Writable);

/// <summary>What a probe found.</summary>
/// <param name="Outcome">How far it got.</param>
/// <param name="Message">A sentence an administrator can act on.</param>
/// <param name="ServerVersion">The database version, if we got that far.</param>
/// <param name="PostgisVersion">The PostGIS version, if present.</param>
/// <param name="Tables">What could be published, if we got that far.</param>
public sealed record ProbeResult(
    ProbeOutcome Outcome,
    string Message,
    string? ServerVersion,
    string? PostgisVersion,
    IReadOnlyList<SourceTable> Tables)
{
    /// <summary>Whether anything can be published from this source.</summary>
    public bool CanPublish => Outcome == ProbeOutcome.Usable && Tables.Count > 0;
}

/// <summary>
/// Tests a data source without creating anything.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §3.3's <b>dry run that creates no state</b>. It is the difference
/// between registration that tells you what is wrong and registration that
/// leaves a broken row behind for you to find later.
/// </para>
/// <para>
/// <b>It also answers the ADR-013 §2a question up front</b>: whether each table
/// has an integer column usable as an ArcGIS object id. Discovering that at the
/// first client query — which is what happened before this existed — means the
/// layer was published, looked fine, and failed for a reason the publisher never
/// saw.
/// </para>
/// </remarks>
public interface IDataSourceProbe
{
    /// <summary>Connects, checks rights, and lists what could be published.</summary>
    /// <param name="connectionString">The candidate connection string.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found. Never throws for an expected failure.</returns>
    Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken);
}
