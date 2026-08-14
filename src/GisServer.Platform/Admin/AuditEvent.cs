using System;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Platform.Admin;

/// <summary>One mutating administrative action.</summary>
/// <param name="Principal">Who did it.</param>
/// <param name="PrincipalName">Their name at the time, denormalised on purpose.</param>
/// <param name="SourceAddress">Where from, or null if unknown.</param>
/// <param name="Action">What they did, as a stable verb — <c>datasource.register</c>.</param>
/// <param name="Resource">What they did it to, or null before it existed.</param>
/// <param name="Detail">
/// What changed, as JSON. <b>Never a credential</b> — see <see cref="IAuditLog"/>.
/// </param>
/// <param name="Succeeded">Whether it worked.</param>
public readonly record struct AuditEvent(
    Guid Principal,
    string PrincipalName,
    string? SourceAddress,
    string Action,
    string? Resource,
    string Detail,
    bool Succeeded);

/// <summary>
/// Records mutating administrative actions.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §5d: principal, source address, resource, before and after. Without
/// it, the ownership model in <c>security.md</c> §2.0 has no way to answer
/// <em>who shared this publicly</em>.
/// </para>
/// <para>
/// <b>The principal name is stored alongside the id, and that duplication is
/// deliberate.</b> An audit trail read a year later is read by a person, and a
/// row naming a principal that has since been deleted should still say who it
/// was. Joining to <c>principal</c> would turn a deletion into the silent
/// erasure of the record of what that account did.
/// </para>
/// <para>
/// <b>Failures are recorded too.</b> A register of successful actions answers
/// <em>who did this</em> and cannot answer <em>who tried</em>, and the second
/// question is the one asked during an incident.
/// </para>
/// <para>
/// <b>Nothing in <c>Detail</c> may be a secret.</b> Registering a data source
/// carries a connection string with a password in it; the audit row records the
/// host and database and never the credential. An audit log that leaks what it
/// audits is a new place to steal from.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>Records an event.</summary>
    Task RecordAsync(AuditEvent entry, CancellationToken cancellationToken);
}
