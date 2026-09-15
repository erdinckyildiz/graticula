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
    /// <param name="editMoment">
    /// When the edits were kept, for a caller that asked with <c>returnEditMoment=true</c>; null
    /// leaves it out, and so does a batch that was rolled back, which kept nothing to date.
    /// </param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object Build(EditOutcome outcome, ApplyEditsRequest.Parsed parsed, DateTimeOffset? editMoment = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(parsed);

        Dictionary<string, object> response = new()
        {
            ["addResults"] = Merge(outcome.Adds, parsed.RejectedAdds, outcome.RolledBack, adds: true),
            ["updateResults"] = Merge(outcome.Updates, parsed.RejectedUpdates, outcome.RolledBack, adds: false),
            ["deleteResults"] = Merge(outcome.Deletes, parsed.RejectedDeletes, outcome.RolledBack, adds: false),
        };

        Moment(response, outcome, editMoment);

        // Not part of Esri's shape, and included anyway: the per-feature results
        // already say nothing was kept, and this says why in one place.
        response["rolledBack"] = outcome.RolledBack;
        return response;
    }

    /// <summary>
    /// Adds <c>editMoment</c>, in epoch milliseconds, when it was asked for and something was kept.
    /// </summary>
    /// <remarks>
    /// <b>Written 2026-09-15.</b> <c>returnEditMoment=true</c> was accepted and nothing came back,
    /// so a client that reads changes by edit time, which is what the moment is for, had nothing to
    /// start its next read from and no error to say why.
    /// </remarks>
    private static void Moment(Dictionary<string, object> response, EditOutcome outcome, DateTimeOffset? editMoment)
    {
        if (editMoment is { } moment && !outcome.RolledBack)
        {
            response["editMoment"] = moment.ToUnixTimeMilliseconds();
        }
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
    /// <param name="editMoment">As for <see cref="Build"/>.</param>
    /// <returns>An object ready for JSON serialisation.</returns>
    public static object One(
        EditOutcome outcome, ApplyEditsRequest.Parsed parsed, EditKind kind, DateTimeOffset? editMoment = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(parsed);

        (string name, object[] results) = kind switch
        {
            EditKind.Add => ("addResults", Merge(outcome.Adds, parsed.RejectedAdds, outcome.RolledBack, adds: true)),
            EditKind.Update => ("updateResults", Merge(outcome.Updates, parsed.RejectedUpdates, outcome.RolledBack, adds: false)),
            _ => ("deleteResults", Merge(outcome.Deletes, parsed.RejectedDeletes, outcome.RolledBack, adds: false)),
        };

        Dictionary<string, object> response = new() { [name] = results };
        Moment(response, outcome, editMoment);
        response["rolledBack"] = outcome.RolledBack;
        return response;
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
                    : Success(result.Identity, result.GlobalId, result.GeometryRepaired);
        }

        for (int i = 0; i < results.Length; i++)
        {
            results[i] ??= Failure(-1, "No result was produced for this feature.");
        }

        return results;
    }

    private static object Success(long objectId, Guid? globalId = null, bool geometryRepaired = false) =>
        geometryRepaired
            ? new
            {
                objectId,
                globalId = globalId is { } value ? GlobalIds.Braced(value) : null,
                success = true,

                // <b>Not in ArcGIS's result, and added only when true (Q-153).</b> The polygon sent was
                // not valid and was stored as the valid polygon made of it; a client that ignores the field
                // loses nothing, and one that reads it knows to fetch the shape it now has.
                geometryRepaired = true,
            }
            : new
            {
                objectId,
                globalId = globalId is { } value2 ? GlobalIds.Braced(value2) : null,
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
