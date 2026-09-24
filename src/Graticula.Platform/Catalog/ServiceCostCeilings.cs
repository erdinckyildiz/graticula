using System;

namespace Graticula.Platform.Catalog;

/// <summary>
/// What one request may cost a service: rows, bytes in, bytes out, edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-113, and the axis is cost rather than capability.</b>
/// <see cref="ServiceCapabilityLimits"/> answers *may you*; this answers *how much*.
/// Turning <c>Update</c> off refuses an act; a max record count shortens the answer to
/// an act that is permitted. Keeping them apart is what lets an operator read a screen
/// and tell "this service is read-only" from "this service answers 500 rows at a time".
/// </para>
/// <para>
/// <b>Null is unset everywhere and means *the server's own figure*</b>, which is what
/// every service did before these columns existed. That is the whole compatibility
/// story, in the same shape migration 16 used.
/// </para>
/// <para>
/// <b>Every ceiling here narrows and none of them widens.</b>
/// <see cref="PageSize"/> never exceeds what the server allows at most — the same rule as the
/// capability intersection, for the same reason: a per-service setting that could raise a
/// server-wide ceiling would make the server's figure advisory, and an operator who lowered it
/// globally would not have lowered it. <b>The server's page size is not a ceiling</b> (ADR-084):
/// it is what a service that says nothing answers, and a service may set a larger one up to the
/// ceiling.
/// </para>
/// </remarks>
public sealed class ServiceCostCeilings
{
    /// <summary>Nothing configured.</summary>
    public static ServiceCostCeilings Unset { get; } = new(null, null, null, null);

    /// <summary>Creates a set of ceilings.</summary>
    /// <param name="maximumRecordCount">
    /// The service's page size — what a query that names none answers, and the most one answers — or
    /// null for the server's.
    /// </param>
    /// <param name="maximumResponseBytes">Most bytes one response body may reach, or null.</param>
    /// <param name="maximumRequestBytes">Most bytes one request body may carry, or null.</param>
    /// <param name="maximumEditsPerTransaction">Most edits one applyEdits may carry, or null.</param>
    /// <param name="requestDeadline">
    /// How long a client may occupy this service, or null for the server's own bound. Owner
    /// requirement 2026-08-18: every service needs one, not only the geometry service.
    /// </param>
    public ServiceCostCeilings(
        int? maximumRecordCount,
        long? maximumResponseBytes,
        long? maximumRequestBytes,
        int? maximumEditsPerTransaction,
        // <b>Defaulted, because *not configured* is the ordinary case and the honest value.</b>
        // Every service that exists today leaves this unset, and a required parameter would make
        // fourteen call sites pass `null` to say what null already says.
        TimeSpan? requestDeadline = null)
    {
        Positive(maximumRecordCount, nameof(maximumRecordCount));
        Positive(maximumResponseBytes, nameof(maximumResponseBytes));
        Positive(maximumRequestBytes, nameof(maximumRequestBytes));
        Positive(maximumEditsPerTransaction, nameof(maximumEditsPerTransaction));

        // <b>One number since 2026-09-23 (V-70, ADR-084).</b> There was a default page beside the
        // maximum, and a constructor check that the one not exceed the other; the owner chose
        // ArcGIS's single `maxRecordCount` instead, because a document saying one number over a
        // query answering another is how a script paging by the document skipped rows.
        MaximumRecordCount = maximumRecordCount;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumEditsPerTransaction = maximumEditsPerTransaction;

        // <b>A deadline of nought or less is refused rather than read as *no bound*.</b> A service
        // that wants the server's bound leaves this null; a column where 0 and null mean different
        // things is a column somebody reads wrong. Migration 24's check says the same in the schema.
        if (requestDeadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestDeadline),
                deadline,
                "A request deadline must be positive. Leave it null to use the server's own bound.");
        }

        RequestDeadline = requestDeadline;
    }

    /// <summary>
    /// The service's page size — ArcGIS's <c>maxRecordCount</c> — or null for the server's.
    /// </summary>
    public int? MaximumRecordCount { get; }

    /// <summary>Most bytes one response body may reach, or null.</summary>
    public long? MaximumResponseBytes { get; }

    /// <summary>Most bytes one request body may carry, or null.</summary>
    public long? MaximumRequestBytes { get; }

    /// <summary>Most edits one <c>applyEdits</c> may carry, or null.</summary>
    public int? MaximumEditsPerTransaction { get; }

    /// <summary>
    /// How long a client may occupy this service, or null for the server's own bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner requirement 2026-08-18: every service, not only the geometry service.</b> This is
    /// the reference's *maximum time a client can use a service* — the whole request, from the
    /// first byte read to the last byte written, rather than the database statement that ADR-007
    /// §4.8's fixed <c>statement_timeout</c> already bounds. Projecting geometry, encoding a
    /// response and writing a hundred thousand features all happen after the statement returns,
    /// and none of it was bounded before.
    /// </para>
    /// <para>
    /// <b>A cost ceiling rather than a capability, and that is why it lives here.</b> This class's
    /// own remarks draw the line: a capability answers *may you*, a cost ceiling answers *how
    /// much*. Time is how much.
    /// </para>
    /// <para>
    /// <b>It may only lower.</b> <c>RequestDeadline.LowerTo</c> takes the smaller of this and the
    /// server's, so a service cannot claim more time than the deployment allows — the same rule
    /// ADR-031 §4 states for the statement timeout, for the same reason.
    /// </para>
    /// </remarks>
    public TimeSpan? RequestDeadline { get; }

    /// <summary>True when nothing is configured.</summary>
    public bool IsUnset =>
        MaximumRecordCount is null
        && MaximumResponseBytes is null && MaximumRequestBytes is null
        && MaximumEditsPerTransaction is null && RequestDeadline is null;

    /// <summary>
    /// The page size in force: what a query naming none answers, the most any query answers, and the
    /// number the document gives as <c>maxRecordCount</c>.
    /// </summary>
    /// <param name="serverPageSize">The server's page size, for a service that set none.</param>
    /// <param name="serverCeiling">What the server permits at most, which nothing exceeds.</param>
    /// <returns>The service's own if it set one, else the server's, never above the ceiling.</returns>
    public int PageSize(int serverPageSize, int serverCeiling)
    {
        int ceiling = Math.Max(1, serverCeiling);

        return Math.Clamp(MaximumRecordCount ?? serverPageSize, 1, ceiling);
    }

    /// <summary>The response-body ceiling in force, given the server's own.</summary>
    /// <param name="serverCeiling">The server's ceiling, or 0 for none.</param>
    /// <remarks>
    /// <b>Zero means *no ceiling*, which makes "smaller" the wrong word for it</b> — so
    /// a service ceiling applies whenever the server has none, and otherwise the two are
    /// compared. Taking a naive minimum would let a disabled server ceiling of 0 disable
    /// the service's as well.
    /// </remarks>
    public long ResponseBytes(long serverCeiling) => MaximumResponseBytes switch
    {
        null => serverCeiling,
        { } mine when serverCeiling <= 0 => mine,
        { } mine => Math.Min(mine, serverCeiling),
    };

    private static void Positive(long? value, string name)
    {
        if (value is { } given && given <= 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                given,
                "A cost ceiling must be positive. Zero would mean a service that answers "
                + "nothing, which is what an empty capability set already says (ADR-031 §2a) "
                + "and says more clearly.");
        }
    }
}
