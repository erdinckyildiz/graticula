using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;

namespace Graticula.Host;

/// <summary>
/// The rows a feature export takes from each layer when it is filtered — ADR-107 §8, owner decision 2026-11-09.
/// </summary>
/// <remarks>
/// <para>
/// <b>A layer's own filter goes through the grammar every where clause here goes through</b> (<see cref="WhereClause"/>):
/// its columns are matched against the layer's, its literals are bound, and nothing the caller wrote reaches SQL as text.
/// </para>
/// <para>
/// <b>A chosen layer related to a filtered chosen layer takes only the rows that belong to the rows the filter
/// selected</b> — a form's repeat rows under the submissions the filter kept, an inspection's photos under the
/// inspections. Exporting a filtered parent with every child would hand over the rows the filter was there to leave out.
/// The limit follows the relationship from its origin to the related layer and not back, and through more than one
/// level: a repeat inside a repeat follows its parent, which follows the filter.
/// </para>
/// <para>
/// <b>Written as one statement in the database</b> — <c>key in (select origin_key from origin where …)</c> — so a
/// filter that keeps a million parents is not a million ids carried through this process. That needs both tables in
/// the same database under this server's own SQL, which a hosted layer always is; a related layer that is not is
/// refused with the reason rather than exported whole.
/// </para>
/// </remarks>
internal static class ExportFilters
{
    /// <summary>A chosen layer's rows: its predicate, or null for all of them, and the layer it follows, if any.</summary>
    internal sealed record Rows(ParsedWhere? Where, int? Follows);

    /// <summary>
    /// Each chosen layer's predicate, in the order given — or the sentence that says why the filters cannot be applied.
    /// </summary>
    /// <param name="layers">The chosen layers.</param>
    /// <param name="filters">The caller's where clause by layer id; null or empty for none.</param>
    /// <param name="contexts">The layers' descriptions.</param>
    /// <param name="relationships">The declared relationships.</param>
    /// <param name="cancellation">Cancellation.</param>
    internal static async Task<(IReadOnlyList<Rows>? Rows, string? Error)> BuildAsync(
        IReadOnlyList<PublishedLayer> layers,
        IReadOnlyDictionary<int, string>? filters,
        ServiceContexts contexts,
        PostgresRelationshipCatalog relationships,
        CancellationToken cancellation)
    {
        Rows[] rows = [.. layers.Select(_ => new Rows(null, null))];

        if (filters is null || filters.Count == 0)
        {
            return (rows, null);
        }

        Dictionary<int, int> at = layers.Select((layer, i) => (layer.LayerIndex, i)).ToDictionary(x => x.LayerIndex, x => x.i);

        foreach (int id in filters.Keys)
        {
            if (!at.ContainsKey(id))
            {
                return (null, $"'where' names layer {id}, which is not one of the layers being exported.");
            }
        }

        // Each layer's own filter, parsed against its own columns.
        AttributePredicate?[] own = new AttributePredicate?[layers.Count];
        IReadOnlyList<string>[] columns = new IReadOnlyList<string>[layers.Count];

        for (int i = 0; i < layers.Count; i++)
        {
            LayerDescription described = (await contexts.GetAsync(layers[i], cancellation).ConfigureAwait(false)).Description;
            columns[i] = [.. described.Fields.Select(f => f.Name)];

            if (!filters.TryGetValue(layers[i].LayerIndex, out string? text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase);

            foreach (FieldDescription field in described.Fields)
            {
                types[field.Name] = field.Type;
            }

            if (!WhereClause.TryParse(text, columns[i], LayerDefinition.Quote, out ParsedWhere parsed, out string? error, types))
            {
                return (null, $"The filter on '{layers[i].Definition.Name}' could not be read: {error}");
            }

            own[i] = parsed.Predicate;
        }

        // Which chosen layer each one follows: the origin of a relationship to it that is chosen and is itself limited.
        int?[] follows = new int?[layers.Count];
        string?[] key = new string?[layers.Count];
        string?[] originKey = new string?[layers.Count];
        Dictionary<Guid, int> byId = layers.Select((layer, i) => (layer.Id, i)).ToDictionary(x => x.Id, x => x.i);
        List<LayerRelationship> declared = [];

        foreach (PublishedLayer layer in layers)
        {
            foreach (LayerRelationship relationship in await relationships.ForLayerAsync(layer.Id, cancellation).ConfigureAwait(false))
            {
                if (relationship.OriginLayerId == layer.Id && byId.ContainsKey(relationship.RelatedLayerId))
                {
                    declared.Add(relationship);
                }
            }
        }

        bool[] limited = [.. own.Select(p => p is not null)];

        for (bool changed = true; changed;)
        {
            changed = false;

            foreach (LayerRelationship relationship in declared)
            {
                int origin = byId[relationship.OriginLayerId];
                int related = byId[relationship.RelatedLayerId];

                if (!limited[origin] || follows[related] is not null || origin == related)
                {
                    continue;
                }

                if (!layers[origin].Definition.IsHosted || !layers[related].Definition.IsHosted)
                {
                    return (null,
                        $"'{layers[related].Definition.Name}' is related to '{layers[origin].Definition.Name}', which is filtered, and "
                        + "only hosted layers are limited to the rows their origin's filter keeps. Filter it with its own "
                        + "where, or leave it out of the export.");
                }

                follows[related] = origin;
                key[related] = relationship.RelatedKey;
                originKey[related] = relationship.OriginKey;
                limited[related] = true;
                changed = true;
            }
        }

        // Written out, origins before the layers that follow them.
        ParsedWhere?[] written = new ParsedWhere?[layers.Count];

        for (int pass = 0; pass <= layers.Count; pass++)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (written[i] is not null || !limited[i])
                {
                    continue;
                }

                if (follows[i] is { } origin && written[origin] is null)
                {
                    continue;
                }

                written[i] = Write(i);
            }
        }

        for (int i = 0; i < layers.Count; i++)
        {
            rows[i] = new Rows(written[i], follows[i] is { } f ? layers[f].LayerIndex : null);
        }

        return (rows, null);

        ParsedWhere Write(int i)
        {
            List<object?> parameters = [];
            List<string> parts = [];

            if (follows[i] is { } origin)
            {
                ParsedWhere above = written[origin]!.Value;
                LayerDefinition source = layers[origin].Definition;
                parameters.AddRange(above.Parameters);
                parts.Add(
                    $"{LayerDefinition.Quote(key[i]!)} in (select {LayerDefinition.Quote(originKey[i]!)} from "
                    + $"{LayerDefinition.Quote(source.SchemaName)}.{LayerDefinition.Quote(source.TableName)}"
                    + (above.Sql.Length > 0 ? $" where ({above.Sql})" : string.Empty) + ")");
            }

            if (own[i] is { } predicate)
            {
                int offset = parameters.Count;

                // Its own literals numbered after the origin's, so the two bind side by side in one statement.
                if (!PredicateSql.TryEmit(predicate, columns[i], LayerDefinition.Quote, out ParsedWhere mine, out string? error,
                        n => "@w" + (n + offset).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    throw new InvalidOperationException($"A parsed filter could not be written again: {error}");
                }

                parameters.AddRange(mine.Parameters);
                parts.Add($"({mine.Sql})");
            }

            return new ParsedWhere(string.Join(" and ", parts), parameters);
        }
    }

    /// <summary>The filters as the wire carries them: a layer id as text and its where clause.</summary>
    internal static IReadOnlyDictionary<int, string>? Read(IReadOnlyDictionary<string, string>? wire, out string? error)
    {
        error = null;

        if (wire is null || wire.Count == 0)
        {
            return null;
        }

        Dictionary<int, string> filters = [];

        foreach ((string id, string text) in wire)
        {
            if (!int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int layer))
            {
                error = $"'where' is an object of layer ids and where clauses; '{id}' is not a layer id.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                filters[layer] = text.Trim();
            }
        }

        return filters.Count == 0 ? null : filters;
    }
}
