using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GisServer.Features;

/// <summary>
/// The ordered attribute names shared by every feature in one result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than per feature, and that is an allocation decision.</b> The
/// obvious model gives each feature a dictionary; a dense tile is 4,863 features,
/// so that is 4,863 dictionaries and their buckets for a field list that is
/// identical in every one of them. <c>A-037</c> established allocation as this
/// server's binding constraint, so the names live once and each feature carries
/// only its values.
/// </para>
/// <para>
/// Lookup is linear over the names. Field lists are short — an ArcGIS
/// <c>outFields</c> is typically under twenty — and a linear scan of twenty
/// strings beats a hash computation and a bucket walk at that size.
/// </para>
/// </remarks>
public sealed class FeatureSchema
{
    /// <summary>A schema with no attributes.</summary>
    public static FeatureSchema Empty { get; } = new([]);

    /// <summary>Creates a schema.</summary>
    /// <exception cref="ArgumentException">A name is blank or duplicated.</exception>
    public FeatureSchema(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        string[] copy = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException($"Attribute {i} has no name.", nameof(names));
            }

            for (int earlier = 0; earlier < i; earlier++)
            {
                if (string.Equals(copy[earlier], name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Attribute '{name}' appears twice. Two attributes with one name means a "
                        + "lookup silently returns whichever came first.", nameof(names));
                }
            }

            copy[i] = name;
        }

        Names = new ReadOnlyCollection<string>(copy);
    }

    /// <summary>The attribute names, in the order values appear.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>How many attributes each feature carries.</summary>
    public int Count => Names.Count;

    /// <summary>
    /// The position of <paramref name="name"/>, or -1 if the schema has no such
    /// attribute.
    /// </summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
