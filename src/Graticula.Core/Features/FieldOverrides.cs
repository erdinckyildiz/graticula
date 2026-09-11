using System;
using System.Collections.Generic;
using Graticula.Catalog;

namespace Graticula.Features;

/// <summary>
/// Applies a layer's per-column overrides to the field list read from its table.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because ADR-063 §6 says the cost of this decision is that every field-list
/// reader has to apply them and the reader that forgets is a leak.</b> Three document
/// writers, the query path, the write path and the filter reader all take their column names
/// from <see cref="LayerDescription"/>; applying the overrides to that one object means none
/// of them has to know this decision exists. It is the same answer CLAUDE.md §2 asks for
/// before an edit — <em>what else carries this?</em> — arrived at by making the answer
/// <em>nothing</em>.
/// </para>
/// <para>
/// <b>Applied after the table is read, never before.</b> The field list is still whatever the
/// table has; this runs over the result. That is the whole of the drift answer: an override
/// naming a column that has gone matches nothing and changes nothing, and a column that
/// appears is described exactly as it would have been.
/// </para>
/// </remarks>
public static class FieldOverrides
{
    /// <summary>
    /// The description a caller should see, with aliases attached and hidden columns removed.
    /// </summary>
    /// <param name="described">What the table said.</param>
    /// <param name="overrides">What the layer says about it.</param>
    /// <returns>
    /// <paramref name="described"/> itself when there is nothing to apply, and a new
    /// description otherwise.
    /// </returns>
    /// <remarks>
    /// <b>The unchanged case allocates nothing and returns the same instance.</b> A-037
    /// established allocation rather than CPU as this server's binding constraint, and almost
    /// every layer has no overrides at all; a version of this that rebuilt the list every
    /// time would put an allocation on the describe path of every layer in the catalogue to serve
    /// the few that say anything.
    /// </remarks>
    public static LayerDescription Apply(
        LayerDescription described, IReadOnlyList<FieldOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.Count == 0)
        {
            return described;
        }

        List<FieldDescription> kept = new(described.Fields.Count);
        bool changed = false;

        foreach (FieldDescription field in described.Fields)
        {
            FieldOverride? said = For(overrides, field.Name);

            if (said is not { } says)
            {
                kept.Add(field);
                continue;
            }

            if (says.Hidden)
            {
                // <b>Removed rather than marked.</b> Every surface validates a column name
                // against this list, so a column that is not in it is refused by the code
                // that refuses an unknown one — in `outFields`, in `where`, in
                // `orderByFields` and on the write path, without any of them being told.
                changed = true;
                continue;
            }

            string? alias = string.IsNullOrWhiteSpace(says.Alias) ? null : says.Alias;

            if (!string.Equals(alias, field.Alias, StringComparison.Ordinal))
            {
                changed = true;
                kept.Add(field with { Alias = alias });
                continue;
            }

            kept.Add(field);
        }

        // <b>An override that matched nothing is not a change.</b> A layer whose only
        // overrides name dropped columns describes exactly what the table describes, and
        // rebuilding the list to say so would allocate for a difference nobody can observe.
        return changed ? described with { Fields = kept } : described;
    }

    /// <summary>
    /// Whether <paramref name="column"/> is hidden by <paramref name="overrides"/>.
    /// </summary>
    /// <param name="overrides">A layer's overrides.</param>
    /// <param name="column">A column name from the table.</param>
    /// <remarks>
    /// <b>For the paths that do not go through a description, and there should be none.</b>
    /// It exists so that a caller who finds one can ask the question directly rather than
    /// inventing a second rule — and so that the refusal written at the point an override is
    /// set can say whether a column is already hidden.
    /// </remarks>
    public static bool Hides(IReadOnlyList<FieldOverride> overrides, string column)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(column);

        return For(overrides, column) is { Hidden: true };
    }

    /// <summary>
    /// Those overrides that name no column this table has.
    /// </summary>
    /// <param name="described">What the table said.</param>
    /// <param name="overrides">What the layer says about it.</param>
    /// <remarks>
    /// <b>ADR-063 condition 3: inert is the design, visible is the condition.</b> An operator
    /// who renames a column in the database and loses a label has to be able to find out why,
    /// so the admin surface reports these rather than silently dropping them.
    /// </remarks>
    public static IReadOnlyList<FieldOverride> Inert(
        LayerDescription described, IReadOnlyList<FieldOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(overrides);

        List<FieldOverride> orphaned = [];

        foreach (FieldOverride says in overrides)
        {
            if (described.Find(says.Column) is null)
            {
                orphaned.Add(says);
            }
        }

        return orphaned;
    }

    private static FieldOverride? For(IReadOnlyList<FieldOverride> overrides, string column)
    {
        // Linear, for the reason `FeatureSchema` gives: a field list is short and an override
        // list is shorter, and a hash over a handful of strings costs more than the scan.
        for (int i = 0; i < overrides.Count; i++)
        {
            if (overrides[i].Matches(column))
            {
                return overrides[i];
            }
        }

        return null;
    }
}
