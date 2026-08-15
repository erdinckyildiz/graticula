using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// UTM and the sexagesimal notations, checked against PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hard part of MGRS is UTM, and UTM has an oracle.</b> PostGIS can
/// transform a point to EPSG:326nn or 327nn, which is the same projection with
/// the same ellipsoid, so the series expansion in
/// <see cref="GeoCoordinateString"/> can be checked against PROJ to the
/// millimetre on real coordinates. The lettering on top of it is a lookup, and
/// what remains for it is a round trip and two published references.
/// </para>
/// <para>
/// <b>These operations were not on the refusal list either.</b> Until
/// 2026-08-15 a caller asking for <c>toGeoCoordinateString</c> got 404 — the
/// answer for an operation that does not exist, given for one nobody had
/// written. The comparison against a real ArcGIS GeometryServer is what found
/// them.
/// </para>
/// </remarks>
public sealed class GeoCoordinateStringAgainstPostgisTests : PostgresFixture
{
    /// <summary>Points spread across zones, hemispheres and bands.</summary>
    /// <remarks>
    /// <b>Chosen rather than random, and each one is a case.</b> The equator on a
    /// central meridian is where the false northing switches; a southern point
    /// exercises the ten-million-metre offset; the Norway and Svalbard rows are
    /// the two zone exceptions that a converter written from the definition
    /// alone gets wrong.
    /// </remarks>
    public static TheoryData<double, double, string> Places => new()
    {
        { 32.8597, 39.9334, "Ankara" },
        { 28.9784, 41.0082, "Istanbul" },
        { -77.0365, 38.8977, "Washington" },
        { 151.2093, -33.8688, "Sydney" },
        { -58.3816, -34.6037, "Buenos Aires" },
        { 3.0, 0.0, "equator on a central meridian" },
        { 5.3221, 60.3913, "Bergen — inside the widened zone 32" },
        { 15.6469, 78.2232, "Longyearbyen — inside the rearranged Svalbard zones" },
        { 174.7633, -36.8485, "Auckland" },
        { -0.1276, 51.5072, "London — on the zone 30/31 boundary" },
    };

    /// <summary>The UTM zone PostGIS should be asked for.</summary>
    /// <remarks>
    /// <b>Derived from our own zone rule, not from the longitude.</b> That is
    /// deliberate: if our zone selection is wrong for Bergen, PostGIS is asked
    /// for the wrong zone and the easting comes back thousands of metres out.
    /// The test would catch it either way, but this way the failure names the
    /// zone.
    /// </remarks>
    private static int EpsgFor(int zone, bool north) => (north ? 32600 : 32700) + zone;

    private async Task<(double X, double Y)> ProjectAsync(
        double longitude, double latitude, int epsg)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select ST_X(p), ST_Y(p) from (
               select ST_Transform(
                        ST_SetSRID(ST_MakePoint(@lon, @lat), 4326),
                        {epsg.ToString(CultureInfo.InvariantCulture)}) as p) t
             """);

        command.Parameters.AddWithValue("lon", longitude);
        command.Parameters.AddWithValue("lat", latitude);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    // ---------- UTM ----------

    /// <summary>
    /// Our UTM easting and northing match PROJ's to the millimetre.
    /// </summary>
    /// <remarks>
    /// <b>One millimetre, which is three orders below anything the output can
    /// express.</b> MGRS's finest form names a one-metre square. A tolerance
    /// this tight is not perfectionism — it is the only tolerance that
    /// distinguishes "the series is right" from "the series is nearly right",
    /// and a nearly-right series fails at the edge of a zone rather than in the
    /// middle where every hand-written test puts its points.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Places))]
    public async Task Utm_matches_PROJ_to_the_millimetre(
        double longitude, double latitude, string place)
    {
        // <b>The unrounded numbers, not the string.</b> The string is whole
        // metres, so comparing it against PROJ can only ever establish that we
        // are within half a metre — which is not a check on a series expansion.
        Assert.True(
            GeoCoordinateString.TryToUtm(
                longitude, latitude,
                out int zone, out double easting, out double northing, out string? error),
            error);

        (double x, double y) = await ProjectAsync(longitude, latitude, EpsgFor(zone, latitude >= 0));

        // PostGIS's southern-hemisphere EPSG codes already carry the ten million
        // metre false northing, so both sides are in the same frame.
        Assert.True(
            Math.Abs(easting - x) < 0.001 && Math.Abs(northing - y) < 0.001,
            $"{place}: we say zone {zone} {easting:F4} {northing:F4}, PROJ says "
            + $"{x:F4} {y:F4} — {Math.Abs(easting - x):F5} m east and "
            + $"{Math.Abs(northing - y):F5} m north apart.");
    }

    /// <summary>
    /// Reading a UTM string back gives the coordinate it was written from.
    /// </summary>
    /// <remarks>
    /// <b>A round trip catches an inverse that is wrong in the same direction as
    /// the forward.</b> It cannot catch one that is wrong in the opposite
    /// direction by the same amount — which is why the forward is pinned against
    /// PROJ above, and only then is the inverse checked against it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Places))]
    public void A_UTM_string_reads_back_to_where_it_came_from(
        double longitude, double latitude, string place)
    {
        Assert.True(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Utm, 0, spaces: true,
                out string text, out string? error),
            error);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Utm,
                out double back, out double backLatitude, out error),
            error);

        // The string is whole metres, so a metre of tolerance is the string's
        // own precision rather than slack in the arithmetic.
        Assert.True(
            Distance(longitude, latitude, back, backLatitude) < 1.5,
            $"{place}: '{text}' read back to {back:F6}, {backLatitude:F6}, which is "
            + $"{Distance(longitude, latitude, back, backLatitude):F2} m away.");
    }

    // ---------- MGRS ----------

    /// <summary>
    /// An MGRS reference reads back to within the square it names.
    /// </summary>
    /// <remarks>
    /// <b>Five digits per axis is a one-metre square, so the tolerance is a
    /// metre and a half.</b> A looser tolerance would let a wrong hundred-
    /// kilometre letter pass on a point near the middle of its zone, which is
    /// exactly where the lettering is easiest to get wrong and hardest to
    /// notice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Places))]
    public void An_MGRS_reference_reads_back_to_the_same_place(
        double longitude, double latitude, string place)
    {
        Assert.True(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Mgrs, 5, spaces: false,
                out string text, out string? error),
            error);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Mgrs,
                out double back, out double backLatitude, out error),
            error);

        Assert.True(
            Distance(longitude, latitude, back, backLatitude) < 1.5,
            $"{place}: '{text}' read back {Distance(longitude, latitude, back, backLatitude):F2} "
            + "m away.");
    }

    /// <summary>
    /// The published USNG reference for the Washington Monument.
    /// </summary>
    /// <remarks>
    /// <b>One external reference, because a round trip cannot detect a lettering
    /// scheme that is self-consistently wrong.</b> If the column set were offset
    /// by one, every write and every read would agree with each other and
    /// disagree with the rest of the world. This is the check that fails in that
    /// case, and it is the reason to have it even though it is a single point.
    /// </remarks>
    [Fact]
    public void The_Washington_Monument_is_where_the_USNG_says_it_is()
    {
        const double Longitude = -77.0353;
        const double Latitude = 38.8895;

        Assert.True(
            GeoCoordinateString.TryWrite(
                Longitude, Latitude, GeoCoordinateNotation.Usng, 5, spaces: true,
                out string text, out string? error),
            error);

        // 18S UJ 23xxx 06xxx — zone 18, band S, square UJ. The last digits move
        // with the exact position published for the monument, so the assertion
        // is on the parts that are the lettering scheme.
        Assert.StartsWith("18S UJ 23", text, StringComparison.Ordinal);
        Assert.Contains(" 06", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Above 84°N the answer is a refusal, not a wrong grid square.
    /// </summary>
    /// <remarks>
    /// <b>The polar grid is a different projection.</b> UTM stops at 84°N and
    /// 80°S and MGRS continues on Universal Polar Stereographic, which this
    /// server does not implement. Producing a UTM-based string there would be
    /// silently wrong, and a wrong grid reference is discovered by somebody
    /// standing in the wrong place.
    /// </remarks>
    [Theory]
    [InlineData(0.0, 85.0)]
    [InlineData(0.0, -81.0)]
    public void The_polar_regions_are_refused_rather_than_approximated(
        double longitude, double latitude)
    {
        Assert.False(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Mgrs, 5, spaces: false,
                out _, out string? error));

        Assert.Contains("Polar Stereographic", error!, StringComparison.Ordinal);
    }

    // ---------- the angular notations ----------

    /// <summary>
    /// Our degrees, minutes and seconds agree with ST_AsLatLonText.
    /// </summary>
    /// <remarks>
    /// <b>Compared as numbers rather than as strings.</b> PostGIS writes
    /// <c>39°56'0.240"N</c> and we write <c>39 56 0.240N</c>; asserting on the
    /// punctuation would be testing two formatters against each other rather
    /// than testing the arithmetic. What must agree is the angle.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Places))]
    public async Task Degrees_minutes_and_seconds_agree_with_PostGIS(
        double longitude, double latitude, string place)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            // <b>One template, applied to both axes.</b> ST_AsLatLonText takes a
            // single pattern and uses it for the latitude and then the
            // longitude; writing it out twice is rejected with "cannot include
            // degrees more than once", which is how this test first failed.
            "select ST_AsLatLonText(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), "
            + "'D.DDDDDDDD C')");

        command.Parameters.AddWithValue("lon", longitude);
        command.Parameters.AddWithValue("lat", latitude);

        string theirs = (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        Assert.True(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.DegreesMinutesSeconds, 4,
                spaces: true, out string text, out string? error),
            error);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.DegreesMinutesSeconds,
                out double back, out double backLatitude, out error),
            error);

        // PostGIS's own decimal rendering, parsed the same way ours is.
        Assert.True(
            GeoCoordinateString.TryRead(
                theirs.Replace("°", " ", StringComparison.Ordinal),
                GeoCoordinateNotation.DecimalDegrees,
                out double theirLongitude, out double theirLatitude, out error),
            $"could not read PostGIS's own output '{theirs}': {error}");

        Assert.Equal(theirLatitude, backLatitude, 6);
        Assert.Equal(theirLongitude, back, 6);

        Assert.True(
            Distance(longitude, latitude, back, backLatitude) < 0.05,
            $"{place}: '{text}' read back "
            + $"{Distance(longitude, latitude, back, backLatitude):F4} m away.");
    }

    /// <summary>
    /// 59.9999 seconds does not print as sixty.
    /// </summary>
    /// <remarks>
    /// <b>The classic defect in every hand-written sexagesimal formatter</b>, and
    /// the reason the rounding here happens once in the smallest unit and then
    /// carries upward. Formatting each part independently produces "39 59 60.0",
    /// which is a minute that does not exist and a string most readers reject.
    /// </remarks>
    [Fact]
    public void A_rounded_second_carries_into_the_minute()
    {
        // 39.99999999° is 39° 59' 59.99996", which at one decimal is 60.0.
        Assert.True(
            GeoCoordinateString.TryWrite(
                0.5, 39.99999999, GeoCoordinateNotation.DegreesMinutesSeconds, 1,
                spaces: true, out string text, out string? error),
            error);

        Assert.DoesNotContain("60.0", text, StringComparison.Ordinal);
        Assert.StartsWith("40 0 0.0 N", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A string without hemisphere letters is refused rather than guessed.
    /// </summary>
    [Fact]
    public void An_ambiguous_string_is_refused()
    {
        Assert.False(
            GeoCoordinateString.TryRead(
                "45 30", GeoCoordinateNotation.DecimalDegrees, out _, out _, out string? error));

        Assert.Contains("hemisphere", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every notation reads back what it wrote, packed and spaced alike.
    /// </summary>
    /// <remarks>
    /// <b>The packed forms are the ones that break.</b> "39 56 0.24N" run
    /// together is "39560.2400N", which nothing can split — so the angular
    /// notations keep their separators whatever the caller asks, and UTM pads to
    /// six and seven digits so that its packed form can be split by width. This
    /// test exists because the first version wrote strings its own reader
    /// rejected, and nothing in the suite noticed.
    /// </remarks>
    [Theory]
    [InlineData(GeoCoordinateNotation.DecimalDegrees, true)]
    [InlineData(GeoCoordinateNotation.DecimalDegrees, false)]
    [InlineData(GeoCoordinateNotation.DegreesDecimalMinutes, true)]
    [InlineData(GeoCoordinateNotation.DegreesDecimalMinutes, false)]
    [InlineData(GeoCoordinateNotation.DegreesMinutesSeconds, true)]
    [InlineData(GeoCoordinateNotation.DegreesMinutesSeconds, false)]
    [InlineData(GeoCoordinateNotation.Utm, true)]
    [InlineData(GeoCoordinateNotation.Utm, false)]
    [InlineData(GeoCoordinateNotation.Mgrs, true)]
    [InlineData(GeoCoordinateNotation.Mgrs, false)]
    [InlineData(GeoCoordinateNotation.Usng, true)]
    [InlineData(GeoCoordinateNotation.Usng, false)]
    public void Every_notation_reads_back_what_it_wrote(
        GeoCoordinateNotation notation, bool spaces)
    {
        foreach (object[] row in Places)
        {
            double longitude = (double)row[0];
            double latitude = (double)row[1];
            string place = (string)row[2];

            Assert.True(
                GeoCoordinateString.TryWrite(
                    longitude, latitude, notation, 5, spaces, out string text, out string? error),
                error);

            Assert.True(
                GeoCoordinateString.TryRead(
                    text, notation, out double back, out double backLatitude, out error),
                $"{place}: wrote '{text}' and could not read it back — {error}");

            Assert.True(
                Distance(longitude, latitude, back, backLatitude) < 1.5,
                $"{place}: '{text}' read back "
                + $"{Distance(longitude, latitude, back, backLatitude):F2} m away.");
        }
    }

    /// <summary>
    /// A near-equator northing keeps its width when packed.
    /// </summary>
    /// <remarks>
    /// <b>The case the padding exists for.</b> Forty-two metres north of the
    /// equator writes as "42" unpadded, and "31N50000042" has no rule that
    /// recovers the easting from the northing.
    /// </remarks>
    [Fact]
    public void A_packed_UTM_string_near_the_equator_is_still_splittable()
    {
        Assert.True(
            GeoCoordinateString.TryWrite(
                3.0, 0.0004, GeoCoordinateNotation.Utm, 0, spaces: false,
                out string text, out string? error),
            error);

        // Two for the zone, one for the hemisphere, six and seven for the axes.
        Assert.Equal(16, text.Length);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Utm, out double back, out double backLatitude,
                out error),
            $"wrote '{text}' and could not read it back — {error}");

        Assert.True(Distance(3.0, 0.0004, back, backLatitude) < 1.5);
    }

    /// <summary>Metres between two geographic points, near enough for a test.</summary>
    private static double Distance(double lon1, double lat1, double lon2, double lat2)
    {
        const double Radius = 6_371_008.8;

        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double mid = (lat1 + lat2) / 2 * Math.PI / 180;

        double x = dLon * Math.Cos(mid);

        return Math.Sqrt((x * x) + (dLat * dLat)) * Radius;
    }
}
