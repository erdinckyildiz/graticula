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
/// look like missing data. **[Q-129](../../../docs/open-questions.md) answered that on
/// 2026-08-25 and the derivation stayed**: a layer may declare its time column
/// (migration 35), and where nothing is declared this is still what decides. So the
/// silence above is now the answer for a layer nobody has told, rather than the answer
/// for every layer with two dates.
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
    /// The declared time column if there is one and it holds, otherwise the derived one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-129](../../../docs/open-questions.md)'s answer, and the order matters.</b>
    /// A declaration wins because it is the only thing that can be right about a table
    /// with <c>created_at</c> and <c>observed_at</c>: the schema cannot say which one
    /// is when the thing happened, and nobody but the publisher knows.
    /// </para>
    /// <para>
    /// <b>Checked against the fields rather than trusted.</b> A registered table's
    /// schema drifts under us (A-023), so a column declared last month may be gone,
    /// or may have become text. Where the declaration no longer holds this falls back
    /// to the derivation, which is the answer the layer had before anything was
    /// declared. Refusing the request instead would take a layer off the air over a
    /// setting the person reading the error cannot see; the console shows the
    /// declaration and whether it is being honoured, which is where somebody can act.
    /// </para>
    /// <para>
    /// <b>Case-insensitive, because a column name typed by a person is.</b> PostgreSQL
    /// folds unquoted identifiers to lower case, so a publisher who typed
    /// <c>ObservedAt</c> into the console means the <c>observedat</c> the table has.
    /// </para>
    /// </remarks>
    /// <param name="fields">The layer's columns.</param>
    /// <param name="declared">The column the publisher named, or null.</param>
    /// <returns>The column name, or null.</returns>
    public static string? FieldOf(IReadOnlyList<FieldDescription> fields, string? declared = null)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (declared is { Length: > 0 })
        {
            foreach (FieldDescription field in fields)
            {
                if (field.Type == FieldType.Date
                    && string.Equals(field.Name, declared, StringComparison.OrdinalIgnoreCase))
                {
                    return field.Name;
                }
            }
        }

        string? found = null;

        foreach (FieldDescription field in fields)
        {
            if (field.Type != FieldType.Date)
            {
                continue;
            }

            if (found is not null)
            {
                // Two answers is no answer, and nobody declared one. See the remarks.
                return null;
            }

            found = field.Name;
        }

        return found;
    }

    /// <summary>
    /// Whether a declared column was found, and is a date, on this layer.
    /// </summary>
    /// <remarks>
    /// <b>So the console can say <em>declared and not honoured</em>.</b> A publisher
    /// who names a column that no longer exists gets the derivation back, which is the
    /// right thing for a map request and the wrong thing to leave unsaid on the page
    /// where the declaration was made.
    /// </remarks>
    /// <param name="fields">The layer's columns.</param>
    /// <param name="declared">The column the publisher named, or null.</param>
    /// <returns>Whether the declaration holds. Null declares nothing and holds.</returns>
    public static bool DeclarationHolds(
        IReadOnlyList<FieldDescription> fields, string? declared)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (declared is not { Length: > 0 })
        {
            return true;
        }

        foreach (FieldDescription field in fields)
        {
            if (field.Type == FieldType.Date
                && string.Equals(field.Name, declared, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
