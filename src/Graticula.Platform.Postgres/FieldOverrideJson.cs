using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Graticula.Catalog;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The stored form of a layer's field overrides — ADR-063, migration 42.
/// </summary>
/// <remarks>
/// <para>
/// <b>One reader and one writer, in one file, because the shape is a contract between
/// them.</b> <c>layer.field_overrides</c> is a JSON array of
/// <c>{"column": …, "alias": …, "hidden": …}</c>; the catalogue reads it on every layer load
/// and the admin surface writes it, and two independently written serialisers is how a key
/// comes to be spelt two ways.
/// </para>
/// <para>
/// <b>A malformed entry is skipped rather than thrown, and the reason is proportion.</b> The
/// schema guarantees an array and the admin surface is the only writer, so a bad element can
/// only come from a hand edit in the database. Throwing would take the layer off every face
/// for the sake of a label; skipping serves it as though that entry were not there, which is
/// what the table alone would have said.
/// </para>
/// </remarks>
internal static class FieldOverrideJson
{
    /// <summary>Reads the stored array.</summary>
    /// <param name="json">The column's value.</param>
    public static ImmutableArray<FieldOverride> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ImmutableArray<FieldOverride>.Empty;
        }

        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0)
        {
            return ImmutableArray<FieldOverride>.Empty;
        }

        ImmutableArray<FieldOverride>.Builder read =
            ImmutableArray.CreateBuilder<FieldOverride>(document.RootElement.GetArrayLength());

        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("column", out JsonElement column)
                || column.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(column.GetString()))
            {
                continue;
            }

            string? alias = entry.TryGetProperty("alias", out JsonElement a)
                && a.ValueKind == JsonValueKind.String
                    ? a.GetString()
                    : null;

            bool hidden = entry.TryGetProperty("hidden", out JsonElement h)
                && h.ValueKind == JsonValueKind.True;

            read.Add(new FieldOverride(column.GetString()!, alias, hidden));
        }

        return read.ToImmutable();
    }

    /// <summary>Writes the array to store.</summary>
    /// <param name="overrides">What is to be stored; entries that say nothing are dropped.</param>
    public static string Write(IEnumerable<FieldOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        List<Dictionary<string, object?>> stored = [];

        foreach (FieldOverride says in overrides)
        {
            // <b>What is stored is what is claimed.</b> An entry that neither renames nor
            // hides would let a surface show a column as overridden when nothing about it
            // differs from the table.
            if (!says.SaysSomething)
            {
                continue;
            }

            stored.Add(new Dictionary<string, object?>
            {
                ["column"] = says.Column,
                ["alias"] = string.IsNullOrWhiteSpace(says.Alias) ? null : says.Alias,
                ["hidden"] = says.Hidden,
            });
        }

        return JsonSerializer.Serialize(stored);
    }
}
