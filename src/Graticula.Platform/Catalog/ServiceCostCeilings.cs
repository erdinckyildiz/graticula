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
/// <see cref="RecordCount"/> takes the smaller of what the service allows and what the
/// server allows, never the larger — the same rule as the capability intersection, for
/// the same reason: a per-service setting that could raise a server-wide ceiling would
/// make the server's figure advisory, and an operator who lowered it globally would not
/// have lowered it.
/// </para>
/// </remarks>
public sealed class ServiceCostCeilings
{
    /// <summary>Nothing configured.</summary>
    public static ServiceCostCeilings Unset { get; } = new(null, null, null, null, null);

    /// <summary>Creates a set of ceilings.</summary>
    /// <param name="maximumRecordCount">Most rows one response may carry, or null.</param>
    /// <param name="defaultRecordCount">
    /// Rows to return when the caller does not say, or null for the server's default.
    /// </param>
    /// <param name="maximumResponseBytes">Most bytes one response body may reach, or null.</param>
    /// <param name="maximumRequestBytes">Most bytes one request body may carry, or null.</param>
    /// <param name="maximumEditsPerTransaction">Most edits one applyEdits may carry, or null.</param>
    public ServiceCostCeilings(
        int? maximumRecordCount,
        int? defaultRecordCount,
        long? maximumResponseBytes,
        long? maximumRequestBytes,
        int? maximumEditsPerTransaction)
    {
        Positive(maximumRecordCount, nameof(maximumRecordCount));
        Positive(defaultRecordCount, nameof(defaultRecordCount));
        Positive(maximumResponseBytes, nameof(maximumResponseBytes));
        Positive(maximumRequestBytes, nameof(maximumRequestBytes));
        Positive(maximumEditsPerTransaction, nameof(maximumEditsPerTransaction));

        // <b>Refused rather than silently clamped.</b> A default page larger than the
        // service's own maximum is a configuration that contradicts itself, and an
        // operator who wrote it meant one of the two numbers — quietly picking one
        // would hide which. The database refuses it as well (migration 17), because
        // this constructor is not the only way a row can be written.
        if (defaultRecordCount is { } fallback && maximumRecordCount is { } ceiling
            && fallback > ceiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultRecordCount),
                fallback,
                $"A default record count of {fallback} is larger than this service's maximum of "
                + $"{ceiling}, so every request that did not ask for a page size would be "
                + "clamped by the maximum and the default would never apply. Set one of them.");
        }

        MaximumRecordCount = maximumRecordCount;
        DefaultRecordCount = defaultRecordCount;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumEditsPerTransaction = maximumEditsPerTransaction;
    }

    /// <summary>Most rows one response may carry, or null for the server's figure.</summary>
    public int? MaximumRecordCount { get; }

    /// <summary>Rows to return when the caller does not ask, or null.</summary>
    public int? DefaultRecordCount { get; }

    /// <summary>Most bytes one response body may reach, or null.</summary>
    public long? MaximumResponseBytes { get; }

    /// <summary>Most bytes one request body may carry, or null.</summary>
    public long? MaximumRequestBytes { get; }

    /// <summary>Most edits one <c>applyEdits</c> may carry, or null.</summary>
    public int? MaximumEditsPerTransaction { get; }

    /// <summary>True when nothing is configured.</summary>
    public bool IsUnset =>
        MaximumRecordCount is null && DefaultRecordCount is null
        && MaximumResponseBytes is null && MaximumRequestBytes is null
        && MaximumEditsPerTransaction is null;

    /// <summary>The row ceiling in force, given the server's own.</summary>
    /// <param name="serverCeiling">What the server permits at most.</param>
    /// <remarks>
    /// <b>The smaller of the two, always.</b> A service may ask for less and never for
    /// more — the rule that makes every ceiling in this type a narrowing.
    /// </remarks>
    public int RecordCount(int serverCeiling) =>
        MaximumRecordCount is { } mine ? Math.Min(mine, serverCeiling) : serverCeiling;

    /// <summary>The page size to use when the caller did not ask.</summary>
    /// <param name="serverDefault">What the server would use.</param>
    /// <param name="serverCeiling">What the server permits at most.</param>
    /// <remarks>
    /// Clamped by the ceiling in force, so a service default can never produce a page
    /// larger than the same service's maximum even if the two were written in
    /// different edits.
    /// </remarks>
    public int PageSize(int serverDefault, int serverCeiling) =>
        Math.Min(DefaultRecordCount ?? serverDefault, RecordCount(serverCeiling));

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
