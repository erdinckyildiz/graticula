using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// How a mobile geodatabase spells the values it holds: dates as Julian days, GUIDs as upper-case braced text, numbers
/// in item XML as their shortest round-trip form — ADR-288, research note §1 and §3.
/// </summary>
internal static partial class FileValues
{
    /// <summary>The Julian day of 1970-01-01T00:00:00Z.</summary>
    public const double UnixEpochJulian = 2440587.5;

    /// <summary>
    /// The open end of an archived row, <c>julianday('9999-12-31 23:59:59')</c>, as a number: what the server's file
    /// holds in <c>gdb_to_date</c> for a row nobody has changed.
    /// </summary>
    public const double OpenEnd = 5373484.499988426;

    /// <summary>
    /// When a row the server wrote begins: the epoch plus one second, as the server's file has it (research note §4).
    /// Any moment before the device's first edit would do; this one is the one observed.
    /// </summary>
    public const double ServerRowStart = 2440587.500011574;

    /// <summary>The SQL expression of <see cref="OpenEnd"/>, as the archive views and triggers spell it.</summary>
    public const string OpenEndSql = "julianday ('9999-12-31 23:59:59')";

    /// <summary>A moment as a Julian day.</summary>
    public static double Julian(DateTimeOffset moment) => (moment.ToUnixTimeMilliseconds() / 86400000.0) + UnixEpochJulian;

    /// <summary>A Julian day as a moment, to the millisecond.</summary>
    public static DateTimeOffset FromJulian(double julian) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round((julian - UnixEpochJulian) * 86400000.0, MidpointRounding.AwayFromZero));

    /// <summary>A GUID as the file writes it: <c>{XXXXXXXX-…}</c>, upper case.</summary>
    public static string Guid(Guid value) => value.ToString("B").ToUpperInvariant();

    /// <summary>A fresh GUID as the file writes it.</summary>
    public static string NewGuid() => Guid(System.Guid.NewGuid());

    /// <summary>A number as item XML writes it: the shortest text that reads back as the same double.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>An integer as item XML writes it.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Text escaped for the item XML, as the server escapes it (apostrophes and quotes included).</summary>
    public static string Xml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\'' => "&apos;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// A table name as the file can carry it unquoted: anything but a letter, digit or underscore becomes an underscore,
    /// as ArcGIS names a table after a layer whose name has spaces.
    /// </summary>
    public static string TableName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (char.IsAsciiDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// A column name, refused unless it is a plain identifier. Field names reach the file's SQL unquoted, as ArcGIS writes
    /// them, so a name that is not one is an error rather than something to escape.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a plain identifier.</exception>
    public static string ColumnName(string name)
    {
        if (string.IsNullOrEmpty(name) || !Identifier().IsMatch(name))
        {
            throw new ArgumentException($"The field name '{name}' cannot be written to an offline copy: only letters, digits and underscores are.", nameof(name));
        }

        return name;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
