using System;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>
/// One published layer, as WMS sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter's own view, built at the edge.</b> Nothing here is a catalogue
/// type: the host reads the catalogue, applies sharing, and hands this in — the same
/// shape <c>WfsFeatureType</c> has, for the same reason (ADR-005 §3.3).
/// </para>
/// <para>
/// <b>The name is the layer's, unqualified.</b> A WMS <c>LAYERS</c> value is a flat
/// name and the folder is a title, which is the decision WFS arrived at the hard way
/// when the OGC conformance suite read a namespace prefix as a folder. Two surfaces
/// naming the same layer differently would be a third thing for an operator to hold
/// in their head.
/// </para>
/// </remarks>
/// <param name="Name">The layer name, unique across this server.</param>
/// <param name="Title">Something for a person to read in a layer list.</param>
/// <param name="Abstract">A longer description, or null.</param>
/// <param name="Srid">The EPSG code its geometry is stored in.</param>
/// <param name="GeometryType">What shape its features are.</param>
/// <param name="Extent">Where its features are, in its own CRS, or null when unknown.</param>
/// <param name="Geographic">The same extent in WGS 84, or null when unknown.</param>
/// <param name="Queryable">Whether <c>GetFeatureInfo</c> may ask about it.</param>
/// <param name="Time">Its time dimension, or null when it has none.</param>
public sealed record WmsLayer(
    string Name,
    string Title,
    string? Abstract,
    int Srid,
    GeometryKind GeometryType,
    Envelope? Extent,
    Envelope? Geographic,
    bool Queryable,
    TimeDimension? Time);

/// <summary>
/// A layer's time dimension: which column carries it, and what it spans.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the schema rather than declared, and that is a decision with a
/// cost.</b> A layer with exactly one <see cref="FieldType.Date"/> column gets a
/// time dimension on it; a layer with none or with several gets none. Nothing in
/// this server stores *which* column is the phenomenon time — adding that is a
/// migration, an admin endpoint and a console control — so the choice was between
/// deriving it, guessing between several, or not shipping WMS-T at all.
/// </para>
/// <para>
/// <b>Several date columns produce no dimension, deliberately.</b> A table with
/// <c>created_at</c> and <c>observed_at</c> has two answers and the server has no
/// way to prefer one; publishing the first would filter maps by the wrong column and
/// look like missing data. [Q-129](../../../docs/open-questions.md) is the declared
/// time field this should become.
/// </para>
/// </remarks>
/// <param name="Field">The column, as the provider spells it.</param>
/// <param name="From">The earliest value, or null when the layer is empty.</param>
/// <param name="Until">The latest value, or null when the layer is empty.</param>
public sealed record TimeDimension(string Field, DateTimeOffset? From, DateTimeOffset? Until)
{
    /// <summary>The dimension's name, which WMS fixes.</summary>
    public const string Name = "time";

    /// <summary>Its units, which ISO 8601 fixes.</summary>
    public const string Units = "ISO8601";

    /// <summary>The extent, as a capabilities document writes it.</summary>
    /// <remarks>
    /// <b>An interval rather than an enumeration.</b> Listing every distinct instant
    /// is what a WMS with a hundred frames does; a layer with a million timestamped
    /// rows would produce a capabilities document larger than the data.
    /// </remarks>
    public string ExtentText =>
        From is { } from && Until is { } until
            ? $"{from:yyyy-MM-ddTHH:mm:ssZ}/{until:yyyy-MM-ddTHH:mm:ssZ}/PT1S"
            : string.Empty;

    /// <summary>The value a client gets when it names no time.</summary>
    /// <remarks>
    /// <b>The latest, which is what <c>current</c> means and what a client expects
    /// from a layer it has not thought about.</b> Defaulting to the earliest would
    /// draw the oldest frame of an animation as the layer's ordinary appearance.
    /// </remarks>
    public string DefaultText =>
        Until is { } until ? $"{until:yyyy-MM-ddTHH:mm:ssZ}" : string.Empty;

    /// <summary>
    /// The one date column of a layer, when it has exactly one.
    /// </summary>
    /// <param name="fields">The layer's columns.</param>
    /// <returns>The column name, or null.</returns>
    public static string? FieldOf(IReadOnlyList<FieldDescription> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        string? found = null;

        foreach (FieldDescription field in fields)
        {
            if (field.Type != FieldType.Date)
            {
                continue;
            }

            if (found is not null)
            {
                // Two answers is no answer. See the type's remarks.
                return null;
            }

            found = field.Name;
        }

        return found;
    }
}
