using System;
using System.Collections.Generic;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>
/// A field's type, in our vocabulary rather than a provider's or a protocol's.
/// </summary>
/// <remarks>
/// <para>
/// The provider maps its own type names into this; the protocol surface maps
/// this into its own. Neither knows the other's names, which is the one place
/// [ADR-005]'s protocol-neutral interface earns its keep concretely rather than
/// as an assertion — PostgreSQL's <c>int4</c> and ArcGIS's
/// <c>esriFieldTypeInteger</c> never appear in the same file.
/// </para>
/// <para>
/// <b>Deliberately coarse.</b> It carries what a client needs in order to render
/// and filter a value, not what the database needs in order to store one.
/// Precision, scale, collation and domain are absent because no consumer of this
/// has ever needed them.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification =
        "The rule exists to stop a member being named after the CLR type it happens to hold. "
        + "This enum's entire purpose is to name types, and Integer, Double, Single and Guid are "
        + "what the things are called in every database and every protocol this maps between. "
        + "Renaming them to avoid the collision would make the mapping tables harder to read for "
        + "no benefit.")]
public enum FieldType
{
    /// <summary>A type we do not recognise. Rendered as text.</summary>
    Unknown,

    /// <summary>16-bit integer.</summary>
    SmallInteger,

    /// <summary>32-bit integer.</summary>
    Integer,

    /// <summary>
    /// 64-bit integer.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="Integer"/> because it cannot be carried
    /// safely by every protocol: JavaScript loses integer precision above 2^53,
    /// so a surface that has to choose will send it as text. That choice belongs
    /// to the surface, and it can only make it if the type is not flattened
    /// here.
    /// </remarks>
    BigInteger,

    /// <summary>32-bit floating point.</summary>
    Single,

    /// <summary>64-bit floating point, and arbitrary-precision numerics.</summary>
    Double,

    /// <summary>Text of any length.</summary>
    Text,

    /// <summary>True or false.</summary>
    Boolean,

    /// <summary>A date or timestamp.</summary>
    Date,

    /// <summary>A UUID.</summary>
    Guid,

    /// <summary>Opaque bytes.</summary>
    Binary,
}

/// <summary>One attribute column of a layer.</summary>
/// <param name="Name">Its name, as the provider spells it.</param>
/// <param name="Type">Its type.</param>
/// <param name="Nullable">Whether it accepts null.</param>
/// <param name="MaxLength">Its declared length, for text, or null.</param>
/// <param name="Alias">
/// What to call it on the wire, or <see langword="null"/> for its own name.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Alias"/> is a label and never an identifier — ADR-063.</b> It is what a
/// client shows a person; every place this server matches a column, filters on one, orders
/// by one or writes to one goes on using <see cref="Name"/>. A caller may not ask for a
/// column by its alias, because two layers over one table may label it differently and a
/// name that means different columns in different services is not a name.
/// </para>
/// <para>
/// <b>On the end and optional, which is deliberate.</b> Every construction of this type that
/// predates the decision keeps compiling and keeps meaning what it meant — no alias — and a
/// provider that has no opinion about labels does not have to acquire one.
/// </para>
/// <para>
/// <b>There is no <c>Hidden</c> here, and its absence is the design.</b> A hidden column is
/// removed from <see cref="LayerDescription.Fields"/> rather than flagged in it, so every
/// surface that already refuses a column it cannot find refuses a hidden one by the same
/// code and with the same words. A flag would need every reader to remember to check it, and
/// the reader that forgets is a leak rather than a cosmetic bug.
/// </para>
/// </remarks>
public readonly record struct FieldDescription(
    string Name, FieldType Type, bool Nullable, int? MaxLength, string? Alias = null)
{
    /// <summary>What a client should show for this column.</summary>
    public string Label => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;

    /// <summary>
    /// Whether this server writes the column and a client does not — ADR-064's edit roles.
    /// </summary>
    /// <remarks>
    /// <b>Set in one place, <see cref="FieldOverrides.Apply"/>, and read by every face</b>: the
    /// ArcGIS document reports such a field not editable, and the writer replaces whatever a
    /// client sent for it. Not positional, so every description built before this existed still
    /// means a column the client may write.
    /// </remarks>
    public bool Maintained { get; init; }
}

/// <summary>
/// What a client needs to know about a layer before querying it.
/// </summary>
/// <param name="Fields">Its attribute columns, geometry excluded.</param>
/// <param name="Extent">
/// Where its features are, or null if that cannot be determined.
/// </param>
/// <param name="Writable">
/// Whether the store will accept writes to whatever this layer sits on, or null when it
/// could not be asked.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Writable"/> is the store's answer, not the caller's privileges —
/// [D-231](../../../docs/architecture-debt.md).</b> The ArcGIS layer document advertised
/// <c>Query,Create,Update,Delete</c> from the caller's privileges alone, so a layer over a
/// materialized view or a join view — relations PostgreSQL refuses every write to — told a
/// client it could edit and then refused every edit. Measured 2026-09-10 across four
/// relation kinds: all four advertised editing, and two of them accepted none of it.
/// <see cref="Graticula.Features.LayerDescription"/> is where the answer belongs because it
/// is the one shape the request path already has in hand.
/// </para>
/// <para>
/// <b>It is read on every describe rather than stored at publish time</b>, which is a
/// decision and not an accident. A view becomes writable the moment somebody adds an
/// <c>INSTEAD OF</c> trigger and stops being writable when they drop it, so a column
/// written when the layer was published would be right on the day it was written and
/// drifting from then on. What bounds the staleness is the describe cache's own lifetime,
/// the same bound the field list has.
/// </para>
/// <para>
/// <b>Null means <em>nobody asked</em>, and it is not a no.</b> The narrowing acts on a
/// definite <c>false</c> only: a surface that cannot reach a store to ask must not
/// silently take a capability away, because an under-claim looks exactly like a
/// deliberately read-only layer and there is nothing in the document to say otherwise.
/// The one production provider always answers; the default is for the surfaces and the
/// fakes that have no relation to ask about.
/// </para>
/// <para>
/// <b>The extent may be an estimate, and callers must treat it as one.</b> A
/// client uses it to decide where to put the map, and being slightly wrong there
/// costs a pan. Computing it exactly means reading every geometry in the table,
/// which for the 6.5-million-row corpus this project tests against is not
/// something to do while somebody waits for a layer to load.
/// </para>
/// <para>
/// Null means <em>unknown</em>, not <em>empty</em>. A client that treats them the
/// same zooms to the origin off the coast of Africa, which is the classic
/// symptom of exactly this confusion.
/// </para>
/// </remarks>
public sealed record LayerDescription(
    IReadOnlyList<FieldDescription> Fields, Envelope? Extent, bool? Writable = null)
{
    /// <summary>
    /// Which of these columns record edits, and whether the layer is tracked — ADR-064.
    /// </summary>
    /// <remarks>
    /// <b>Set by <see cref="FieldOverrides.Apply"/> from the columns the table actually has</b>,
    /// so a role naming a column somebody dropped tracks nothing — ADR-063's drift answer, which
    /// is inert rather than broken. None for every description built before this existed.
    /// </remarks>
    public Graticula.Catalog.EditorTracking Tracking { get; init; } =
        Graticula.Catalog.EditorTracking.None;

    /// <summary>Finds a field by name, or null.</summary>
    public FieldDescription? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (FieldDescription field in Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }
}
