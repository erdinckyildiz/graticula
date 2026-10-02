using System;
using System.Globalization;
using System.Text;

namespace Graticula.Geometries;

/// <summary>
/// The two cell notations — ADR-146: GARS, the Global Area Reference System (30′ cells, 15′ quadrants, 5′ keypads), and
/// GEOREF, the World Geographic Reference System (15° quadrangles, 1° cells, then minutes). Both are angular grids on
/// longitude and latitude, so neither needs a projection.
/// </summary>
/// <remarks>
/// A string names a cell, not a point: read, it answers the cell's centre — the point that is nearest every point the
/// string can stand for.
/// </remarks>
public static partial class GeoCoordinateString
{
    /// <summary>GARS's latitude letters, and GEOREF's longitude quadrangle letters: A to Z without I and O.</summary>
    private const string TwentyFour = "ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>GEOREF's latitude quadrangle letters: A to M without I.</summary>
    private const string Twelve = "ABCDEFGHJKLM";

    /// <summary>GEOREF's one-degree letters: A to Q without I and O.</summary>
    private const string Fifteen = "ABCDEFGHJKLMNPQ";

    /// <summary>Writes a position as a GARS cell, to the 5′ keypad.</summary>
    private static string Gars(double longitude, double latitude)
    {
        // Minutes from the south-west corner of the world, the edges folded in so 180° E and 90° N have a cell.
        double x = Math.Clamp((longitude + 180) * 60, 0, (360 * 60) - 1e-9);
        double y = Math.Clamp((latitude + 90) * 60, 0, (180 * 60) - 1e-9);
        int column = (int)(x / 30), row = (int)(y / 30);
        int quadrantX = (int)(x % 30 / 15), quadrantY = (int)(y % 30 / 15);
        int keyX = (int)(x % 15 / 5), keyY = (int)(y % 15 / 5);
        int quadrant = (quadrantY == 1 ? 1 : 3) + quadrantX;
        int key = ((2 - keyY) * 3) + keyX + 1;

        return (column + 1).ToString("D3", CultureInfo.InvariantCulture)
            + TwentyFour[row / 24] + TwentyFour[row % 24]
            + quadrant.ToString(CultureInfo.InvariantCulture) + key.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a GARS cell — five characters, with a quadrant and a keypad optional — as its centre.</summary>
    private static bool TryReadGars(string text, out double longitude, out double latitude, out string? error)
    {
        longitude = 0;
        latitude = 0;
        error = "A GARS cell is three digits (001–720), two letters (AA–QZ, no I or O), and optionally a quadrant (1–4) "
            + "and a keypad (1–9): 006AG39.";
        string s = text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        if (s.Length is < 5 or > 7 || !int.TryParse(s.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int column)
            || column is < 1 or > 720)
        {
            return false;
        }

        int first = TwentyFour.IndexOf(s[3], StringComparison.Ordinal), second = TwentyFour.IndexOf(s[4], StringComparison.Ordinal);
        int row = (first * 24) + second;

        if (first < 0 || second < 0 || row >= 360)
        {
            return false;
        }

        double west = ((column - 1) * 30.0) - (180 * 60), south = (row * 30.0) - (90 * 60), size = 30;

        if (s.Length >= 6)
        {
            int quadrant = s[5] - '0';

            if (quadrant is < 1 or > 4)
            {
                return false;
            }

            west += (quadrant - 1) % 2 * 15;
            south += quadrant <= 2 ? 15 : 0;
            size = 15;
        }

        if (s.Length == 7)
        {
            int key = s[6] - '0';

            if (key is < 1 or > 9)
            {
                return false;
            }

            west += (key - 1) % 3 * 5;
            south += (2 - ((key - 1) / 3)) * 5;
            size = 5;
        }

        longitude = (west + (size / 2)) / 60;
        latitude = (south + (size / 2)) / 60;
        error = null;
        return true;
    }

    /// <summary>
    /// Writes a position as GEOREF: four letters, then minutes of longitude and of latitude, each with
    /// <paramref name="digits"/> figures — 2 for whole minutes, 3 for tenths, 4 for hundredths.
    /// </summary>
    private static string Georef(double longitude, double latitude, int digits)
    {
        double x = Math.Clamp(longitude + 180, 0, 360 - 1e-9);
        double y = Math.Clamp(latitude + 90, 0, 180 - 1e-9);
        int places = Math.Clamp(digits, 2, 4);
        double scale = Math.Pow(10, places - 2);

        StringBuilder text = new();
        text.Append(TwentyFour[(int)(x / 15)]).Append(Twelve[(int)(y / 15)]);
        text.Append(Fifteen[(int)(x % 15)]).Append(Fifteen[(int)(y % 15)]);

        // Truncated, not rounded: a cell is the square the point is in, and rounding up could name the next one.
        long minutesX = (long)Math.Floor(x % 1 * 60 * scale), minutesY = (long)Math.Floor(y % 1 * 60 * scale);
        text.Append(minutesX.ToString(new string('0', places), CultureInfo.InvariantCulture));
        text.Append(minutesY.ToString(new string('0', places), CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>Reads GEOREF — four letters and an even number of figures, or two letters alone — as its cell's centre.</summary>
    private static bool TryReadGeoref(string text, out double longitude, out double latitude, out string? error)
    {
        longitude = 0;
        latitude = 0;
        error = "A GEOREF string is two letters for the 15° quadrangle, two for the degree, then minutes of longitude and of "
            + "latitude with the same number of figures: MKPG1204.";
        string s = text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        if (s.Length < 2 || s.Length == 3 || (s.Length > 4 && (s.Length - 4) % 2 != 0) || s.Length > 12)
        {
            return false;
        }

        int qx = TwentyFour.IndexOf(s[0], StringComparison.Ordinal), qy = Twelve.IndexOf(s[1], StringComparison.Ordinal);

        if (qx < 0 || qy < 0)
        {
            return false;
        }

        double west = qx * 15, south = qy * 15, width = 15;

        if (s.Length >= 4)
        {
            int dx = Fifteen.IndexOf(s[2], StringComparison.Ordinal), dy = Fifteen.IndexOf(s[3], StringComparison.Ordinal);

            if (dx < 0 || dy < 0)
            {
                return false;
            }

            west += dx;
            south += dy;
            width = 1;
        }

        if (s.Length > 4)
        {
            int places = (s.Length - 4) / 2;
            double scale = Math.Pow(10, places - 2);

            if (!long.TryParse(s.AsSpan(4, places), NumberStyles.None, CultureInfo.InvariantCulture, out long mx)
                || !long.TryParse(s.AsSpan(4 + places, places), NumberStyles.None, CultureInfo.InvariantCulture, out long my)
                || mx / scale >= 60 || my / scale >= 60)
            {
                return false;
            }

            width = 1 / (60 * scale);
            west += mx / scale / 60;
            south += my / scale / 60;
        }

        longitude = west + (width / 2) - 180;
        latitude = south + (width / 2) - 90;
        error = null;
        return true;
    }
}
