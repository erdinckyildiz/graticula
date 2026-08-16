using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GisServer.Geometries;

/// <summary>How a longitude and latitude are written as text.</summary>
public enum GeoCoordinateNotation
{
    /// <summary>Decimal degrees: <c>32.5N 45.25W</c>.</summary>
    DecimalDegrees,

    /// <summary>Degrees and decimal minutes: <c>32 30.0N 45 15.0W</c>.</summary>
    DegreesDecimalMinutes,

    /// <summary>Degrees, minutes and seconds: <c>32 30 0.0N 45 15 0.0W</c>.</summary>
    DegreesMinutesSeconds,

    /// <summary>Zone, hemisphere, easting and northing: <c>30N 500000 3600000</c>.</summary>
    Utm,

    /// <summary>The military grid: <c>30N XA 12345 67890</c> without spaces.</summary>
    Mgrs,

    /// <summary>
    /// The United States National Grid, which is MGRS on a WGS84-family datum.
    /// </summary>
    Usng,
}

/// <summary>
/// Longitude and latitude written as a grid or sexagesimal string, and read back.
/// </summary>
/// <remarks>
/// <para>
/// <b>These were not on any list until 2026-08-15.</b> The owner put a real
/// ArcGIS GeometryServer beside this one, and <c>toGeoCoordinateString</c> and
/// <c>fromGeoCoordinateString</c> were absent from both the supported operations
/// and the refusals — a caller asking for them got 404, which says the operation
/// does not exist rather than that we had not written it.
/// </para>
/// <para>
/// <b>In process, and not because it is cheap.</b> The rule from ADR-022 §4b is
/// that work goes to the datastore when the data is already there. Nothing here
/// touches stored data: the input is a coordinate pair in the request. UTM is a
/// closed-form series and MGRS is a lettering scheme over it, so this is
/// arithmetic on two doubles — a round trip would cost more than the whole
/// conversion.
/// </para>
/// <para>
/// <b>Everything here assumes a WGS84-shaped ellipsoid</b> (GRS80 and WGS84
/// differ in flattening by about 0.1 mm at the equator, which is why USNG and
/// MGRS produce the same string). A caller sending coordinates in another
/// reference must project first — which is what the endpoint does, through the
/// datastore's PROJ, before calling any of this.
/// </para>
/// <para>
/// <b>The polar regions are refused rather than approximated.</b> Above 84°N and
/// below 80°S, MGRS switches from UTM to the Universal Polar Stereographic grid,
/// which is a different projection with its own lettering. Emitting a UTM-based
/// string there would be silently wrong, and a wrong grid reference is the kind
/// of error that is only discovered by someone standing in the wrong place.
/// </para>
/// </remarks>
public static class GeoCoordinateString
{
    // WGS84.
    private const double A = 6_378_137.0;
    private const double F = 1.0 / 298.257223563;
    private const double K0 = 0.9996;

    private const double FalseEasting = 500_000.0;
    private const double FalseNorthing = 10_000_000.0;

    /// <summary>The northernmost latitude UTM covers.</summary>
    public const double MaximumLatitude = 84.0;

    /// <summary>The southernmost latitude UTM covers.</summary>
    public const double MinimumLatitude = -80.0;

    /// <summary>
    /// The latitude band letters, from 80°S upward in eight-degree steps.
    /// </summary>
    /// <remarks>
    /// <b>I and O are absent, and X is twelve degrees rather than eight.</b> The
    /// two letters are omitted throughout MGRS because they are confusable with
    /// one and zero; X is stretched so the scheme reaches 84°N without a
    /// twenty-first band.
    /// </remarks>
    private const string Bands = "CDEFGHJKLMNPQRSTUVWX";

    private const string ColumnLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string RowLetters = "ABCDEFGHJKLMNPQRSTUV";

    /// <summary>The UTM zone, easting and northing for a coordinate.</summary>
    /// <param name="longitude">Longitude in degrees.</param>
    /// <param name="latitude">Latitude in degrees.</param>
    /// <param name="zone">The UTM zone, 1 to 60.</param>
    /// <param name="easting">Metres east, including the 500,000 false easting.</param>
    /// <param name="northing">
    /// Metres north, including the ten million metre false northing in the
    /// southern hemisphere — the same frame EPSG:327nn uses.
    /// </param>
    /// <param name="error">Why not, when it is outside the grid.</param>
    /// <returns>Whether the coordinate is on the UTM grid at all.</returns>
    /// <remarks>
    /// <b>Public because the numbers are worth having unrounded.</b> The UTM
    /// string is whole metres, which is what ArcGIS emits — and a test that can
    /// only see the string cannot tell a correct series from one that is
    /// forty centimetres out, because the rounding hides exactly that much. The
    /// first version of this file's test compared formatted strings against
    /// PROJ and reported half-metre disagreements that were entirely its own
    /// rounding.
    /// </remarks>
    public static bool TryToUtm(
        double longitude,
        double latitude,
        out int zone,
        out double easting,
        out double northing,
        out string? error)
    {
        zone = 0;
        easting = 0;
        northing = 0;
        error = null;

        if (double.IsNaN(longitude) || double.IsNaN(latitude)
            || double.IsInfinity(longitude) || double.IsInfinity(latitude))
        {
            error = "The coordinate is not a number.";
            return false;
        }

        if (latitude > MaximumLatitude || latitude < MinimumLatitude)
        {
            error =
                $"Latitude {latitude:0.####} is outside the UTM grid, which runs from "
                + $"{MinimumLatitude:0}° to {MaximumLatitude:0}°.";
            return false;
        }

        (zone, easting, northing) = ToUtm(Wrap(longitude), latitude);

        return true;
    }

    /// <summary>Writes a coordinate as text.</summary>
    /// <param name="longitude">Longitude in degrees.</param>
    /// <param name="latitude">Latitude in degrees.</param>
    /// <param name="notation">Which notation.</param>
    /// <param name="digits">
    /// Digits after the decimal point for the angular notations, or digits per
    /// axis for the grid notations. Five gives one-metre MGRS precision.
    /// </param>
    /// <param name="spaces">Whether to separate the parts with spaces.</param>
    /// <param name="text">The string.</param>
    /// <param name="error">Why not, when it could not be written.</param>
    /// <returns>Whether it was written.</returns>
    public static bool TryWrite(
        double longitude,
        double latitude,
        GeoCoordinateNotation notation,
        int digits,
        bool spaces,
        out string text,
        out string? error)
    {
        text = string.Empty;
        error = null;

        if (double.IsNaN(longitude) || double.IsNaN(latitude)
            || double.IsInfinity(longitude) || double.IsInfinity(latitude))
        {
            error = "The coordinate is not a number.";
            return false;
        }

        if (latitude is < -90 or > 90)
        {
            error = $"Latitude {latitude} is outside -90 to 90.";
            return false;
        }

        longitude = Wrap(longitude);

        // <b>The angular notations keep their separators whatever was asked.</b>
        // Running "39 56 0.24N" together gives "39560.2400N", which nothing can
        // read back — the parts are variable width, so no rule recovers
        // them. Honouring the flag there would mean emitting a string this
        // server's own reader rejects. The flag is for the grid notations, where
        // the packed form is standard and fixed width.
        switch (notation)
        {
            case GeoCoordinateNotation.DecimalDegrees:
                text = Angle(latitude, "NS", digits, 1, true) + " "
                     + Angle(longitude, "EW", digits, 1, true);
                return true;

            case GeoCoordinateNotation.DegreesDecimalMinutes:
                text = Angle(latitude, "NS", digits, 2, true) + " "
                     + Angle(longitude, "EW", digits, 2, true);
                return true;

            case GeoCoordinateNotation.DegreesMinutesSeconds:
                text = Angle(latitude, "NS", digits, 3, true) + " "
                     + Angle(longitude, "EW", digits, 3, true);
                return true;

            case GeoCoordinateNotation.Utm:
            case GeoCoordinateNotation.Mgrs:
            case GeoCoordinateNotation.Usng:
                break;

            default:
                error = $"'{notation}' is not a notation this server writes.";
                return false;
        }

        if (latitude > MaximumLatitude || latitude < MinimumLatitude)
        {
            error =
                $"Latitude {latitude:0.####} is outside the UTM grid, which runs from "
                + $"{MinimumLatitude:0}° to {MaximumLatitude:0}°. The polar regions use "
                + "the Universal Polar Stereographic grid, a different projection with its own "
                + "lettering, and this server does not implement it — a UTM-based string "
                + "there would be silently wrong.";
            return false;
        }

        (int zone, double easting, double northing) = ToUtm(longitude, latitude);

        if (notation == GeoCoordinateNotation.Utm)
        {
            string hemisphere = latitude >= 0 ? "N" : "S";
            string gap = spaces ? " " : string.Empty;

            // <b>Padded to six and seven, so the packed form can be split.</b>
            // A northing of 42 metres just north of the equator writes as "42",
            // and "31N50000042" has no rule that recovers the two numbers. The
            // widths are the grid's own: eastings never reach seven digits and
            // northings never eight.
            text = zone.ToString(CultureInfo.InvariantCulture) + hemisphere + gap
                 + easting.ToString("F0", CultureInfo.InvariantCulture).PadLeft(6, '0') + gap
                 + northing.ToString("F0", CultureInfo.InvariantCulture).PadLeft(7, '0');

            return true;
        }

        return TryMgrs(zone, easting, northing, latitude, digits, spaces, out text, out error);
    }

    /// <summary>Reads a coordinate from text.</summary>
    /// <param name="text">The string.</param>
    /// <param name="notation">Which notation it is in.</param>
    /// <param name="longitude">Longitude in degrees.</param>
    /// <param name="latitude">Latitude in degrees.</param>
    /// <param name="error">Why not, when it could not be read.</param>
    /// <returns>Whether it was read.</returns>
    /// <remarks>
    /// <b>The notation is given rather than guessed.</b> "32 30 45" is a valid
    /// DMS latitude and a valid pair of UTM numbers, and a reader that decides
    /// for itself will one day decide wrongly on somebody's survey data.
    /// </remarks>
    public static bool TryRead(
        string text,
        GeoCoordinateNotation notation,
        out double longitude,
        out double latitude,
        out string? error)
    {
        longitude = 0;
        latitude = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The string is empty.";
            return false;
        }

        return notation switch
        {
            GeoCoordinateNotation.Mgrs or GeoCoordinateNotation.Usng =>
                TryReadMgrs(text.Trim(), out longitude, out latitude, out error),
            GeoCoordinateNotation.Utm =>
                TryReadUtm(text.Trim(), out longitude, out latitude, out error),
            _ => TryReadAngles(text.Trim(), out longitude, out latitude, out error),
        };
    }

    // ---------- angular notations ----------

    private static double Wrap(double longitude)
    {
        // -180 stays -180 rather than becoming 180: both are the same meridian
        // and the sign is what a caller sent.
        while (longitude > 180)
        {
            longitude -= 360;
        }

        while (longitude < -180)
        {
            longitude += 360;
        }

        return longitude;
    }

    /// <summary>One angle in one, two or three sexagesimal parts.</summary>
    /// <remarks>
    /// <b>The rounding is done once, at the smallest part.</b> Formatting each
    /// part separately lets 59.9999 seconds print as "60", which is a minute
    /// that does not exist — the classic bug in every hand-written DMS
    /// formatter. Here the value is rounded to the output precision first and
    /// the parts are taken from the rounded number.
    /// </remarks>
    private static string Angle(double value, string signs, int digits, int parts, bool spaces)
    {
        char sign = value < 0 ? signs[1] : signs[0];
        double magnitude = Math.Abs(value);

        digits = Math.Clamp(digits, 0, 8);

        string gap = spaces ? " " : string.Empty;

        if (parts == 1)
        {
            return magnitude.ToString("F" + digits, CultureInfo.InvariantCulture) + gap + sign;
        }

        double scale = parts == 2 ? 60.0 : 3600.0;

        // Round in the smallest unit, then carry upward.
        double smallest = Math.Round(magnitude * scale, digits, MidpointRounding.AwayFromZero);

        int degrees = (int)(smallest / scale);
        double remainder = smallest - (degrees * scale);

        if (parts == 2)
        {
            return degrees.ToString(CultureInfo.InvariantCulture) + gap
                 + remainder.ToString("F" + digits, CultureInfo.InvariantCulture) + gap + sign;
        }

        int minutes = (int)(remainder / 60);
        double seconds = remainder - (minutes * 60);

        return degrees.ToString(CultureInfo.InvariantCulture) + gap
             + minutes.ToString(CultureInfo.InvariantCulture) + gap
             + seconds.ToString("F" + digits, CultureInfo.InvariantCulture) + gap + sign;
    }

    /// <summary>
    /// Reads a latitude and longitude written in any of the angular notations.
    /// </summary>
    /// <remarks>
    /// <b>One reader for all three, because the difference is only how many
    /// numbers precede the hemisphere letter.</b> Splitting them into three
    /// readers would mean three places to get the sign handling wrong.
    /// </remarks>
    private static bool TryReadAngles(
        string text, out double longitude, out double latitude, out string? error)
    {
        longitude = 0;
        latitude = 0;
        error = null;

        // Everything that is not a number, a sign or a hemisphere letter is a
        // separator: degree marks, primes, quotes, commas, spaces.
        List<double> numbers = [];
        List<char> hemispheres = [];

        int index = 0;

        while (index < text.Length)
        {
            char c = text[index];

            if (char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int start = index;

                while (index < text.Length
                       && (char.IsAsciiDigit(text[index]) || text[index] is '.' or '-' or '+'))
                {
                    index++;
                }

                if (!double.TryParse(
                        text.AsSpan(start, index - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double number))
                {
                    error = $"'{text[start..index]}' is not a number.";
                    return false;
                }

                numbers.Add(number);
                continue;
            }

            char upper = char.ToUpperInvariant(c);

            if (upper is 'N' or 'S' or 'E' or 'W')
            {
                hemispheres.Add(upper);
            }

            index++;
        }

        if (hemispheres.Count != 2)
        {
            error =
                "Two hemisphere letters are required, one for latitude and one for longitude "
                + $"— found {hemispheres.Count}. Without them the string is ambiguous: "
                + "\"45 30\" is a different place north of the equator and south of it.";
            return false;
        }

        if (numbers.Count % 2 != 0 || numbers.Count is < 2 or > 6)
        {
            error =
                $"Expected an even count of two, four or six numbers and found {numbers.Count}: "
                + "degrees, degrees and minutes, or degrees, minutes and seconds, for each of "
                + "latitude and longitude.";
            return false;
        }

        int per = numbers.Count / 2;

        double first = Combine(numbers, 0, per);
        double second = Combine(numbers, per, per);

        // <b>The order follows the hemisphere letters, not the position.</b>
        // "W45 N32" is unusual and unambiguous, and reading it positionally
        // would put a longitude in a latitude.
        bool firstIsLatitude = hemispheres[0] is 'N' or 'S';

        latitude = firstIsLatitude ? first : second;
        longitude = firstIsLatitude ? second : first;

        char latitudeSign = firstIsLatitude ? hemispheres[0] : hemispheres[1];
        char longitudeSign = firstIsLatitude ? hemispheres[1] : hemispheres[0];

        if (latitudeSign is not ('N' or 'S') || longitudeSign is not ('E' or 'W'))
        {
            error =
                $"'{latitudeSign}' and '{longitudeSign}' are not one latitude and one longitude "
                + "hemisphere. Expected one of N or S with one of E or W.";
            return false;
        }

        if (latitudeSign == 'S')
        {
            latitude = -latitude;
        }

        if (longitudeSign == 'W')
        {
            longitude = -longitude;
        }

        if (Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
        {
            error = $"{latitude:0.####}, {longitude:0.####} is not on the earth.";
            return false;
        }

        return true;
    }

    private static double Combine(List<double> numbers, int start, int count)
    {
        double value = Math.Abs(numbers[start]);

        if (count > 1)
        {
            value += Math.Abs(numbers[start + 1]) / 60.0;
        }

        if (count > 2)
        {
            value += Math.Abs(numbers[start + 2]) / 3600.0;
        }

        return value;
    }

    // ---------- UTM ----------

    /// <summary>The zone a longitude falls in, with Norway and Svalbard.</summary>
    /// <remarks>
    /// <b>Two exceptions, and they are in the standard rather than in folklore.</b>
    /// Zone 32 is widened westward over southern Norway so Bergen is not split,
    /// and zones 31 to 37 are rearranged over Svalbard. A converter without them
    /// puts real places in the wrong zone, which is a different grid square and
    /// therefore a different string.
    /// </remarks>
    private static int ZoneOf(double longitude, double latitude)
    {
        int zone = (int)Math.Floor((longitude + 180) / 6) + 1;

        if (zone > 60)
        {
            zone = 60;
        }

        if (latitude is >= 56 and < 64 && longitude is >= 3 and < 12)
        {
            return 32;
        }

        if (latitude >= 72 && latitude < 84)
        {
            if (longitude is >= 0 and < 9)
            {
                return 31;
            }

            if (longitude is >= 9 and < 21)
            {
                return 33;
            }

            if (longitude is >= 21 and < 33)
            {
                return 35;
            }

            if (longitude is >= 33 and < 42)
            {
                return 37;
            }
        }

        return zone;
    }

    /// <summary>Transverse Mercator forward, to the sixth order.</summary>
    /// <remarks>
    /// <b>The series, not an iteration.</b> Truncated at the sixth power of the
    /// eccentricity, the error is under a millimetre anywhere inside a UTM zone
    /// — which is two orders below the one-metre precision MGRS's five-digit
    /// form can express, so nothing downstream can see it.
    /// </remarks>
    private static (int Zone, double Easting, double Northing) ToUtm(
        double longitude, double latitude)
    {
        int zone = ZoneOf(longitude, latitude);

        double centralMeridian = ((zone - 1) * 6) - 180 + 3;

        double phi = latitude * Math.PI / 180;
        double lambda = (longitude - centralMeridian) * Math.PI / 180;

        double e2 = F * (2 - F);
        double ep2 = e2 / (1 - e2);

        double n = A / Math.Sqrt(1 - (e2 * Math.Sin(phi) * Math.Sin(phi)));
        double t = Math.Tan(phi) * Math.Tan(phi);
        double c = ep2 * Math.Cos(phi) * Math.Cos(phi);
        double a1 = Math.Cos(phi) * lambda;

        double m = A * (
            ((1 - (e2 / 4) - (3 * e2 * e2 / 64) - (5 * e2 * e2 * e2 / 256)) * phi)
            - (((3 * e2 / 8) + (3 * e2 * e2 / 32) + (45 * e2 * e2 * e2 / 1024)) * Math.Sin(2 * phi))
            + (((15 * e2 * e2 / 256) + (45 * e2 * e2 * e2 / 1024)) * Math.Sin(4 * phi))
            - ((35 * e2 * e2 * e2 / 3072) * Math.Sin(6 * phi)));

        double easting = (K0 * n * (
            a1
            + ((1 - t + c) * a1 * a1 * a1 / 6)
            + ((5 - (18 * t) + (t * t) + (72 * c) - (58 * ep2)) * Math.Pow(a1, 5) / 120)))
            + FalseEasting;

        double northing = K0 * (m + (n * Math.Tan(phi) * (
            (a1 * a1 / 2)
            + ((5 - t + (9 * c) + (4 * c * c)) * Math.Pow(a1, 4) / 24)
            + ((61 - (58 * t) + (t * t) + (600 * c) - (330 * ep2)) * Math.Pow(a1, 6) / 720))));

        if (latitude < 0)
        {
            northing += FalseNorthing;
        }

        return (zone, easting, northing);
    }

    /// <summary>Transverse Mercator inverse.</summary>
    private static (double Longitude, double Latitude) FromUtm(
        int zone, double easting, double northing, bool north)
    {
        double centralMeridian = ((zone - 1) * 6) - 180 + 3;

        double x = easting - FalseEasting;
        double y = north ? northing : northing - FalseNorthing;

        double e2 = F * (2 - F);
        double ep2 = e2 / (1 - e2);
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));

        double m = y / K0;
        double mu = m / (A * (1 - (e2 / 4) - (3 * e2 * e2 / 64) - (5 * e2 * e2 * e2 / 256)));

        double phi1 = mu
            + (((3 * e1 / 2) - (27 * e1 * e1 * e1 / 32)) * Math.Sin(2 * mu))
            + (((21 * e1 * e1 / 16) - (55 * Math.Pow(e1, 4) / 32)) * Math.Sin(4 * mu))
            + ((151 * e1 * e1 * e1 / 96) * Math.Sin(6 * mu))
            + ((1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu));

        double c1 = ep2 * Math.Cos(phi1) * Math.Cos(phi1);
        double t1 = Math.Tan(phi1) * Math.Tan(phi1);
        double n1 = A / Math.Sqrt(1 - (e2 * Math.Sin(phi1) * Math.Sin(phi1)));
        double r1 = A * (1 - e2) / Math.Pow(1 - (e2 * Math.Sin(phi1) * Math.Sin(phi1)), 1.5);
        double d = x / (n1 * K0);

        double latitude = phi1 - (n1 * Math.Tan(phi1) / r1 * (
            (d * d / 2)
            - ((5 + (3 * t1) + (10 * c1) - (4 * c1 * c1) - (9 * ep2)) * Math.Pow(d, 4) / 24)
            + ((61 + (90 * t1) + (298 * c1) + (45 * t1 * t1) - (252 * ep2) - (3 * c1 * c1))
               * Math.Pow(d, 6) / 720)));

        double longitude = (d
            - ((1 + (2 * t1) + c1) * d * d * d / 6)
            + ((5 - (2 * c1) + (28 * t1) - (3 * c1 * c1) + (8 * ep2) + (24 * t1 * t1))
               * Math.Pow(d, 5) / 120)) / Math.Cos(phi1);

        return (centralMeridian + (longitude * 180 / Math.PI), latitude * 180 / Math.PI);
    }

    private static bool TryReadUtm(
        string text, out double longitude, out double latitude, out string? error)
    {
        longitude = 0;
        latitude = 0;

        string[] parts = text.Split(
            [' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // <b>The packed form is split by width, which is why the writer pads.</b>
        // Zone and hemisphere, then exactly six digits of easting and seven of
        // northing. Without this, a string this server wrote with addSpaces
        // false could not be read by the operation next door.
        if (parts.Length == 1)
        {
            string packed = parts[0];
            int letter = -1;

            for (int i = 0; i < packed.Length; i++)
            {
                if (!char.IsAsciiDigit(packed[i]))
                {
                    letter = i;
                    break;
                }
            }

            if (letter < 1 || packed.Length != letter + 14)
            {
                error =
                    $"'{text}' is not a packed UTM string. Packed, it is the zone, the "
                    + "hemisphere letter, six digits of easting and seven of northing \u2014 "
                    + "for example '35N0500000004500000'.";
                return false;
            }

            parts =
            [
                packed[..(letter + 1)],
                packed.Substring(letter + 1, 6),
                packed[(letter + 7)..],
            ];
        }

        if (parts.Length != 3)
        {
            error =
                "A UTM string is three parts: zone with hemisphere, easting, northing — "
                + $"for example '35N 500000 4500000'. Found {parts.Length}.";
            return false;
        }

        string zoneText = parts[0];
        char hemisphere = char.ToUpperInvariant(zoneText[^1]);

        if (hemisphere is not ('N' or 'S'))
        {
            error = $"'{parts[0]}' does not end in a hemisphere letter, N or S.";
            return false;
        }

        if (!int.TryParse(
                zoneText.AsSpan(0, zoneText.Length - 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int zone)
            || zone is < 1 or > 60)
        {
            error = $"'{zoneText[..^1]}' is not a UTM zone; they run from 1 to 60.";
            return false;
        }

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                out double easting)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                out double northing))
        {
            error = "The easting and northing must be numbers.";
            return false;
        }

        error = null;

        (longitude, latitude) = FromUtm(zone, easting, northing, hemisphere == 'N');

        return true;
    }

    // ---------- MGRS ----------

    private static char BandOf(double latitude)
    {
        if (latitude >= 84)
        {
            return 'X';
        }

        int index = (int)Math.Floor((latitude + 80) / 8);

        return Bands[Math.Clamp(index, 0, Bands.Length - 1)];
    }

    /// <summary>
    /// The hundred-kilometre square letters for a UTM position.
    /// </summary>
    /// <remarks>
    /// <b>Columns repeat every three zones and rows every two, which is the
    /// whole trick.</b> A zone is eight columns wide (A–Z without I and O, taken
    /// eight at a time), so zones 1, 4, 7 share a column set. Rows run A–V
    /// without I and O, and even-numbered zones start five letters further on —
    /// that offset is what stops two adjacent zones showing the same pair at the
    /// same latitude.
    /// </remarks>
    private static (char Column, char Row) SquareOf(int zone, double easting, double northing)
    {
        int set = ((zone - 1) % 3) * 8;
        int column = (int)Math.Floor(easting / 100_000) - 1;

        char columnLetter = ColumnLetters[set + Math.Clamp(column, 0, 7)];

        double reduced = northing % 2_000_000;
        int row = (int)Math.Floor(reduced / 100_000);

        // Even zones are offset by five letters.
        if (zone % 2 == 0)
        {
            row += 5;
        }

        return (columnLetter, RowLetters[((row % 20) + 20) % 20]);
    }

    private static bool TryMgrs(
        int zone,
        double easting,
        double northing,
        double latitude,
        int digits,
        bool spaces,
        out string text,
        out string? error)
    {
        text = string.Empty;
        error = null;

        digits = Math.Clamp(digits, 1, 5);

        (char column, char row) = SquareOf(zone, easting, northing);

        double withinEasting = easting % 100_000;
        double withinNorthing = northing % 100_000;

        // <b>Truncated, not rounded.</b> A grid reference names the square a
        // point is in; rounding 99,999 up to 100,000 names the square next door.
        double scale = Math.Pow(10, 5 - digits);

        long e = (long)Math.Floor(withinEasting / scale);
        long n = (long)Math.Floor(withinNorthing / scale);

        string gap = spaces ? " " : string.Empty;

        StringBuilder builder = new();

        builder.Append(zone.ToString(CultureInfo.InvariantCulture));
        builder.Append(BandOf(latitude));
        builder.Append(gap);
        builder.Append(column);
        builder.Append(row);
        builder.Append(gap);
        builder.Append(e.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'));
        builder.Append(gap);
        builder.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'));

        text = builder.ToString();

        return true;
    }

    private static bool TryReadMgrs(
        string text, out double longitude, out double latitude, out string? error)
    {
        longitude = 0;
        latitude = 0;
        error = null;

        string compact = text.Replace(" ", string.Empty, StringComparison.Ordinal)
                             .Replace("\t", string.Empty, StringComparison.Ordinal)
                             .ToUpperInvariant();

        int digitsEnd = 0;

        while (digitsEnd < compact.Length && char.IsAsciiDigit(compact[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd is 0 or > 2 || compact.Length < digitsEnd + 3)
        {
            error =
                $"'{text}' is not an MGRS reference. It is a zone number, a band letter, two "
                + "square letters and an even count of digits — for example '35TPF1234567890'.";
            return false;
        }

        int zone = int.Parse(compact[..digitsEnd], CultureInfo.InvariantCulture);

        if (zone is < 1 or > 60)
        {
            error = $"'{compact[..digitsEnd]}' is not a UTM zone; they run from 1 to 60.";
            return false;
        }

        char band = compact[digitsEnd];
        char column = compact[digitsEnd + 1];
        char row = compact[digitsEnd + 2];

        int bandIndex = Bands.IndexOf(band, StringComparison.Ordinal);
        int columnIndex = ColumnLetters.IndexOf(column, StringComparison.Ordinal);
        int rowIndex = RowLetters.IndexOf(row, StringComparison.Ordinal);

        if (bandIndex < 0)
        {
            error =
                $"'{band}' is not a latitude band. They run C to X without I and O, and A, B, Y "
                + "and Z are the polar grid this server does not implement.";
            return false;
        }

        if (columnIndex < 0 || rowIndex < 0)
        {
            error =
                $"'{column}{row}' is not a hundred-kilometre square. The letters exclude I and O.";
            return false;
        }

        string rest = compact[(digitsEnd + 3)..];

        if (rest.Length % 2 != 0 || rest.Length > 10)
        {
            error =
                $"The numeric part '{rest}' must be an even count of up to ten digits: half the "
                + "easting, half the northing.";
            return false;
        }

        double withinEasting = 0;
        double withinNorthing = 0;

        if (rest.Length > 0)
        {
            int half = rest.Length / 2;

            if (!long.TryParse(rest.AsSpan(0, half), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long e)
                || !long.TryParse(rest.AsSpan(half), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long n))
            {
                error = $"The numeric part '{rest}' is not digits.";
                return false;
            }

            double scale = Math.Pow(10, 5 - half);

            // <b>The half-square offset puts the point in the middle of the
            // square it names</b> rather than at its south-west corner. A
            // two-digit reference names a ten-kilometre square, and returning
            // its corner is five kilometres of avoidable error in each axis.
            withinEasting = (e * scale) + (scale / 2);
            withinNorthing = (n * scale) + (scale / 2);
        }
        else
        {
            withinEasting = 50_000;
            withinNorthing = 50_000;
        }

        int set = ((zone - 1) % 3) * 8;
        int columnInZone = columnIndex - set;

        if (columnInZone is < 0 or > 7)
        {
            error =
                $"'{column}' is not a column letter for zone {zone}. Columns repeat every three "
                + "zones, and this one belongs to a different set.";
            return false;
        }

        double easting = ((columnInZone + 1) * 100_000) + withinEasting;

        int rowIndexInZone = rowIndex;

        if (zone % 2 == 0)
        {
            rowIndexInZone -= 5;
        }

        rowIndexInZone = ((rowIndexInZone % 20) + 20) % 20;

        // <b>Rows repeat every two million metres, so the band letter is what
        // picks the right repetition.</b> Without it the same square letters
        // name a place two thousand kilometres away, which is the difference
        // between Ankara and Helsinki.
        double bandSouth = -80 + (bandIndex * 8);
        double approximateNorthing = ApproximateNorthing(bandSouth);

        double northing = (rowIndexInZone * 100_000) + withinNorthing;

        while (northing < approximateNorthing - 1_000_000)
        {
            northing += 2_000_000;
        }

        bool north = band >= 'N';

        (longitude, latitude) = FromUtm(zone, easting, northing, north);

        return true;
    }

    /// <summary>
    /// Roughly how far north a latitude is, in metres, for picking the row cycle.
    /// </summary>
    /// <remarks>
    /// <b>Approximate on purpose.</b> It only has to be within a million metres
    /// to choose the right two-million-metre repetition, and a spherical arc
    /// length is comfortably inside that everywhere the grid exists.
    /// </remarks>
    private static double ApproximateNorthing(double latitude) =>
        latitude >= 0
            ? latitude * Math.PI / 180 * A
            : FalseNorthing + (latitude * Math.PI / 180 * A);
}
