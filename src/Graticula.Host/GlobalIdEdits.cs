using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Graticula.Host;

/// <summary>
/// <c>applyEdits</c> with <c>useGlobalIds=true</c>: updates and deletes addressed by GlobalID, turned into
/// the object-id edit the writer applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>A translation at the door, not a second edit path.</b> An ArcGIS client that works offline or keeps
/// related records mints GlobalIDs and refers to features by them; ArcGIS's <c>useGlobalIds=true</c>
/// says so. The writer addresses rows by object id, which every check on the edit path — ownership,
/// versions, domains, the transaction — already speaks, so the GlobalIDs are resolved to object ids
/// once and the edit continues as the one those checks were written for.
/// </para>
/// <para>
/// <b>A GlobalID that names no feature refuses the whole request</b>, and names itself. An object id
/// cannot be invented for it, and applying the rest would be a different edit from the one the client
/// sent — the rule <c>rollbackOnFailure</c> exists to keep.
/// </para>
/// </remarks>
internal static class GlobalIdEdits
{
    /// <summary>Every GlobalID the updates and deletes refer to.</summary>
    /// <param name="updates">The <c>updates</c> value, or null.</param>
    /// <param name="deletes">The <c>deletes</c> value — a JSON array of GlobalIDs or a comma-separated list — or null.</param>
    /// <param name="globalIdField">The layer's GlobalID column.</param>
    /// <param name="ids">The GlobalIDs, when this returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether every reference could be read.</returns>
    public static bool TryCollect(
        string? updates, string? deletes, string globalIdField, out HashSet<Guid> ids, out string? error)
    {
        ids = [];
        error = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(updates))
            {
                foreach (JsonElement feature in JsonDocument.Parse(updates).RootElement.EnumerateArray())
                {
                    if (!feature.TryGetProperty("attributes", out JsonElement attributes)
                        || !TryAttribute(attributes, globalIdField, out JsonElement value)
                        || value.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(value.GetString(), out Guid id))
                    {
                        error = $"useGlobalIds=true: every update needs its '{globalIdField}' attribute, the GlobalID of the feature it changes.";
                        return false;
                    }

                    ids.Add(id);
                }
            }

            foreach (string text in DeleteTexts(deletes))
            {
                if (!Guid.TryParse(text, out Guid id))
                {
                    error = $"useGlobalIds=true: deletes are GlobalIDs, and '{text}' is not one.";
                    return false;
                }

                ids.Add(id);
            }

            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            error = "useGlobalIds=true: 'updates' must be a JSON array of features and 'deletes' an array or list of GlobalIDs.";
            return false;
        }
    }

    /// <summary>The same updates and deletes, addressed by object id.</summary>
    /// <param name="updates">The <c>updates</c> value, or null.</param>
    /// <param name="deletes">The <c>deletes</c> value, or null.</param>
    /// <param name="globalIdField">The layer's GlobalID column.</param>
    /// <param name="objectIdField">The layer's object id column.</param>
    /// <param name="objectIds">Each GlobalID's object id, from the table.</param>
    /// <param name="missing">The GlobalIDs no feature carries; nothing is rewritten when there are any.</param>
    /// <returns>The rewritten updates and deletes.</returns>
    public static (string? Updates, string? Deletes) Rewrite(
        string? updates,
        string? deletes,
        string globalIdField,
        string objectIdField,
        IReadOnlyDictionary<Guid, long> objectIds,
        out List<Guid> missing)
    {
        missing = [];
        string? rewrittenUpdates = null;
        string? rewrittenDeletes = null;

        if (!string.IsNullOrWhiteSpace(updates))
        {
            JsonArray features = JsonNode.Parse(updates)!.AsArray();

            foreach (JsonNode? feature in features)
            {
                JsonObject attributes = feature!["attributes"]!.AsObject();
                string key = attributes.Select(a => a.Key).First(k => string.Equals(k, globalIdField, StringComparison.OrdinalIgnoreCase));
                Guid id = Guid.Parse(attributes[key]!.GetValue<string>());

                if (!objectIds.TryGetValue(id, out long objectId))
                {
                    missing.Add(id);
                    continue;
                }

                string? existing = attributes.Select(a => a.Key).FirstOrDefault(k => string.Equals(k, objectIdField, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    attributes.Remove(existing);
                }

                attributes[objectIdField] = objectId;
            }

            rewrittenUpdates = features.ToJsonString();
        }

        List<long> deleted = [];

        foreach (string text in DeleteTexts(deletes))
        {
            Guid id = Guid.Parse(text);

            if (objectIds.TryGetValue(id, out long objectId))
            {
                deleted.Add(objectId);
            }
            else
            {
                missing.Add(id);
            }
        }

        if (deletes is not null)
        {
            rewrittenDeletes = string.Join(",", deleted.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        }

        return (rewrittenUpdates, rewrittenDeletes);
    }

    /// <summary>The refusal for GlobalIDs no feature carries.</summary>
    /// <param name="missing">Those GlobalIDs.</param>
    /// <returns>The sentence.</returns>
    public static string Unknown(IReadOnlyList<Guid> missing) =>
        $"useGlobalIds=true: no feature has the GlobalID{(missing.Count == 1 ? string.Empty : "s")} "
        + string.Join(", ", missing.Take(10).Select(Graticula.Features.GlobalIds.Braced))
        + (missing.Count > 10 ? $" and {missing.Count - 10} more" : string.Empty)
        + ". Nothing was applied.";

    private static bool TryAttribute(JsonElement attributes, string name, out JsonElement value)
    {
        foreach (JsonProperty property in attributes.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string[] DeleteTexts(string? deletes)
    {
        if (string.IsNullOrWhiteSpace(deletes))
        {
            return [];
        }

        string trimmed = deletes.Trim();

        if (trimmed.StartsWith('['))
        {
            return [.. JsonDocument.Parse(trimmed).RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
