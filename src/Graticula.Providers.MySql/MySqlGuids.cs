using System;
using System.Globalization;

namespace Graticula.Providers.MySql;

/// <summary>
/// A GlobalID column in a MySQL table — ADR-208, as <c>OracleGuids</c> is for Oracle: <c>CHAR(38)</c> braced as an Esri
/// geodatabase keeps one, <c>CHAR(36)</c> bare, or <c>BINARY(16)</c> as <c>UUID_TO_BIN</c> makes one.
/// </summary>
/// <remarks>
/// <b>A column named GLOBALID is the layer's GlobalID</b> when it is kept in one of these forms, and is compared in SQL as
/// ArcGIS spells one — <c>{1F0C…}</c>, braced and upper-case — whatever the storage. A BINARY(16)'s bytes are the GUID's
/// digits in the order they are written, which is what <c>UUID_TO_BIN(x)</c> without its swap flag stores.
/// </remarks>
internal static class MySqlGuids
{
    /// <summary>How a GlobalID column keeps its value.</summary>
    internal enum Storage
    {
        /// <summary><c>CHAR(38)</c>: <c>{1F0C…}</c>.</summary>
        Braced,

        /// <summary><c>CHAR(36)</c>: <c>1f0c…</c>.</summary>
        Bare,

        /// <summary><c>BINARY(16)</c>: sixteen bytes.</summary>
        Binary,

        /// <summary>MariaDB's own <c>UUID</c> type, which reads and takes the bare text form (ADR-209).</summary>
        Uuid,
    }

    /// <summary>The storage of a column named GLOBALID, or null when it is not kept as a GUID.</summary>
    internal static Storage? Of(string name, string dataType, long? length) =>
        !string.Equals(name, Graticula.Features.GlobalIds.Column, StringComparison.OrdinalIgnoreCase)
            ? null
            : dataType.ToLowerInvariant() switch
            {
                "binary" when length == 16 => Storage.Binary,
                "uuid" => Storage.Uuid,
                "char" or "varchar" when length == 38 => Storage.Braced,
                "char" or "varchar" when length == 36 => Storage.Bare,
                _ => null,
            };

    /// <summary>The column as ArcGIS's text, <c>{1F0C…}</c>, for a comparison.</summary>
    internal static string Text(string quoted, Storage storage) => storage switch
    {
        Storage.Braced => $"upper({quoted})",
        Storage.Bare => $"concat('{{', upper({quoted}), '}}')",
        Storage.Uuid => $"concat('{{', upper(cast({quoted} as char)), '}}')",
        _ => $"concat('{{', upper(insert(insert(insert(insert(hex({quoted}), 9, 0, '-'), 14, 0, '-'), 19, 0, '-'), 24, 0, '-')), '}}')",
    };

    /// <summary>A GUID as ArcGIS writes one, to compare with <see cref="Text"/>.</summary>
    internal static string Braced(Guid value) => Graticula.Features.GlobalIds.Braced(value);

    /// <summary>A GUID as its column keeps it, to bind.</summary>
    internal static object Bound(Guid value, Storage storage) => storage switch
    {
        Storage.Braced => Braced(value),
        Storage.Bare => value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
        Storage.Uuid => value.ToString("D", CultureInfo.InvariantCulture),
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
