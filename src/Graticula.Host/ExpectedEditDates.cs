using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// Graticula's <c>expectedEditDates</c> on a layer's <c>applyEdits</c> — ADR-300 §5.5: the EditDate each updated
/// record had when its client downloaded it, so an update to a record changed since is refused rather than applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape.</b> A JSON object whose keys are the features' object ids — or, with <c>useGlobalIds=true</c>, their
/// GlobalIDs — and whose values are epoch milliseconds, or <c>null</c> for a record that had no EditDate. Only updates
/// are compared; a key naming no update in the request is ignored.
/// </para>
/// <para>
/// <b>What it answers.</b> The writer compares in the update's own <c>where</c> (<see cref="EditBatch.ExpectsEdited"/>).
/// A record that moved answers <c>412</c>, one that is gone — or that the view no longer shows — <c>404</c>, each with
/// its GlobalID where it is known (<c>ApplyEditsResponse.Build</c>'s <c>preconditions</c>). With <c>useGlobalIds=true</c>
/// a GlobalID nothing carries is a <c>404</c> entry instead of refusing the whole request, as it would without the
/// parameter, because a record deleted while a device was offline is exactly what the form client must be told about.
/// </para>
/// <para>
/// <b>A request without it is unchanged</b>: every failure 400, and an unknown GlobalID refuses the request.
/// </para>
/// </remarks>
internal static class ExpectedEditDates
{
    /// <summary>The request parameter.</summary>
    public const string Parameter = "expectedEditDates";

    /// <summary>Where the report for the response is kept on the request.</summary>
    public const string ItemKey = "graticula.expectedEditDates";

    /// <summary>
    /// What the response needs to say about the compared updates: each one's GlobalID, and which identities stood in for a
    /// GlobalID nothing carries.
    /// </summary>
    /// <param name="GlobalIdOf">GlobalIDs by the identity the writer saw.</param>
    /// <param name="Placeholders">Identities given to GlobalIDs no feature carries.</param>
    internal sealed record Report(IReadOnlyDictionary<long, Guid> GlobalIdOf, IReadOnlySet<long> Placeholders);

    /// <summary>Reads the parameter.</summary>
    /// <param name="text">Its value.</param>
    /// <param name="expected">The moments by key, as written.</param>
    /// <param name="error">Why it could not be read.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryParse(string text, out Dictionary<string, long?> expected, out string? error)
    {
        expected = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        error = null;
        const string Shape = "expectedEditDates is a JSON object of the EditDate each updated feature had when it was "
            + "downloaded, in epoch milliseconds, keyed by its object id: {\"12\": 1763300000000}.";

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Shape;
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    expected[property.Name] = null;
                }
                else if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long moment))
                {
                    expected[property.Name] = moment;
                }
                else
                {
                    error = $"expectedEditDates['{property.Name}'] is not a whole number of epoch milliseconds. " + Shape;
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            error = Shape;
            return false;
        }
    }

    /// <summary>
    /// The moments by the identity the writer will see, from keys that are object ids or — with GlobalIDs — GlobalIDs.
    /// </summary>
    /// <param name="expected">The parameter, read.</param>
    /// <param name="objectIds">With <c>useGlobalIds=true</c>, every referenced GlobalID's identity (placeholders too).</param>
    /// <param name="byIdentity">The moments by identity.</param>
    /// <param name="error">Why a key could not be read.</param>
    /// <returns>Whether every key was read.</returns>
    public static bool TryResolve(
        Dictionary<string, long?> expected,
        IReadOnlyDictionary<Guid, long>? objectIds,
        out Dictionary<long, long?> byIdentity,
        out string? error)
    {
        byIdentity = [];
        error = null;

        foreach ((string key, long? moment) in expected)
        {
            if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long objectId))
            {
                byIdentity[objectId] = moment;
            }
            else if (Guid.TryParse(key, out Guid global))
            {
                if (objectIds is null)
                {
                    error = $"expectedEditDates names '{key}', a GlobalID, and this request addresses features by object id. "
                        + "Key it by object id, or send useGlobalIds=true.";
                    return false;
                }

                if (objectIds.TryGetValue(global, out long identity))
                {
                    byIdentity[identity] = moment;
                }
            }
            else
            {
                error = $"expectedEditDates names '{key}', which is neither an object id nor a GlobalID.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The outcome as the response states it: each compared update's GlobalID, a placeholder's object id as -1, and the
    /// words the form client shows.
    /// </summary>
    /// <param name="outcome">What the writer did.</param>
    /// <param name="report">What the request resolved.</param>
    /// <returns>The outcome to answer with.</returns>
    public static EditOutcome Decorate(EditOutcome outcome, Report report)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(report);

        EditResult[] updates = [.. outcome.Updates.Select(result =>
        {
            if (result.Succeeded || !(result.VersionMoved || result.NoSuchFeature))
            {
                return result;
            }

            bool placeholder = report.Placeholders.Contains(result.Identity);
            Guid? global = report.GlobalIdOf.TryGetValue(result.Identity, out Guid g) ? g : result.GlobalId;
            string named = global is { } id ? GlobalIds.Braced(id) : result.Identity.ToString(CultureInfo.InvariantCulture);

            return result with
            {
                Identity = placeholder ? -1 : result.Identity,
                GlobalId = global,
                Error = result.VersionMoved
                    ? $"Feature {named} was changed on the server since it was downloaded, so this edit was not applied. "
                        + "Fetch it again and choose which version to keep."
                    : $"Feature {named} is not here: it was deleted, or it is no longer among the features this layer "
                        + "shows you. The edit was not applied.",
            };
        })];

        return outcome with { Updates = updates };
    }
}
