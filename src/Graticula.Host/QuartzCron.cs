using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Host;

/// <summary>
/// A Quartz cron expression, as ArcGIS keeps a map area's <c>packageRefreshSchedule</c> — ADR-288 §11a, D-405: six fields
/// (seconds, minutes, hours, day of month, month, day of week) and an optional seventh (year), read in UTC.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here, not adopted.</b> The server has no scheduler library and a map area's refresh is the only thing that needs
/// one (the backup schedule is a single hour of the day). What is supported is what a schedule an ArcGIS client writes uses:
/// <c>*</c>, <c>?</c>, lists, ranges, steps, month and day names. <c>L</c>, <c>W</c> and <c>#</c> are refused when the
/// schedule is stored, rather than accepted and never run.
/// </para>
/// <para>
/// <b>Day of week counts Sunday as 1</b>, as Quartz does (<c>TUE</c> is 3). That is a different count from the
/// <c>dayOfWeek</c> of <c>mapAreaRefreshParams</c>, which counts Monday as 1 (read 2026-11-15, research note §5a.1): the
/// two are converted by <see cref="WeeklyParams"/> and <see cref="FromWeekly"/>.
/// </para>
/// <para>
/// When both day of month and day of week are restricted, a day must satisfy both; Quartz itself refuses that (one of the
/// two must be <c>?</c>), and so does <see cref="TryParse"/>.
/// </para>
/// </remarks>
internal sealed class QuartzCron
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] Days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _seconds;
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool[]? _years;
    private readonly bool _anyDayOfMonth;
    private readonly bool _anyDayOfWeek;

    private QuartzCron(
        string text, bool[] seconds, bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek, bool[]? years,
        bool anyDayOfMonth, bool anyDayOfWeek)
    {
        Text = text;
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _years = years;
        _anyDayOfMonth = anyDayOfMonth;
        _anyDayOfWeek = anyDayOfWeek;
    }

    /// <summary>The expression as it was given, trimmed.</summary>
    public string Text { get; }

    /// <summary>Reads an expression.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="cron">The schedule, when this returns true.</param>
    /// <param name="why">Why not, when it returns false.</param>
    /// <returns>Whether it is an expression this server runs.</returns>
    public static bool TryParse(string? text, out QuartzCron cron, out string? why)
    {
        cron = null!;
        why = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            why = "A schedule is a Quartz cron expression: seconds, minutes, hours, day of month, month, day of week, for example '0 30 2 ? * MON'.";
            return false;
        }

        string[] fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length is not (6 or 7))
        {
            why = $"A Quartz cron expression has six or seven fields and '{text.Trim()}' has {fields.Length}.";
            return false;
        }

        if (!Field(fields[0], 0, 59, null, "seconds", out bool[] seconds, out _, out why)
            || !Field(fields[1], 0, 59, null, "minutes", out bool[] minutes, out _, out why)
            || !Field(fields[2], 0, 23, null, "hours", out bool[] hours, out _, out why)
            || !Field(fields[3], 1, 31, null, "day of month", out bool[] dom, out bool anyDom, out why)
            || !Field(fields[4], 1, 12, Months, "month", out bool[] months, out _, out why)
            || !Field(fields[5], 1, 7, Days, "day of week", out bool[] dow, out bool anyDow, out why))
        {
            return false;
        }

        bool[]? years = null;

        if (fields.Length == 7 && !Field(fields[6], 1970, 2199, null, "year", out years, out _, out why))
        {
            return false;
        }

        if (anyDom == anyDow)
        {
            why = "One of day of month and day of week must be '?' and the other must name days, as Quartz requires.";
            return false;
        }

        cron = new QuartzCron(string.Join(' ', fields), seconds, minutes, hours, dom, months, dow, years, anyDom, anyDow);
        return true;
    }

    /// <summary>The first moment after <paramref name="after"/>, to the second, that the expression names; or null within eight years.</summary>
    /// <param name="after">The moment to start after (UTC).</param>
    /// <returns>The moment, or null.</returns>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        DateTime t = after.UtcDateTime;
        t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Utc).AddSeconds(1);
        DateTime limit = t.AddYears(8);

        while (t < limit)
        {
            if (_years is { } y && (t.Year >= y.Length || !y[t.Year]))
            {
                if (t.Year >= y.Length - 1)
                {
                    return null;
                }

                t = new DateTime(t.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                continue;
            }

            if (!_months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if (!_hours[t.Hour])
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                continue;
            }

            if (!_minutes[t.Minute])
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
                continue;
            }

            if (!_seconds[t.Second])
            {
                t = t.AddSeconds(1);
                continue;
            }

            return new DateTimeOffset(t, TimeSpan.Zero);
        }

        return null;
    }

    private bool DayMatches(DateTime t) =>
        _anyDayOfMonth
            ? _daysOfWeek[(int)t.DayOfWeek + 1]
            : _daysOfMonth[t.Day];

    /// <summary>
    /// The <c>mapAreaRefreshParams</c> a weekly schedule corresponds to, when the expression is one day a week at a fixed
    /// time; null for any other shape, which ArcGIS's own client could not have drawn.
    /// </summary>
    /// <param name="startDate">The <c>startDate</c> to state (the moment the schedule was set).</param>
    /// <returns>The object, or null.</returns>
    public object? WeeklyParams(DateTimeOffset startDate)
    {
        int[] days = [.. Enumerable.Range(1, 7).Where(d => _daysOfWeek[d])];

        if (!_anyDayOfMonth || days.Length != 1 || _years is not null || _months.Skip(1).Any(m => !m))
        {
            return null;
        }

        // Quartz counts Sunday as 1; the portal's dayOfWeek counts Monday as 1 (TUE is 3 and 2, read 2026-11-15).
        int portal = days[0] == 1 ? 7 : days[0] - 1;
        return new { startDate = startDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), type = "weekly", nthDay = 1, dayOfWeek = portal };
    }

    /// <summary>The cron of a weekly <c>mapAreaRefreshParams</c>: that day at the time of its <c>startDate</c>.</summary>
    /// <param name="startDate">The <c>startDate</c>.</param>
    /// <param name="dayOfWeek">The portal's <c>dayOfWeek</c>, Monday as 1.</param>
    /// <returns>The expression.</returns>
    public static string FromWeekly(DateTimeOffset startDate, int dayOfWeek)
    {
        DateTime t = startDate.UtcDateTime;
        int quartz = dayOfWeek % 7 + 1; // Monday 1 -> MON 2 ... Sunday 7 -> SUN 1
        return $"{t.Second} {t.Minute} {t.Hour} ? * {Days[quartz - 1]}";
    }

    private static bool Field(
        string text, int min, int max, string[]? names, string label, out bool[] allowed, out bool any, out string? why)
    {
        allowed = new bool[Math.Max(max + 1, 8)];
        any = text == "?";
        why = null;

        if (any)
        {
            return label is "day of month" or "day of week"
                ? true
                : Fail(label, "'?' is only for day of month and day of week.", out why);
        }

        foreach (string part in text.Split(','))
        {
            string range = part;
            int step = 1;
            int slash = part.IndexOf('/', StringComparison.Ordinal);

            if (slash >= 0)
            {
                range = part[..slash];

                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    return Fail(label, $"'{part}' has no usable step.", out why);
                }
            }

            int from, to;

            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else
            {
                int dash = range.IndexOf('-', StringComparison.Ordinal);

                if (dash > 0)
                {
                    if (!Value(range[..dash], min, max, names, out from) || !Value(range[(dash + 1)..], min, max, names, out to))
                    {
                        return Fail(label, $"'{part}' is not a range of {label} values this server runs.", out why);
                    }
                }
                else
                {
                    if (!Value(range, min, max, names, out from))
                    {
                        return Fail(label, $"'{part}' is not a {label} value this server runs (L, W and # are not supported).", out why);
                    }

                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                return Fail(label, $"'{part}' runs backwards.", out why);
            }

            for (int v = from; v <= to; v += step)
            {
                allowed[v] = true;
            }
        }

        return true;

        static bool Fail(string label, string message, out string why)
        {
            why = $"The {label} field: {message}";
            return false;
        }
    }

    private static bool Value(string text, int min, int max, string[]? names, out int value)
    {
        if (names is not null)
        {
            int index = Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                // Months are 1-12 from JAN; days are 1-7 from SUN.
                value = index + 1;
                return true;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }
}
