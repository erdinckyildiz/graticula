using System;
using System.Globalization;
using System.Text;

namespace Graticula.Host.Forms.Reports;

/// <summary>
/// The <c>format</c>, <c>round</c>, <c>toFixed</c> and <c>useGrouping</c> methods of a report placeholder, and the
/// default forms of numbers and dates — ADR-301 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbers.</b> Survey123's <i>Number expressions</i> page gives four placeholder characters — <c>.</c> the decimal
/// separator, <c>,</c> group separators, <c>0</c> a required digit and <c>#</c> an optional one — which mean the same in
/// .NET's custom numeric formats, so the pattern is checked to hold only those (and literal text) and handed over.
/// </para>
/// <para>
/// <b>Dates.</b> The <i>Date and date-time expressions</i> page's table of tokens (<c>YYYY</c>, <c>MMM</c>, <c>Do</c>,
/// <c>HH</c>, <c>A</c>, <c>ZZ</c>, <c>X</c>, …) is read here token by token, longest first; text in square brackets is
/// written as it is. An empty pattern is ISO 8601, as the page says.
/// </para>
/// </remarks>
internal static class ReportFormat
{
    private static readonly string[] DateTokens =
    [
        "YYYY", "YY", "MMMM", "MMM", "MM", "M", "Do", "DD", "D", "dddd", "ddd", "HH", "H", "hh", "h", "mm", "m", "ss", "s",
        "SSS", "A", "a", "ZZ", "Z", "X", "x",
    ];

    /// <summary>Whether a number pattern holds only what Survey123's page documents; the refusal otherwise.</summary>
    public static string? NumberPatternProblem(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        foreach (char c in pattern)
        {
            if (c is '%' or '‰' or 'E' or 'e' or ';' or '\\' or '"' or '\'')
            {
                return $"The number format '{pattern}' uses '{c}', which Survey123's number format does not: it takes '#', '0', ',' and '.'.";
            }
        }

        return pattern.Contains('#', StringComparison.Ordinal) || pattern.Contains('0', StringComparison.Ordinal)
            ? null
            : $"The number format '{pattern}' has no digit in it: write '#' or '0' where the digits go, as in \"#,##0.00\".";
    }

    /// <summary>A number as <c>format:"#,##0.###"</c> writes it.</summary>
    public static string Number(double value, string pattern, CultureInfo culture) =>
        value.ToString(pattern, culture);

    /// <summary>A number as it is written with no format: as many digits as it has, grouped only when asked.</summary>
    public static string Number(double value, CultureInfo culture, bool grouping) =>
        grouping ? value.ToString("#,##0.###############", culture) : value.ToString("0.###############", culture);

    /// <summary>A date as <c>format:"…"</c> writes it, in the offset it is given in.</summary>
    public static string Date(DateTimeOffset value, string pattern, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }

        StringBuilder written = new();
        int i = 0;

        while (i < pattern.Length)
        {
            if (pattern[i] == '[')
            {
                int end = pattern.IndexOf(']', i + 1);

                if (end > i)
                {
                    written.Append(pattern, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
            }

            string? token = null;

            foreach (string candidate in DateTokens)
            {
                if (string.CompareOrdinal(pattern, i, candidate, 0, candidate.Length) == 0)
                {
                    token = candidate;
                    break;
                }
            }

            if (token is null)
            {
                written.Append(pattern[i]);
                i++;
                continue;
            }

            written.Append(Token(value, token, culture));
            i += token.Length;
        }

        return written.ToString();
    }

    private static string Token(DateTimeOffset value, string token, CultureInfo culture)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        int hour12 = value.Hour % 12 == 0 ? 12 : value.Hour % 12;
        TimeSpan offset = value.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        TimeSpan abs = offset.Duration();

        return token switch
        {
            "YYYY" => value.Year.ToString("0000", inv),
            "YY" => (value.Year % 100).ToString("00", inv),
            "MMMM" => culture.DateTimeFormat.GetMonthName(value.Month),
            "MMM" => culture.DateTimeFormat.GetAbbreviatedMonthName(value.Month),
            "MM" => value.Month.ToString("00", inv),
            "M" => value.Month.ToString(inv),
            "Do" => Ordinal(value.Day, culture),
            "DD" => value.Day.ToString("00", inv),
            "D" => value.Day.ToString(inv),
            "dddd" => culture.DateTimeFormat.GetDayName(value.DayOfWeek),
            "ddd" => culture.DateTimeFormat.GetAbbreviatedDayName(value.DayOfWeek),
            "HH" => value.Hour.ToString("00", inv),
            "H" => value.Hour.ToString(inv),
            "hh" => hour12.ToString("00", inv),
            "h" => hour12.ToString(inv),
            "mm" => value.Minute.ToString("00", inv),
            "m" => value.Minute.ToString(inv),
            "ss" => value.Second.ToString("00", inv),
            "s" => value.Second.ToString(inv),
            "SSS" => value.Millisecond.ToString("000", inv),
            "A" => value.Hour < 12 ? "AM" : "PM",
            "a" => value.Hour < 12 ? "am" : "pm",
            "ZZ" => $"{sign}{abs.Hours:00}{abs.Minutes:00}",
            "Z" => $"{sign}{abs.Hours:00}:{abs.Minutes:00}",
            "X" => value.ToUnixTimeSeconds().ToString(inv),
            "x" => value.ToUnixTimeMilliseconds().ToString(inv),
            _ => token,
        };
    }

    /// <summary>The day with its ordinal: English suffixes in English, a full stop where the language writes one.</summary>
    private static string Ordinal(int day, CultureInfo culture)
    {
        string number = day.ToString(CultureInfo.InvariantCulture);

        if (culture.TwoLetterISOLanguageName is not ("en" or "iv"))
        {
            return number + ".";
        }

        return day is 11 or 12 or 13 ? number + "th"
            : (day % 10) switch { 1 => number + "st", 2 => number + "nd", 3 => number + "rd", _ => number + "th" };
    }

    /// <summary>
    /// Reads a <c>utcOffset</c> argument: <c>+01:00</c>, <c>+0100</c> or <c>+01</c>, the three spellings the page gives.
    /// </summary>
    public static TimeSpan? Offset(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim();

        if (t.Length < 2 || t[0] is not ('+' or '-'))
        {
            return null;
        }

        string digits = t[1..].Replace(":", string.Empty, StringComparison.Ordinal);

        if (digits.Length is not (2 or 4) || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int both))
        {
            return null;
        }

        int hours = digits.Length == 2 ? both : both / 100;
        int minutes = digits.Length == 2 ? 0 : both % 100;

        if (hours > 14 || minutes > 59)
        {
            return null;
        }

        TimeSpan span = new(hours, minutes, 0);
        return t[0] == '-' ? -span : span;
    }

    /// <summary>A <c>locale</c> argument as a culture: lower case, as the page asks, and one .NET knows.</summary>
    public static CultureInfo? Culture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0 || name.Length > 20)
        {
            return null;
        }

        try
        {
            CultureInfo found = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return found;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
