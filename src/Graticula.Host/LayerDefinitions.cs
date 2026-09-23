using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// A MapServer <c>layerDefs</c>: a <c>where</c> clause per layer, evaluated through the same parser
/// the FeatureServer query uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refused from 2026-08-20 to 2026-09-15, and dropped before that.</b>
/// [D-125](../../docs/architecture-debt.md): export and identify took the parameter and ignored it,
/// so a caller filtering the map was shown every feature. Refusing was the honest repair while
/// nothing evaluated it. A review from an ArcGIS user's side then pointed out what the refusal cost:
/// <c>layerDefs</c> has been the dynamic map service's filter since 9.3, and every client that
/// filters a MapImageLayer sends it. It is now evaluated, and a definition that does not parse is
/// still refused — so the rule D-125 enforced, <i>never a wider answer than asked</i>, stands.
/// </para>
/// <para>
/// <b>Three spellings, all ArcGIS's:</b> a JSON object keyed by layer id
/// (<c>{"0":"il='Adana'"}</c>), a JSON array of <c>{layerId, where}</c>, and the older
/// <c>0:il='Adana';1:…</c>. Nothing the caller writes reaches SQL as text; each clause goes through
/// <see cref="WhereClause"/> against that layer's own columns.
/// </para>
/// </remarks>
internal static class LayerDefinitions
{
    private static readonly Regex Legacy = new(@"(?:^|;)\s*(\d+)\s*:", RegexOptions.Compiled);

    /// <summary>Reads <c>layerDefs</c> into a clause per layer index.</summary>
    /// <param name="raw">The parameter, or null.</param>
    /// <param name="available">The service's layers.</param>
    /// <param name="definitions">The clauses, by layer index; empty when none was sent.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(
        string? raw,
        IReadOnlyList<PublishedLayer> available,
        out Dictionary<int, string> definitions,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(available);

        definitions = [];
        error = null;

        string text = (raw ?? string.Empty).Trim();

        if (text.Length == 0 || text is "{}" or "[]")
        {
            return true;
        }

        try
        {
            if (text[0] == '{')
            {
                foreach (JsonProperty entry in JsonDocument.Parse(text).RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int index) || entry.Value.ValueKind != JsonValueKind.String)
                    {
                        error = $"`layerDefs` has '{entry.Name}', which is not a layer id with a where clause.";
                        return false;
                    }

                    definitions[index] = entry.Value.GetString()!;
                }
            }
            else if (text[0] == '[')
            {
                foreach (JsonElement entry in JsonDocument.Parse(text).RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("layerId", out JsonElement id) || !id.TryGetInt32(out int index)
                        || !entry.TryGetProperty("where", out JsonElement where) || where.ValueKind != JsonValueKind.String)
                    {
                        error = "Each entry of a `layerDefs` array needs a numeric `layerId` and a `where` string.";
                        return false;
                    }

                    definitions[index] = where.GetString()!;
                }
            }
            else
            {
                MatchCollection starts = Legacy.Matches(text);

                if (starts.Count == 0 || starts[0].Index != 0)
                {
                    error = "`layerDefs` is not a layer definition. Send {\"0\":\"field = 'value'\"} or 0:field = 'value'.";
                    return false;
                }

                for (int i = 0; i < starts.Count; i++)
                {
                    int from = starts[i].Index + starts[i].Length;
                    int to = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;

                    definitions[int.Parse(starts[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                        text[from..to].Trim();
                }
            }
        }
        catch (JsonException)
        {
            error = "`layerDefs` starts like JSON and is not JSON.";
            return false;
        }

        foreach (int index in definitions.Keys)
        {
            if (!available.Any(layer => layer.LayerIndex == index))
            {
                error = $"`layerDefs` names layer {index}, which this service does not have.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses each layer's clause against its columns, or says which one does not parse.
    /// </summary>
    /// <param name="definitions">The clauses, by layer index.</param>
    /// <param name="available">The service's layers.</param>
    /// <param name="contexts">Where a layer's columns come from.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The predicates by layer id, or the refusal.</returns>
    public static async Task<(Dictionary<Guid, AttributePredicate> Predicates, string? Error)> ParseAsync(
        IReadOnlyDictionary<int, string> definitions,
        IReadOnlyList<PublishedLayer> available,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        Dictionary<Guid, AttributePredicate> predicates = [];

        foreach ((int index, string clause) in definitions)
        {
            if (clause.Trim().Length == 0)
            {
                continue;
            }

            PublishedLayer layer = available.First(l => l.LayerIndex == index);

            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase);

            foreach (FieldDescription field in described.Fields)
            {
                types[field.Name] = field.Type;
            }

            // V-45: the constant idioms, answered as the query parameter answers them.
            (bool? constant, string rest) = FeatureServerQueryParameters.Reduce(clause);

            if (constant is true)
            {
                continue;
            }

            if (constant is false)
            {
                predicates[layer.Id] = new AttributePredicate.MatchesNothing();
                continue;
            }

            if (!WhereClause.TryParse(
                    rest,
                    [.. described.Fields.Select(f => f.Name)],
                    LayerDefinition.Quote,
                    out ParsedWhere parsed,
                    out string? error,
                    types))
            {
                return ([], $"`layerDefs` for layer {index} could not be parsed. {error}");
            }

            if (parsed.Predicate is { } predicate)
            {
                predicates[layer.Id] = predicate;
            }
        }

        return (predicates, null);
    }
}
