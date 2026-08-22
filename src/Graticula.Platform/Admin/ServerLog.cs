using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Admin;

/// <summary>One request, as it is recorded.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, without its query.</param>
/// <param name="Query">
/// The query string, <b>already redacted</b>. See <see cref="IRequestLog"/>.
/// </param>
/// <param name="Status">The status code answered.</param>
/// <param name="DurationMs">How long it took, in milliseconds.</param>
/// <param name="PrincipalName">Who asked, or null for an anonymous caller.</param>
/// <param name="SourceAddress">Where from.</param>
/// <param name="Face">Which protocol surface it reached — ArcGIS, WMS, WFS, OGC, studio.</param>
/// <param name="Service">The service it named, when it named one.</param>
/// <param name="Bytes">Response length, when it is known.</param>
public readonly record struct RequestEntry(
    string Method,
    string Path,
    string? Query,
    int Status,
    int DurationMs,
    string? PrincipalName,
    string? SourceAddress,
    string? Face,
    string? Service,
    long? Bytes);

/// <summary>Something the studio reported from a browser.</summary>
/// <param name="Kind">What sort of event — an error, a rejection, a failed layer.</param>
/// <param name="Page">The page it happened on.</param>
/// <param name="Message">What it said.</param>
/// <param name="Detail">Anything structured, as JSON.</param>
/// <param name="PrincipalName">Who was signed in, if anyone.</param>
/// <param name="SourceAddress">Where from.</param>
/// <param name="Agent">The user agent string.</param>
public readonly record struct ClientEntry(
    string Kind,
    string? Page,
    string Message,
    string Detail,
    string? PrincipalName,
    string? SourceAddress,
    string? Agent);

/// <summary>What to select from a log.</summary>
/// <param name="From">Earliest occurrence to return, inclusive.</param>
/// <param name="To">Latest occurrence to return, exclusive.</param>
/// <param name="Text">Free text, matched against the columns a reader would search.</param>
/// <param name="Principal">Exact principal name.</param>
/// <param name="Action">Exact action, for the audit trail.</param>
/// <param name="Status">Exact status code, for the request log.</param>
/// <param name="Kind">Exact kind, for studio events.</param>
/// <param name="Failed">When true, only unsuccessful entries.</param>
/// <param name="Before">
/// Keyspace cursor: return only entries older than this. Paging by cursor rather than by
/// offset, because a log grows at the head while it is being read and an offset walks
/// backwards through a list that is moving forwards.
/// </param>
/// <param name="Limit">How many rows at most.</param>
public readonly record struct LogQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Text,
    string? Principal,
    string? Action,
    int? Status,
    string? Kind,
    bool Failed,
    long? Before,
    int Limit);

/// <summary>One row of any of the three logs, in the shape a screen reads.</summary>
/// <param name="Cursor">Its position, for paging.</param>
/// <param name="OccurredAt">When.</param>
/// <param name="PrincipalName">Who, or null for anonymous.</param>
/// <param name="SourceAddress">Where from.</param>
/// <param name="What">The action, the method and path, or the kind and message.</param>
/// <param name="Resource">The service, layer or page it concerned.</param>
/// <param name="Succeeded">Whether it worked.</param>
/// <param name="Detail">Everything else, as JSON, for a reader who opens the row.</param>
public readonly record struct LogRow(
    long Cursor,
    DateTimeOffset OccurredAt,
    string? PrincipalName,
    string? SourceAddress,
    string What,
    string? Resource,
    bool Succeeded,
    string Detail);

/// <summary>
/// Records one row per request, without the request waiting for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract is that <see cref="Record"/> returns immediately and may lose the
/// entry.</b> It is not a <c>Task</c>, on purpose: a method that returns something
/// awaitable invites a caller to await it, and awaiting this on the request path is the
/// one thing
/// [ADR-045](../../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// condition 1 forbids. A synchronous void that enqueues cannot be misused that way.
/// </para>
/// <para>
/// <b>Lossy by design, and the loss is counted.</b> The queue is bounded; when it is full
/// the entry is dropped and <see cref="Dropped"/> goes up. A request that got slower
/// because of logging is worse than a log line that went missing, and silence about the
/// drop is worse than both — which is why the count exists and is shown on the screen.
/// <b>So this log is not evidence of absence.</b>
/// </para>
/// <para>
/// <b><see cref="RequestEntry.Query"/> arrives redacted, and this interface will not do it
/// for you.</b> Esri clients send a session token as <c>?token=</c>
/// ([D-120](../../../docs/architecture-debt.md)), so an unredacted query string is a
/// credential. Redacting here would look safer and be worse: the caller holds the raw
/// value either way, and a port that quietly launders its input teaches that the raw value
/// is safe to pass around.
/// </para>
/// </remarks>
public interface IRequestLog
{
    /// <summary>How many entries have been dropped because the queue was full.</summary>
    long Dropped { get; }

    /// <summary>Queues an entry, or drops it. Never blocks, never throws.</summary>
    /// <param name="entry">The request.</param>
    void Record(RequestEntry entry);
}

/// <summary>
/// Records what a browser reports, from an endpoint a stranger can reach.
/// </summary>
/// <remarks>
/// <b>Every string here is untrusted input.</b> The studio is usable without signing in,
/// so the route that feeds this is anonymous, which makes it the one write path in this
/// server a passer-by can reach. Size caps, rate limits and escaping on the way out are
/// not defence in depth here — they are the defence.
/// </remarks>
public interface IClientEventLog
{
    /// <summary>Records an event.</summary>
    /// <param name="entry">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RecordAsync(ClientEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the three logs, and only reads them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One port for three tables, because the screen asks them the same questions.</b> When,
/// who, from where, and did it work — every log answers those, and a reader moving between
/// sources should not have to learn three filter sets. What differs is one field each:
/// an action for the audit trail, a status for requests, a kind for studio events, and
/// <see cref="LogQuery"/> carries all three so the shared shape does not become a lowest
/// common denominator.
/// </para>
/// <para>
/// <b>Reading is separated from writing, and that is not ceremony.</b> The write side of
/// the request log must never block; the read side runs arbitrary filters and may be slow.
/// One interface holding both would put a method that must be fast beside a method that
/// cannot be, and the next person to add a call site would have no way to tell which is
/// which.
/// </para>
/// </remarks>
public interface ILogReader
{
    /// <summary>Reads the administrative audit trail.</summary>
    /// <param name="query">What to select.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<LogRow>> AuditAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>Reads the request log.</summary>
    /// <param name="query">What to select.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<LogRow>> RequestsAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>Reads what the studio reported.</summary>
    /// <param name="query">What to select.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<LogRow>> ClientAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>The distinct actions the audit trail holds, for a filter to offer.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each action and how many times it appears.</returns>
    Task<IReadOnlyList<(string Action, long Count)>> ActionsAsync(
        CancellationToken cancellationToken);

    /// <summary>Deletes entries older than the retention window.</summary>
    /// <param name="keep">How long to keep.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many rows went.</returns>
    /// <remarks>
    /// <b>Only the two new logs are swept.</b> The audit trail is not: *who deleted that
    /// service last quarter* is the question it exists for, and a retention window that
    /// forgets it would make the trail decorative. The request and studio logs are
    /// operational and grow at request rate, which is the whole reason a cap is needed at
    /// all.
    /// </remarks>
    Task<long> SweepAsync(TimeSpan keep, CancellationToken cancellationToken);
}
