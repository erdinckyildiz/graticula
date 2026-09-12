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
/// <c>{"column": …, "alias": …, "hidden": …}</c>, with <c>tracks</c> (ADR-064) and
/// <c>domain</c> and <c>subtypes</c> (ADR-065, in <see cref="FieldDomainJson"/>'s shapes) when an
/// entry says them; the catalogue reads it on every layer load
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

            // <b>An edit role, ADR-064, and an unknown one reads as none.</b> A role a newer
            // build wrote and this one does not know is a column this build does not maintain —
            // which leaves the layer untracked here and its updates needing features:fullEdit,
            // the safe direction for a downgrade.
            EditRole tracks = entry.TryGetProperty("tracks", out JsonElement t)
                && t.ValueKind == JsonValueKind.String
                && Enum.TryParse(t.GetString(), ignoreCase: true, out EditRole role)
                && Enum.IsDefined(role)
                    ? role
                    : EditRole.None;

            // <b>A domain and subtypes, ADR-065, and one that cannot be read is left out</b> — for
            // the proportion argument above: the layer is served as though the entry said nothing
            // about values, which is what it said before, rather than taken off every face.
            FieldDomain? domain = entry.TryGetProperty("domain", out JsonElement d)
                && d.ValueKind == JsonValueKind.Object
                    ? FieldDomainJson.ReadDomain(d, out _)
                    : null;

            LayerSubtypes? subtypes = entry.TryGetProperty("subtypes", out JsonElement st)
                && st.ValueKind == JsonValueKind.Object
                    ? FieldDomainJson.ReadSubtypes(column.GetString()!, st, out _)
                    : null;

            read.Add(new FieldOverride(column.GetString()!, alias, hidden, tracks, domain, subtypes));
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

            Dictionary<string, object?> entry = new()
            {
                ["column"] = says.Column,
                ["alias"] = string.IsNullOrWhiteSpace(says.Alias) ? null : says.Alias,
                ["hidden"] = says.Hidden,
            };

            // <b>Only when it says something</b>, so a layer that tracks nothing stores exactly
            // what it stored before ADR-064 — and an older build reading it finds no key it
            // has to ignore.
            if (says.Tracks != EditRole.None)
            {
                entry["tracks"] = says.Tracks.ToString().ToLowerInvariant();
            }

            // ADR-065, on the same terms: absent unless it says something.
            if (says.Domain is { } domain)
            {
                entry["domain"] = FieldDomainJson.Write(domain);
            }

            if (says.Subtypes is { } subtypes)
            {
                entry["subtypes"] = FieldDomainJson.Write(subtypes, withField: false);
            }

            stored.Add(entry);
        }

        return JsonSerializer.Serialize(stored);
    }
}
