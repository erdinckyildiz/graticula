using System;

namespace Graticula.Api.ArcGis;

/// <summary>Maps a CLR value type to an ArcGIS field type name.</summary>
/// <remarks>
/// <para>
/// ArcGIS has a fixed, small type vocabulary and a client will refuse a field
/// type it does not recognise. Anything we cannot map honestly becomes
/// <c>esriFieldTypeString</c> — which is a real narrowing, and preferable to
/// claiming a numeric type for something that is not one.
/// </para>
/// <para>
/// This is a v1 shape. Domains, subtypes and editor tracking are Q-58c and will
/// want more than a type name.
/// </para>
/// </remarks>
public static class ArcGisFieldType
{
    /// <summary>The ArcGIS field type for a runtime value type.</summary>
    /// <param name="type">
    /// The value's type, or <see langword="null"/> when a column's type is not
    /// known — every value seen so far was null, which happens on a narrow
    /// sample.
    /// </param>
    public static string For(Type? type) => type switch
    {
        null => "esriFieldTypeString",

        _ when type == typeof(int) || type == typeof(uint) => "esriFieldTypeInteger",
        _ when type == typeof(short) || type == typeof(ushort) || type == typeof(byte)
            => "esriFieldTypeSmallInteger",

        // No 64-bit integer in the classic vocabulary. Narrowing a bigint to
        // esriFieldTypeInteger would silently truncate an OSM id, so it goes out
        // as text — visibly lossy rather than invisibly wrong.
        _ when type == typeof(long) || type == typeof(ulong) => "esriFieldTypeString",

        _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal)
            => "esriFieldTypeDouble",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "esriFieldTypeDate",
        _ when type == typeof(Guid) => "esriFieldTypeGUID",
        _ when type == typeof(bool) => "esriFieldTypeSmallInteger",

        _ => "esriFieldTypeString",
    };
}
