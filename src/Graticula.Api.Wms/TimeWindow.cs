using System;
using System.Globalization;

namespace Graticula.Api.Wms;

/// <summary>
/// A <c>TIME</c> value: an instant, or an interval.
/// </summary>
/// <remarks>
/// <para>
/// <b>WMS-T, built in the same pass as WMS by owner instruction 2026-08-20</b> —
/// *"wms'i aradan çıkarırken wms-t'yi felan da aradan çıkart"*. It is a dimension
/// rather than a protocol: the same four operations, with one more parameter and one
/// more block in the capabilities document.
/// </para>
/// <para>
/// <b>An instant is read as the interval it names, not as an equality.</b>
/// <c>TIME=2026-08</c> means the whole of August; <c>TIME=2026-08-20</c> means that
/// day. ISO 8601 says a truncated timestamp denotes a period, and a server matching
/// it with <c>=</c> answers nothing for every client that sends a date rather than a
/// timestamp — which is most of them.
/// </para>
/// <para>
/// <b>Half-open, deliberately.</b> The window includes its start and excludes its
/// end, so consecutive days do not both match a midnight observation. WMS's own
/// interval syntax is written closed, and honouring that literally means a feature
/// at exactly midnight appears in two adjacent frames of an animation.
/// </para>
/// <para>
/// <b>Lists are refused rather than partly honoured.</b> WMS allows
/// <c>TIME=a,b,c</c> and a periodicity like <c>start/end/P1D</c>; both ask for
/// several maps and this surface draws one. Answering with the first value is a map
/// of a moment the client did not ask about, and it looks correct.
/// [Q-130](../../../docs/open-questions.md).
/// </para>
/// </remarks>
/// <param name="From">The first instant included, in UTC.</param>
/// <param name="Until">The first instant excluded, in UTC.</param>
public readonly record struct TimeWindow(DateTimeOffset From, DateTimeOffset Until)
{
    /// <summary>What the client wrote, for the response to echo back.</summary>
    public string? Text { get; private init; }

    /// <summary>Whether this window names a single instant rather than a period.</summary>
    public bool IsInstant => Until <= From;

    /// <summary>
    /// Reads a <c>TIME</c> parameter.
    /// </summary>
    /// <param name="value">The parameter.</param>
    /// <param name="window">The window.</param>
    /// <param name="why">Why not, when it did not parse.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? value, out TimeWindow window, out string? why)
    {
        window = default;
        why = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            why = "`TIME` was empty.";
            return false;
        }

        string text = value.Trim();

        if (text.Contains(',', StringComparison.Ordinal))
        {
            why = "`TIME` names several values. This server draws one map per request, so a list "
                + "is refused rather than answered with the first of them — a map of a moment the "
                + "client did not ask about looks exactly like the one they did.";

            return false;
        }

        string[] parts = text.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length > 2)
        {
            why = "`TIME` names a periodic interval. This server draws one map per request; ask "
                + "for a single instant or a single `start/end` interval.";

            return false;
        }

        if (parts.Length == 2)
        {
            if (!TryInstant(parts[0], out DateTimeOffset from, out _, out why)
                || !TryInstant(parts[1], out DateTimeOffset until, out DateTimeOffset untilEnd, out why))
            {
                return false;
            }

            // The end of an interval is inclusive of the period it names: 2026-08
            // as an end means through the end of August, not the first instant of it.
            DateTimeOffset close = untilEnd > until ? untilEnd : until;

            if (close <= from)
            {
                why = $"`TIME={text}` ends before it starts.";
                return false;
            }

            window = new TimeWindow(from, close) { Text = text };
            return true;
        }

        if (!TryInstant(parts[0], out DateTimeOffset instant, out DateTimeOffset end, out why))
        {
            return false;
        }

        window = new TimeWindow(instant, end) { Text = text };
        return true;
    }

    /// <summary>
    /// Reads one ISO 8601 value, and the period it denotes.
    /// </summary>
    /// <remarks>
    /// <b>The period is the point.</b> <c>2026</c> denotes a year and
    /// <c>2026-08-20T14:00:00Z</c> denotes a second; the difference is how much
    /// precision was written, which is ISO 8601's own rule and the reason a
    /// truncated value must not be compared with equality.
    /// </remarks>
    private static bool TryInstant(
        string text, out DateTimeOffset from, out DateTimeOffset until, out string? why)
    {
        from = default;
        until = default;
        why = null;

        if (string.Equals(text, "current", StringComparison.OrdinalIgnoreCase))
        {
            // `current` is WMS's own keyword for the newest data. Answered as the
            // open-ended window ending now, which is what a client asking for it
            // means and is the only reading that does not require knowing the data.
            from = DateTimeOffset.MinValue;
            until = DateTimeOffset.UtcNow;
            return true;
        }

        string[] formats =
        [
            "yyyy",
            "yyyy-MM",
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mmK",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.fffK",
            "yyyy-MM-ddTHH:mm:ss.fffffffK",
        ];

        for (int i = 0; i < formats.Length; i++)
        {
            if (!DateTimeOffset.TryParseExact(
                text,
                formats[i],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
            {
                continue;
            }

            from = parsed;

            until = i switch
            {
                0 => parsed.AddYears(1),
                1 => parsed.AddMonths(1),
                2 => parsed.AddDays(1),
                3 => parsed.AddMinutes(1),
                4 => parsed.AddSeconds(1),
                _ => parsed.AddTicks(1),
            };

            return true;
        }

        why = $"`{text}` is not an ISO 8601 instant. Write it as 2026-08-20, 2026-08-20T14:00:00Z, "
            + "or an interval `start/end`.";

        return false;
    }

    /// <summary>The window as ISO 8601, for a document to echo.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        Text ?? string.Create(
            CultureInfo.InvariantCulture, $"{From:yyyy-MM-ddTHH:mm:ssZ}/{Until:yyyy-MM-ddTHH:mm:ssZ}");
}
