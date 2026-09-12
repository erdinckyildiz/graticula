using System;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Catalog;

/// <summary>
/// One subtype of a layer: a code in its subtype column, a name, and what differs for features
/// of that kind — ADR-065.
/// </summary>
/// <param name="Code">The value its features hold in the subtype column.</param>
/// <param name="Name">What a person is shown.</param>
/// <param name="Defaults">
/// A value per column that a new feature of this kind starts with. <b>Carried to clients in the
/// template, and never written by this server</b>: a client that creates a feature from the
/// template sends them, and one that does not has sent what it meant.
/// </param>
/// <param name="Domains">
/// A domain per column that replaces the column's own for features of this kind. A column with no
/// entry keeps its own domain — the ArcGIS REST API's <c>inherited</c>.
/// </param>
public sealed record Subtype(
    long Code,
    string Name,
    IReadOnlyDictionary<string, DomainValue> Defaults,
    IReadOnlyDictionary<string, FieldDomain> Domains)
{
    /// <summary>
    /// Whether two subtypes say the same thing, entry for entry.
    /// </summary>
    /// <param name="other">The other subtype.</param>
    /// <returns>Whether they are the same claim.</returns>
    public bool SameAs(Subtype? other) =>
        other is not null
        && Code == other.Code
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Defaults.Count == other.Defaults.Count
        && Defaults.All(d => other.Defaults.TryGetValue(d.Key, out DomainValue v) && v == d.Value)
        && Domains.Count == other.Domains.Count
        && Domains.All(d => other.Domains.TryGetValue(d.Key, out FieldDomain? v) && d.Value.SameAs(v));
}

/// <summary>
/// A layer's subtypes — ADR-065.
/// </summary>
/// <param name="Field">The integer column whose value says which subtype a feature is.</param>
/// <param name="DefaultCode">The subtype a client offers first.</param>
/// <param name="Types">The subtypes, in the order they were given.</param>
/// <remarks>
/// <para>
/// <b>Carried by the field override of the subtype column</b>, because Q-58c put the rest of the
/// FeatureServer data model with ADR-063's machinery: <em>this column's values are subtype codes,
/// and these are their names</em> is a claim about one column, like its label. It inherits the
/// drift answer with it — a subtype column somebody dropped leaves an override that describes
/// nothing, and the layer has no subtypes rather than subtypes nobody can write.
/// </para>
/// <para>
/// <b>What varies by subtype is defaults and domains, and nothing else.</b> That is what the
/// ArcGIS REST API's <c>subtypes</c> and <c>types</c> objects carry, and what a geodatabase's
/// <c>SubtypeFieldInfo</c> holds.
/// </para>
/// </remarks>
public sealed record LayerSubtypes(string Field, long DefaultCode, IReadOnlyList<Subtype> Types)
{
    /// <summary>The subtype with this code, or null.</summary>
    /// <param name="code">A code.</param>
    /// <returns>The subtype.</returns>
    public Subtype? Find(long code)
    {
        foreach (Subtype type in Types)
        {
            if (type.Code == code)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether any subtype gives <paramref name="column"/> a domain of its own.
    /// </summary>
    /// <param name="column">A column name.</param>
    /// <returns>
    /// Whether checking a value for that column needs to know which subtype the feature is.
    /// </returns>
    public bool Varies(string column)
    {
        foreach (Subtype type in Types)
        {
            if (type.Domains.ContainsKey(column))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The domain that governs <paramref name="column"/> for a feature of subtype
    /// <paramref name="code"/>.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="code">The feature's subtype, or null when it has none.</param>
    /// <param name="own">The column's own domain, or null.</param>
    /// <returns>The subtype's domain when it has one, and the column's own otherwise.</returns>
    public FieldDomain? DomainFor(string column, long? code, FieldDomain? own) =>
        code is { } known
        && Find(known) is { } type
        && type.Domains.TryGetValue(column, out FieldDomain? replaced)
            ? replaced
            : own;

    /// <summary>
    /// The same subtypes with every reference to a column not in <paramref name="keep"/> removed.
    /// </summary>
    /// <param name="keep">Whether a column is still on the layer.</param>
    /// <param name="fits">
    /// Whether a subtype's domain fits the column it names, as the column is today; null to keep
    /// every domain on a kept column.
    /// </param>
    /// <returns>The narrowed subtypes, or this instance when nothing had to go.</returns>
    /// <remarks>
    /// <b>ADR-063's drift answer, applied inside a subtype</b>: a default or a domain naming a
    /// column that has gone, or one that is hidden, describes nothing a client can see and is
    /// left out rather than reported — a hidden column's default in a template would be the
    /// column's name on the wire.
    /// </remarks>
    public LayerSubtypes Narrowed(Func<string, bool> keep, Func<string, FieldDomain, bool>? fits = null)
    {
        ArgumentNullException.ThrowIfNull(keep);

        bool Kept(KeyValuePair<string, FieldDomain> domain) =>
            keep(domain.Key) && (fits is null || fits(domain.Key, domain.Value));

        bool changed = false;
        List<Subtype> kept = new(Types.Count);

        foreach (Subtype type in Types)
        {
            if (type.Defaults.Keys.All(keep) && type.Domains.All(Kept))
            {
                kept.Add(type);
                continue;
            }

            changed = true;
            kept.Add(type with
            {
                Defaults = type.Defaults.Where(d => keep(d.Key))
                    .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
                Domains = type.Domains.Where(Kept)
                    .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
            });
        }

        return changed ? this with { Types = kept } : this;
    }

    /// <summary>
    /// Whether two sets of subtypes say the same thing, entry for entry.
    /// </summary>
    /// <param name="other">The other set.</param>
    /// <returns>Whether they are the same claim.</returns>
    public bool SameAs(LayerSubtypes? other) =>
        other is not null
        && string.Equals(Field, other.Field, StringComparison.Ordinal)
        && DefaultCode == other.DefaultCode
        && Types.Count == other.Types.Count
        && Types.Zip(other.Types).All(pair => pair.First.SameAs(pair.Second));
}
