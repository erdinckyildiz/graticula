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

        // <b>Creates a layer over a table it also creates.</b> Running it twice cannot produce
        // two copies: `layer_table_unique` covers (data source, schema, table, geometry column)
        // and `layer_name_unique_in_service` covers the name within its service, so the second
        // attempt is refused by the store rather than duplicating the data.
        //
        // <b>Which is why it is not `Harmless`.</b> The distinction is what an operator sees: a
        // second inspection succeeds and a second import fails, and a register that called both
        // safe would have them expect the same thing from two different answers.
        JobKind.GeodatabaseImport => JobRerun.RefusedByTheStore,

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "This job kind has not declared what re-running it would do. ADR-011 condition 2: "
            + "every job type declares its re-run behaviour before it is registered, and there "
            + "is no default because a wrong default here corrupts data. Add it to RerunOf."),
    };
}
