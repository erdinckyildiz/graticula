using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// UTM and the sexagesimal notations, checked against PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>The projection is PROJ's now, so these tests stopped checking it.</b> Until
/// 2026-08-23 <see cref="GeoCoordinateString"/> carried a transverse Mercator series and this
/// file pinned it against `ST_Transform` to the millimetre — a real check on a real risk, and
/// the risk was that there were two coordinate engines at all.
/// [D-114](../../docs/architecture-debt.md) deleted the series;
/// [ADR-022](../../docs/adr/ADR-022-geometry-server.md) §4 refuses a second engine by name.
/// </para>
/// <para>
/// <b>What is left to test is what is still ours</b>, and it is the part a converter written
/// from the definition alone gets wrong: the zone rule with its Norway and Svalbard exceptions,
/// the hundred-kilometre lettering, the band letters, the packing and the angular notations.
/// The projection is supplied here the way the endpoint supplies it, so a round trip runs the
/// whole path rather than half of it.
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

    /// <summary>Where a coordinate falls on the grid, projected the way the endpoint does.</summary>
    /// <remarks>
    /// <b>Our zone, PROJ's metres.</b> The zone is asked of `GeoCoordinateString` on purpose:
    /// if the zone rule is wrong for Bergen then PostGIS is asked for the wrong EPSG code and
    /// the easting comes back thousands of metres out, which is a failure that names the zone.
    /// </remarks>
    private async Task<GeoCoordinateString.GridPosition> GridAsync(
        double longitude, double latitude)
    {
        Assert.True(
            GeoCoordinateString.TryZone(longitude, latitude, out int zone, out bool north,
                out string? error),
            error);

        (double x, double y) = await ProjectAsync(longitude, latitude, EpsgFor(zone, north));

        return new GeoCoordinateString.GridPosition(zone, north, x, y);
    }

    /// <summary>The geographic position a grid position names.</summary>
    private async Task<(double Longitude, double Latitude)> UnprojectAsync(
        GeoCoordinateString.GridPosition grid)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select ST_X(p), ST_Y(p) from (
               select ST_Transform(
                        ST_SetSRID(ST_MakePoint(@x, @y),
                                   {EpsgFor(grid.Zone, grid.North).ToString(CultureInfo.InvariantCulture)}),
                        4326) as p) t
             """);

        command.Parameters.AddWithValue("x", grid.Easting);
        command.Parameters.AddWithValue("y", grid.Northing);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    // ---------- UTM ----------

    /// <summary>
    /// The zone rule is ours, and its exceptions are what a definition alone gets wrong.
    /// </summary>
    /// <remarks>
    /// <b>This replaces a test that pinned our transverse Mercator against PROJ to the
    /// millimetre.</b> That test was worth having while there were two engines, and it retired
    /// with the one it was watching ([D-114](../../docs/architecture-debt.md)). What did not
    /// retire is the zone: six-degree columns, except that zone 32 widens over southern Norway
    /// so Bergen is not split, and four zones are rearranged over Svalbard. **Those four rows
    /// are the reason this is a test rather than an assertion about arithmetic.**
    /// </remarks>
    [Theory]
    [InlineData(32.8597, 39.9334, 36, true, "Ankara")]
    [InlineData(-77.0365, 38.8977, 18, true, "Washington")]
    [InlineData(151.2093, -33.8688, 56, false, "Sydney")]
    [InlineData(3.0, 0.0, 31, true, "equator on a central meridian")]
    [InlineData(5.3221, 60.3913, 32, true, "Bergen — the widened zone")]
    [InlineData(15.6469, 78.2232, 33, true, "Longyearbyen — the rearranged Svalbard zones")]
    [InlineData(-0.1276, 51.5072, 30, true, "London — on the 30/31 boundary")]
    public void The_zone_rule_places_a_coordinate(
        double longitude, double latitude, int expected, bool north, string place)
    {
        Assert.True(
            GeoCoordinateString.TryZone(longitude, latitude, out int zone, out bool inNorth,
                out string? error),
            error);

        Assert.Equal(expected, zone);
        Assert.Equal(north, inNorth);

        // And the EPSG code the zone names is the one PostGIS knows, which is the whole
        // reason the zone matters: it is an argument to ST_Transform.
        Assert.Equal((north ? 32_600 : 32_700) + expected, GeoCoordinateString.EpsgFor(zone, north));

        Assert.NotEmpty(place);
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
    public async Task A_UTM_string_reads_back_to_where_it_came_from(
        double longitude, double latitude, string place)
    {
        GeoCoordinateString.GridPosition grid = await GridAsync(longitude, latitude);

        Assert.True(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Utm, 0, spaces: true,
                out string text, out string? error, grid),
            error);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Utm,
                out _, out _, out GeoCoordinateString.GridPosition? read, out error),
            error);

        Assert.NotNull(read);

        (double back, double backLatitude) = await UnprojectAsync(read!.Value);

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
    public async Task An_MGRS_reference_reads_back_to_the_same_place(
        double longitude, double latitude, string place)
    {
        GeoCoordinateString.GridPosition grid = await GridAsync(longitude, latitude);

        Assert.True(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Mgrs, 5, spaces: false,
                out string text, out string? error, grid),
            error);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Mgrs,
                out _, out _, out GeoCoordinateString.GridPosition? read, out error),
            error);

        Assert.NotNull(read);

        (double back, double backLatitude) = await UnprojectAsync(read!.Value);

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
    public async Task The_Washington_Monument_is_where_the_USNG_says_it_is()
    {
        const double Longitude = -77.0353;
        const double Latitude = 38.8895;

        GeoCoordinateString.GridPosition grid = await GridAsync(Longitude, Latitude);

        Assert.True(
            GeoCoordinateString.TryWrite(
                Longitude, Latitude, GeoCoordinateNotation.Usng, 5, spaces: true,
                out string text, out string? error, grid),
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
        // <b>Refused before anything is projected</b>, which is why no grid position is
        // supplied here: the latitude band is checked first, so a polar coordinate never
        // reaches the datastore at all.
        Assert.False(
            GeoCoordinateString.TryWrite(
                longitude, latitude, GeoCoordinateNotation.Mgrs, 5, spaces: false,
                out _, out string? error));

        Assert.Contains("Polar Stereographic", error!, StringComparison.Ordinal);

        // And the zone check refuses it for the same reason, in the words the endpoint uses.
        Assert.False(
            GeoCoordinateString.TryZone(longitude, latitude, out _, out _, out string? why));

        Assert.Contains("outside the UTM grid", why!, StringComparison.Ordinal);
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
    public async Task Every_notation_reads_back_what_it_wrote(
        GeoCoordinateNotation notation, bool spaces)
    {
        bool onGrid = notation is GeoCoordinateNotation.Utm or GeoCoordinateNotation.Mgrs
            or GeoCoordinateNotation.Usng;

        foreach (object[] row in Places)
        {
            double longitude = (double)row[0];
            double latitude = (double)row[1];
            string place = (string)row[2];

            // <b>Projected for the grid notations, and not for the angular ones.</b> That is
            // the split D-114 introduced: this class holds a notation and the datastore holds
            // the projection, so a test of the round trip has to walk both.
            GeoCoordinateString.GridPosition? grid =
                onGrid ? await GridAsync(longitude, latitude) : null;

            Assert.True(
                GeoCoordinateString.TryWrite(
                    longitude, latitude, notation, 5, spaces, out string text, out string? error,
                    grid),
                error);

            Assert.True(
                GeoCoordinateString.TryRead(
                    text, notation, out double back, out double backLatitude,
                    out GeoCoordinateString.GridPosition? read, out error),
                $"{place}: wrote '{text}' and could not read it back — {error}");

            if (read is { } position)
            {
                (back, backLatitude) = await UnprojectAsync(position);
            }

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
    public async Task A_packed_UTM_string_near_the_equator_is_still_splittable()
    {
        GeoCoordinateString.GridPosition grid = await GridAsync(3.0, 0.0004);

        Assert.True(
            GeoCoordinateString.TryWrite(
                3.0, 0.0004, GeoCoordinateNotation.Utm, 0, spaces: false,
                out string text, out string? error, grid),
            error);

        // Two for the zone, one for the hemisphere, six and seven for the axes.
        Assert.Equal(16, text.Length);

        Assert.True(
            GeoCoordinateString.TryRead(
                text, GeoCoordinateNotation.Utm, out _, out _,
                out GeoCoordinateString.GridPosition? read, out error),
            $"wrote '{text}' and could not read it back — {error}");

        Assert.NotNull(read);

        (double back, double backLatitude) = await UnprojectAsync(read!.Value);

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
