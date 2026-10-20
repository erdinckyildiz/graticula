using System;
using System.Globalization;

namespace Graticula.Providers.Oracle;

/// <summary>
/// A GlobalID column in an Oracle table — ADR-205 — in the three ways one is kept: <c>CHAR(38)</c> braced, as an Esri
/// geodatabase in Oracle keeps it; <c>CHAR(36)</c> bare; and <c>RAW(16)</c>, as <c>SYS_GUID()</c> makes one.
/// </summary>
/// <remarks>
/// <b>A column named GLOBALID is the layer's GlobalID</b> (<c>GlobalIds.FieldOf</c>), as on a PostGIS layer, and only
/// when it is kept in one of these forms; a text column of another width keeps the name and is text. In SQL it is
/// compared as ArcGIS spells one — <c>{1F0C…}</c>, braced and upper-case — whatever the storage, so a where clause
/// written for ArcGIS finds it, and a RAW's bytes are its digits in the order they are written.
/// </remarks>
internal static class OracleGuids
{
    /// <summary>How a GlobalID column keeps its value.</summary>
    internal enum Storage
    {
        /// <summary><c>CHAR(38)</c>: <c>{1F0C…}</c>.</summary>
        Braced,

        /// <summary><c>CHAR(36)</c>: <c>1f0c…</c>.</summary>
        Bare,

        /// <summary><c>RAW(16)</c>: sixteen bytes.</summary>
        Raw,
    }

    /// <summary>The storage of a column named GLOBALID, or null when it is not kept as a GUID.</summary>
    internal static Storage? Of(string name, string dataType, int? dataLength, int? charLength)
    {
        if (!string.Equals(name, Graticula.Features.GlobalIds.Column, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return dataType.ToUpperInvariant() switch
        {
            "RAW" when dataLength == 16 => Storage.Raw,
            "CHAR" or "VARCHAR2" or "NCHAR" or "NVARCHAR2" when charLength == 38 => Storage.Braced,
            "CHAR" or "VARCHAR2" or "NCHAR" or "NVARCHAR2" when charLength == 36 => Storage.Bare,
            _ => null,
        };
    }

    /// <summary>The column as ArcGIS's text, <c>{1F0C…}</c>, for a comparison.</summary>
    internal static string Text(string quoted, Storage storage) => storage switch
    {
        Storage.Braced => $"upper({quoted})",
        Storage.Bare => $"('{{' || upper({quoted}) || '}}')",
        _ => $"('{{' || regexp_replace(rawtohex({quoted}), '^(.{{8}})(.{{4}})(.{{4}})(.{{4}})(.{{12}})$', '\\1-\\2-\\3-\\4-\\5') || '}}')",
    };

    /// <summary>A GUID as ArcGIS writes one, to compare with <see cref="Text"/>.</summary>
    internal static string Braced(Guid value) => Graticula.Features.GlobalIds.Braced(value);

    /// <summary>A GUID as its column keeps it, to bind.</summary>
    internal static object Bound(Guid value, Storage storage) => storage switch
    {
        Storage.Braced => Braced(value),
        Storage.Bare => value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
        _ => Convert.FromHexString(value.ToString("N", CultureInfo.InvariantCulture)),
    };

    /// <summary>A stored value as a GUID, or null when it is not one.</summary>
    internal static Guid? Read(object? value) => value switch
    {
        string text when Guid.TryParse(text.Trim(), out Guid parsed) => parsed,
        byte[] { Length: 16 } raw => Guid.ParseExact(Convert.ToHexString(raw), "N"),
        Guid id => id,
        _ => null,
    };
}
