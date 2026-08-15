using System;
using System.Collections.Generic;
using System.Linq;
using GisServer.Features;

namespace GisServer.Api.ArcGis;

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
            addResults = Merge(outcome.Adds, parsed.RejectedAdds),
            updateResults = Merge(outcome.Updates, parsed.RejectedUpdates),
            deleteResults = Merge(outcome.Deletes, parsed.RejectedDeletes),

            // Not part of Esri's shape, and included anyway. A client that asked
            // for all-or-nothing and got a response full of successes alongside
            // one failure has no other way to learn that none of the successes
            // were kept — every result would say true.
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
            EditKind.Add => ("addResults", Merge(outcome.Adds, parsed.RejectedAdds)),
            EditKind.Update => ("updateResults", Merge(outcome.Updates, parsed.RejectedUpdates)),
            _ => ("deleteResults", Merge(outcome.Deletes, parsed.RejectedDeletes)),
        };

        return new Dictionary<string, object>
        {
            [name] = results,
            ["rolledBack"] = outcome.RolledBack,
        };
    }

    private static object[] Merge(
        IReadOnlyList<EditResult> applied, IReadOnlyList<ApplyEditsRequest.Rejected> rejected)
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

            results[at] = result.Succeeded
                ? Success(result.ObjectId)
                : Failure(result.ObjectId, result.Error ?? "The edit failed.");
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
