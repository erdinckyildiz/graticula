using System;
using System.Collections.Generic;

namespace Graticula.Features;

/// <summary>
/// Which column of a layer is its ArcGIS GlobalID, and how one is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-013 §2 said hosted layers have a <c>globalid uuid</c> column, and none did.</b> Every
/// document said <c>globalIdField: ""</c> and every edit result said <c>globalId: null</c>, found by
/// a review from an ArcGIS user's side on 2026-09-15. Attachments, related records and replication
/// in ArcGIS clients are keyed by GlobalID precisely because an object id is not stable across
/// copies of the data, so a layer without one cannot take part in any of them.
/// </para>
/// <para>
/// <b>The column is recognised by its name and type</b> — a <c>uuid</c> column called
/// <c>globalid</c>, the name ArcGIS's own <i>Add GlobalIDs</i> gives it — rather than by a setting,
/// so a registered table that already carries one is recognised as it is, and the hosted layers this
/// server adds one to use the same name.
/// </para>
/// </remarks>
public static class GlobalIds
{
    /// <summary>The name a GlobalID column has.</summary>
    public const string Column = "globalid";

    /// <summary>The layer's GlobalID column, or null when it has none.</summary>
    /// <param name="fields">The layer's columns.</param>
    /// <returns>The column's name as the layer spells it, or null.</returns>
    public static string? FieldOf(IReadOnlyList<FieldDescription> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        foreach (FieldDescription field in fields)
        {
            if (field.Type == FieldType.Guid && string.Equals(field.Name, Column, StringComparison.OrdinalIgnoreCase))
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>A GUID as ArcGIS writes one: upper case, in braces.</summary>
    /// <param name="value">The GUID.</param>
    /// <returns>The text.</returns>
    public static string Braced(Guid value) => "{" + value.ToString("D").ToUpperInvariant() + "}";
}
