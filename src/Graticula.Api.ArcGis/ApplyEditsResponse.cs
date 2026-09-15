using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Features;

namespace Graticula.Api.ArcGis;

/// <summary>
/// Builds the ArcGIS <c>applyEdits</c> response.
/// </summary>
/// <remarks>
/// <para>
/// Three arrays, one entry per submitted feature, in the order submitted. That
/// ordering is the contract: a client matches results to its own features by
/// position, so a response that silently omits the ones that failed to parse
/// would shift every subsequent result onto the wrong feature.
/// </para>
/// <para>
/// <b>Which is why rejections are merged back in at their original index</b>
/// rather than appended. A feature the parser could not read never reached the
/// writer, but it still occupied a position in the request.
/// </para>
/// </remarks>
public static class ApplyEditsResponse
{
    /// <summary>Assembles the response.</summary>
    /// <param name="outcome">What the writer did.</param>
    /// <param name="parsed">What the parser rejected before the writer saw it.</param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object Build(EditOutcome outcome, ApplyEditsRequest.Parsed parsed)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(parsed);

        return new
        {
            addResults = Merge(outcome.Adds, parsed.RejectedAdds, outcome.RolledBack, adds: true),
            updateResults = Merge(outcome.Updates, parsed.RejectedUpdates, outcome.RolledBack, adds: false),
            deleteResults = Merge(outcome.Deletes, parsed.RejectedDeletes, outcome.RolledBack, adds: false),

            // Not part of Esri's shape, and included anyway: the per-feature results
            // already say nothing was kept, and this says why in one place.
            rolledBack = outcome.RolledBack,
        };
    }

    /// <summary>Which single operation a response is for.</summary>
    public enum EditKind
    {
        /// <summary><c>addFeatures</c>.</summary>
        Add,

        /// <summary><c>updateFeatures</c>.</summary>
        Update,

        /// <summary><c>deleteFeatures</c>.</summary>
        Delete,
    }

    /// <summary>
    /// The response for one of the single-operation endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One array, because that is the document the client is parsing.</b>
    /// ArcGIS answers <c>addFeatures</c> with <c>addResults</c> and nothing
    /// else; returning all three, two of them empty, is a different shape from
    /// the one an older client was written against.
    /// </para>
    /// <para>
    /// <b>Through the same merge as <see cref="Build"/>.</b> A feature the
    /// parser rejected never reached the writer but still occupied a position in
    /// the request, and dropping it here would shift every later result onto the
    /// wrong feature — the exact defect the merge exists to prevent, reintroduced
    /// by a second code path.
    /// </para>
    /// <para>
    /// <c>rolledBack</c> is carried on this shape too, for the same reason it is
    /// on the full one: a client that asked for all-or-nothing and reads a list
    /// of successes has no other way to learn none of them were kept.
    /// </para>
    /// </remarks>
    /// <param name="outcome">What the writer did.</param>
    /// <param name="parsed">What the parser rejected before the writer saw it.</param>
    /// <param name="kind">Which operation was asked for.</param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object One(EditOutcome outcome, ApplyEditsRequest.Parsed parsed, EditKind kind)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(parsed);

        (string name, object[] results) = kind switch
        {
            EditKind.Add => ("addResults", Merge(outcome.Adds, parsed.RejectedAdds, outcome.RolledBack, adds: true)),
            EditKind.Update => ("updateResults", Merge(outcome.Updates, parsed.RejectedUpdates, outcome.RolledBack, adds: false)),
            _ => ("deleteResults", Merge(outcome.Deletes, parsed.RejectedDeletes, outcome.RolledBack, adds: false)),
        };

        return new Dictionary<string, object>
        {
            [name] = results,
            ["rolledBack"] = outcome.RolledBack,
        };
    }

    /// <summary>
    /// What a feature that succeeded is told when the batch it was in was rolled back.
    /// </summary>
    internal const string RolledBackDescription =
        "Not applied: another edit in this request failed and rollbackOnFailure was set, so "
        + "nothing in the request was kept. Fix the edit that failed and send the request again.";

    /// <summary>Puts applied results and parser rejections back in submitted order.</summary>
    /// <remarks>
    /// <b>A rolled-back batch has no successes, whatever each edit did on its own.</b> Until
    /// 2026-09-15 an edit that ran cleanly inside a batch that was then rolled back answered
    /// <c>success: true</c> with an object id, and only the extra <c>rolledBack</c> flag said
    /// otherwise. No ArcGIS client reads that flag: the JS SDK and Runtime read
    /// <c>addResults[i].success</c>, so they recorded a feature that did not exist and then tried
    /// to attach to it or update it. Found against the showcase with one good add and one out of
    /// its domain — the good one came back as object id 4, and there was no feature 4. Each such
    /// edit is now a failure that says it was not kept and why; an add carries <c>-1</c>, since the
    /// id it was given was rolled back with it, and an update or delete keeps the id it named.
    /// </remarks>
    private static object[] Merge(
        IReadOnlyList<EditResult> applied,
        IReadOnlyList<ApplyEditsRequest.Rejected> rejected,
        bool rolledBack,
        bool adds)
    {
        object[] results = new object[applied.Count + rejected.Count];

        foreach (ApplyEditsRequest.Rejected reject in rejected)
        {
            if (reject.Index >= 0 && reject.Index < results.Length)
            {
                results[reject.Index] = Failure(reject.ObjectId, reject.Error);
            }
        }

        int at = 0;

        foreach (EditResult result in applied)
        {
            // Fill the gaps the rejections left, in order. The two lists
            // together reconstruct the submitted order exactly.
            while (at < results.Length && results[at] is not null)
            {
                at++;
            }

            if (at >= results.Length)
            {
                break;
            }

            results[at] = !result.Succeeded
                ? Failure(result.Identity, result.Error ?? "The edit failed.")
                : rolledBack
                    ? Failure(adds ? -1 : result.Identity, RolledBackDescription)
                    : Success(result.Identity);
        }

        for (int i = 0; i < results.Length; i++)
        {
            results[i] ??= Failure(-1, "No result was produced for this feature.");
        }

        return results;
    }

    private static object Success(long objectId) => new
    {
        objectId,
        globalId = (string?)null,
        success = true,
    };

    /// <summary>A failed edit, in the shape ArcGIS clients read.</summary>
    /// <remarks>
    /// The error code is 400 for everything. ArcGIS uses a numeric code that
    /// clients occasionally branch on, and inventing a taxonomy we do not have
    /// would be worse than one honest code with a description that says what is
    /// actually wrong.
    /// </remarks>
    private static object Failure(long objectId, string description) => new
    {
        objectId,
        globalId = (string?)null,
        success = false,
        error = new { code = 400, description },
    };
}
