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
public readonly record struct FieldDescription(
    string Name, FieldType Type, bool Nullable, int? MaxLength);

/// <summary>
/// What a client needs to know about a layer before querying it.
/// </summary>
/// <param name="Fields">Its attribute columns, geometry excluded.</param>
/// <param name="Extent">
/// Where its features are, or null if that cannot be determined.
/// </param>
/// <remarks>
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
public sealed record LayerDescription(IReadOnlyList<FieldDescription> Fields, Envelope? Extent)
{
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
