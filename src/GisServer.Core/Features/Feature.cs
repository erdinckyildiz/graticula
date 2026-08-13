using System;
using System.Globalization;
using GisServer.Geometries;

namespace GisServer.Features;

/// <summary>
/// One row of a layer: an identity, a geometry, and its attribute values.
/// </summary>
/// <remarks>
/// <para>
/// The identity is a string because that is the widest thing a provider can
/// return. Q-57 settled that identity is <em>declared</em> rather than inferred,
/// and a declared column may hold an integer, a uuid or text. ArcGIS
/// FeatureServer additionally needs a unique integer, which is why
/// <c>layer.object_id_column</c> is a separate, nullable thing — ADR-013 §2a.
/// </para>
/// <para>
/// Geometry may be <see langword="null"/>: a spatial table is allowed rows with
/// no shape, and dropping them would quietly change the answer to a count.
/// </para>
/// </remarks>
public sealed class Feature
{
    private readonly object?[] _values;

    /// <summary>Creates a feature.</summary>
    /// <exception cref="ArgumentException">
    /// The value count does not match the schema, which would silently misalign
    /// every attribute after the missing one.
    /// </exception>
    public Feature(string id, Geometry? geometry, FeatureSchema schema, object?[] values)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length != schema.Count)
        {
            throw new ArgumentException(
                $"The schema has {schema.Count} attribute(s) but {values.Length} value(s) were "
                + "supplied. A mismatch misaligns every attribute after the gap.", nameof(values));
        }

        Id = id;
        Geometry = geometry;
        Schema = schema;
        _values = values;
    }

    /// <summary>The declared identity, as text.</summary>
    public string Id { get; }

    /// <summary>The shape, or <see langword="null"/> where the row has none.</summary>
    public Geometry? Geometry { get; }

    /// <summary>The attribute names, shared with every feature in this result.</summary>
    public FeatureSchema Schema { get; }

    /// <summary>The attribute value at <paramref name="index"/>.</summary>
    public object? this[int index] => _values[index];

    /// <summary>
    /// The value of <paramref name="name"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The schema has no such attribute.</exception>
    public object? this[string name]
    {
        get
        {
            int index = Schema.IndexOf(name);

            return index >= 0
                ? _values[index]
                : throw new ArgumentException(
                    $"This result has no attribute '{name}'. It has: "
                    + $"{string.Join(", ", Schema.Names)}.", nameof(name));
        }
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Feature {Id} [{Geometry?.Kind.ToString() ?? "no geometry"}, {Schema.Count} attribute(s)]");
}
