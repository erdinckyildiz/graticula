using System;

namespace Graticula.Catalog;

/// <summary>
/// What a layer says about one of its table's columns that the table does not say.
/// </summary>
/// <param name="Column">
/// The column's name, as the table spells it. An override naming a column the table does
/// not have describes nothing and is inert; see the remarks.
/// </param>
/// <param name="Alias">
/// What to call the column on the wire, or <see langword="null"/> to use its own name.
/// </param>
/// <param name="Hidden">
/// Whether the column is absent from every surface. <see langword="false"/> is the default
/// and means the column behaves as it did before this decision existed.
/// </param>
/// <remarks>
/// <para>
/// <b>Two things and no more —
/// <see href="https://github.com/erdinckyildiz/graticula/blob/main/docs/adr/ADR-063-a-field-list-may-differ-from-the-table.md">ADR-063</see>,
/// by owner decision 2026-09-09 answering Q-36.</b> A layer may rename a column for display
/// and may hide it. It may not create one, and it may not change a column's type,
/// nullability or name on the wire. <b>Computed fields were refused with the reason</b>: an
/// expression has a language, a type, an evaluation site, an injection surface and a cost
/// per row, it has to be pushed into SQL or it defeats every predicate that touches it, and
/// it turns schema drift from inert into broken.
/// </para>
/// <para>
/// <b>Inert rather than broken is the whole of the drift answer.</b> The field list is still
/// read from the table on every describe; overrides are applied to it afterwards by column
/// name. So an override naming a column somebody dropped describes nothing and therefore
/// says nothing false, and a column somebody added appears with no alias and unhidden —
/// which is what it would have done before this existed. Nothing is cached that the table
/// does not own, so nothing has to be reconciled on a schedule.
/// </para>
/// <para>
/// <b>Inert is not the same as invisible, and the difference is condition 3.</b> An operator
/// who renames a column in the database and loses a label has to be able to find out why, so
/// the admin surface reports overrides that match nothing rather than dropping them quietly.
/// </para>
/// <para>
/// <b>Hidden means hidden everywhere or it means nothing.</b> A hidden column is absent from
/// the layer document, absent from <c>outFields=*</c>, refused when named explicitly,
/// refused in a filter or <c>where</c> clause, refused as an <c>orderByFields</c> or
/// statistics target, and not writable. The mechanism is removal rather than a check at each
/// door: <see cref="Graticula.Features.LayerDescription"/> is what every surface validates a
/// column name against, so a column taken out of it is refused by the code that already
/// refuses unknown columns — and a caller cannot tell a hidden column from an absent one,
/// which is the point. A column absent from the document but usable in one predicate at a
/// time is not hidden; it is searchable, which is how a value is recovered without ever
/// being displayed.
/// </para>
/// </remarks>
public readonly record struct FieldOverride(string Column, string? Alias, bool Hidden)
{
    /// <summary>
    /// Whether this override says anything at all.
    /// </summary>
    /// <remarks>
    /// <b>An override that neither renames nor hides is a row with no effect</b>, and
    /// keeping one lets a surface show a column as *overridden* when nothing about it
    /// differs from the table. The admin surface drops these on write rather than storing
    /// them, so what is stored is what is claimed.
    /// </remarks>
    public bool SaysSomething =>
        Hidden || !string.IsNullOrWhiteSpace(Alias);

    /// <summary>
    /// Whether <paramref name="column"/> is the column this override is about.
    /// </summary>
    /// <param name="column">A column name from the table.</param>
    /// <remarks>
    /// <b>Ordinal, because PostgreSQL folds unquoted identifiers to lower case and this
    /// server quotes them.</b> A column stored as <c>Name</c> is <c>Name</c> everywhere this
    /// product reads it, so matching case-insensitively here would let one override claim two
    /// columns on a table that has both — which PostgreSQL allows and which no other part of
    /// this code treats as the same column.
    /// </remarks>
    public bool Matches(string column) =>
        string.Equals(Column, column, StringComparison.Ordinal);
}
