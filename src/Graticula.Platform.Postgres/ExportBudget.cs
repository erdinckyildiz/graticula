namespace Graticula.Platform.Postgres;

/// <summary>
/// The one disk budget every export shares — tile packages (ADR-098) and feature exports (ADR-106 §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two stores, one number.</b> A tile package and a GeoPackage are written to the same disk, so what a start may
/// take is what is left after <em>both</em> kinds of live export, and the check and the insert of either store happen
/// under the one advisory lock below. Were each store to count only its own table, the budget would be twice what the
/// operator set, and a reader's large export could never be refused for a package that was already using the room.
/// </para>
/// <para>
/// <b>Kept here, and not written into either store</b>, so the two cannot drift: the lock's name and the sum are each
/// one string, read by both. The lock is still called <c>tile.export:budget</c> because migration 64's stores took it
/// under that name and a build that ran beside this one, during an upgrade, must take the same lock.
/// </para>
/// </remarks>
internal static class ExportBudget
{
    /// <summary>Takes the budget lock for the rest of the transaction; one for the whole server.</summary>
    internal const string LockSql = "select pg_advisory_xact_lock(hashtextextended('tile.export:budget', 0))";

    /// <summary>What every live export holds: its file once written, its estimate until then.</summary>
    /// <remarks>
    /// <b>Live</b> is not removed and not failed or cancelled — a failed or cancelled export's partial file is deleted by
    /// the worker or the sweep, so it holds nothing it should be charged for.
    /// </remarks>
    internal const string HeldSql = """
        select coalesce(sum(held), 0)::bigint
          from (
                select coalesce(e.bytes, e.estimated_bytes) as held
                  from tile_export e
                  join job j on j.id = e.job_id
                 where e.removed_at is null and j.status in ('queued', 'running', 'done')
                union all
                select coalesce(f.bytes, f.estimated_bytes) as held
                  from feature_export f
                  join job j on j.id = f.job_id
                 where f.removed_at is null and j.status in ('queued', 'running', 'done')
               ) live
        """;
}
