using System;

namespace Graticula.Platform.Jobs;

/// <summary>What happens if a job's work runs a second time.</summary>
/// <remarks>
/// <para>
/// <b>[ADR-011](../../../docs/adr/ADR-011-job-system.md) condition 2</b>: *every job type
/// declares its re-run behaviour before it is registered. There is no default, because a wrong
/// default here corrupts data.*
/// </para>
/// <para>
/// <b>This is about the work, not about the record.</b> <see cref="JobStatus"/> has no
/// <c>retrying</c> and a failed job stays failed — asking again is a new job with a new row.
/// That settles what the *register* says. What it does not settle is what happens to the
/// *data* when the same work is done twice, which is the question a crash between "the write
/// landed" and "the row said Done" actually asks.
/// </para>
/// </remarks>
public enum JobRerun
{
    /// <summary>
    /// Running it again produces the same result and changes nothing.
    /// </summary>
    /// <remarks>
    /// Safe to retry after a crash, safe to run twice by accident, and safe for an operator to
    /// press twice.
    /// </remarks>
    Harmless,

    /// <summary>
    /// Running it again is refused by the store, so a duplicate cannot be created.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as harmless, and the difference is what an operator sees.</b> The work
    /// is not idempotent; what makes a second run safe is a constraint that stops it, so the
    /// second attempt **fails** rather than quietly doing nothing. Anything in this state needs
    /// its refusal to be a sentence rather than a constraint violation.
    /// </remarks>
    RefusedByTheStore,

    /// <summary>
    /// It keeps durable checkpoints, and a second run goes on from the last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-011] §3.4's <c>RESUMABLE</c>, which that section named and this enumeration did not
    /// have until a kind needed it.</b> A tile seed writes tiles to a cache, and writing a tile twice
    /// writes the same bytes to the same key; what a second run must not do is start again at the
    /// bottom of the pyramid, and its checkpoints are what stop it.
    /// </para>
    /// <para>
    /// <b>Not <see cref="Harmless"/>, because what a lost lease does to it differs.</b> A harmless
    /// kind is queued again once and a second loss fails it, since a job that kills its process
    /// would otherwise be claimed and lost for as long as somebody keeps restarting the server. A
    /// resumable kind is queued again every time and keeps its progress: a seed of a quarter of a
    /// million tiles that is failed by the second restart of the week has to be asked for again and
    /// resumes nothing. The cost is named in ADR-093 §5.4 — a seed whose own work kills the process
    /// comes back after each restart until somebody cancels it.
    /// </para>
    /// </remarks>
    Resumable,

    /// <summary>
    /// Running it again would duplicate or corrupt, and nothing stops it.
    /// </summary>
    /// <remarks>
    /// <b>No job kind is allowed to be this.</b> It exists so that the answer can be written
    /// down when it is the true one, and so that a kind added without thinking cannot borrow a
    /// safer word by accident. A kind that would be this needs a constraint or a design change
    /// before it is registered — which is what the condition means by *before*.
    /// </remarks>
    Unsafe,
}

/// <summary>Facts about a job kind that the schema does not carry.</summary>
public static class JobKinds
{
    /// <summary>
    /// What re-running this kind's work would do.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its re-run behaviour.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The kind has no declared behaviour, which is the condition being enforced rather than a
    /// bug — see the remarks.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>There is no default arm, and the throw is not one.</b> A <c>_ =&gt;</c> returning
    /// <see cref="JobRerun.Harmless"/> would be exactly the wrong default the condition names:
    /// a kind added without a decision would inherit the safest word and nobody would find out
    /// until data was duplicated. Throwing means a kind added without a decision fails
    /// immediately and loudly, and <c>JobRerunTests</c> makes it fail at build time instead, by
    /// walking every value of the enumeration.
    /// </para>
    /// </remarks>
    public static JobRerun RerunOf(JobKind kind) => kind switch
    {
        // <b>Reads headers and writes nothing.</b> The answer goes into the job's own detail;
        // no table is touched. Two inspections of one archive produce two identical answers.
        JobKind.GeodatabaseInspect => JobRerun.Harmless,

        // <b>Creates a layer over a table it also creates.</b> Running it twice cannot publish
        // two copies: `layer_name_unique_in_service` refuses the same name in the same service,
        // so the second attempt is refused by the store rather than duplicating the layer.
        //
        // <b>This also named `layer_table_unique` until 2026-09-11, and that was never the
        // reason.</b> Migration 40 scoped it to one service, and it could not have refused a
        // rerun even before: every import creates its table under a new random suffix. So what
        // a second run leaves behind is a table — created, filled, then refused its layer —
        // which is why a reclaimed import is failed rather than run again (D-243).
        //
        // <b>Which is why it is not `Harmless`.</b> The distinction is what an operator sees: a
        // second inspection succeeds and a second import fails, and a register that called both
        // safe would have them expect the same thing from two different answers.
        JobKind.GeodatabaseImport => JobRerun.RefusedByTheStore,

        // <b>Writes tiles, each to its own key, and remembers how far it got.</b> A tile written
        // twice is the same bytes under the same key — `TileSingleFlight` already lets a request and
        // a seed race for one without harm — and the per-level cursor in `tile_seed_level` is the
        // checkpoint a second run goes on from. ADR-093 §5.4.
        JobKind.TileSeed => JobRerun.Resumable,

        // <b>Writes one file of its own, from the start, every time — ADR-098 §5.4.</b> A second run walks
        // the same tiles (the cache already holds most of them, so it is quick) and writes the same package
        // over its own half-written one, under the export's own name; nothing outside the export's file is
        // written except cached tiles, which are the seed's harmless case. So it is `Harmless` and not
        // `Resumable`: the package is not appended to across runs, because a half-written zip or archive
        // has no durable cursor to go on from, and a lost lease gives it one more try and then fails it,
        // which is what an export that kills its process should get.
        JobKind.TileExport => JobRerun.Harmless,

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "This job kind has not declared what re-running it would do. ADR-011 condition 2: "
            + "every job type declares its re-run behaviour before it is registered, and there "
            + "is no default because a wrong default here corrupts data. Add it to RerunOf."),
    };
}
