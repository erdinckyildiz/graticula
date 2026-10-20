using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>
/// Spatial relations beyond intersection, and a distance, decided by the geometry engine for a layer whose database
/// cannot decide them the way PostGIS does — a GeoParquet file (D-263, ADR-066 #3) and a registered Oracle table
/// (ADR-205).
/// </summary>
/// <remarks>
/// <para>
/// <b>Moved out of <c>GeoParquetFeatureSource</c> on 2026-10-20</b>, when Oracle became the second reader that needs
/// it, so the two answer a relation with the same code and the same sentences.
/// </para>
/// <para>
/// <b>The engine is the worker process the GeometryServer's <c>relation</c> runs in</b> (owner decision 2026-09-23),
/// so such a layer and that operation give the same answer, and nothing new is linked into this process. <b>The
/// meaning is PostGIS's, read off <c>PostGisFeatureSource</c>:</b> the feature is on the left and the filter on the
/// right, so <c>Within</c> is the feature within the filter and <c>Contains</c> the feature containing it —
/// <c>T*****FF*</c>, which is how <c>ST_Contains</c> is defined. A distance is in the layer's units on a projected layer;
/// on a layer in degrees it is metres, measured in an azimuthal equidistant projection centred on the filter.
/// </para>
/// </remarks>
public static class EngineRelations
{
    /// <summary>The relations the engine answers.</summary>
    public static IReadOnlyList<SpatialRelation> Relations { get; } =
    [
        SpatialRelation.Contains, SpatialRelation.Within, SpatialRelation.Crosses,
        SpatialRelation.Overlaps, SpatialRelation.Touches, SpatialRelation.Relate,
    ];

    /// <summary>
    /// How far from its centre a distance on a layer in degrees is measured: the filter's reach plus the distance.
    /// </summary>
    /// <remarks>
    /// The azimuthal equidistant projection the test is made in is exact along every line through its centre and
    /// stretches the rest by <c>(r/R) / sin(r/R)</c>: 0.1 % at 500 km and 0.4 % at 1,000 km. Past that a distance is
    /// refused rather than answered differently from the datastore, which measures on the ellipsoid.
    /// </remarks>
    public const double MostGeographicReachMetres = 1_000_000;

    /// <summary>Whether this filter is the engine's to decide: a distance, or a relation beyond intersection.</summary>
    /// <param name="spatial">The filter.</param>
    /// <returns>True when the engine decides it.</returns>
    public static bool Decides(SpatialFilter spatial)
    {
        ArgumentNullException.ThrowIfNull(spatial);
        return spatial.Distance > 0 || Contains(spatial.Relation);
    }

    /// <summary>Whether the engine answers this relation.</summary>
    /// <param name="relation">The relation.</param>
    /// <returns>True for the relations in <see cref="Relations"/>.</returns>
    public static bool Contains(SpatialRelation relation)
    {
        foreach (SpatialRelation one in Relations)
        {
            if (one == relation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The box a filter's candidates are looked for in: its own, grown by a distance when it has one.</summary>
    /// <param name="filter">The filter's geometry, in the layer's reference.</param>
    /// <param name="distance">The distance; zero for none.</param>
    /// <param name="layerSrid">The layer's reference, which says whether the distance is metres on degrees.</param>
    /// <returns>The box.</returns>
    public static Envelope SearchBox(Geometry filter, double distance, int layerSrid)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // <b>In degrees, from the geodesic edges</b> — the ones the distance is measured to (SatisfyingAsync). The
        // envelope of the vertices alone misses an edge's bow toward the pole: measured 2026-10-20, a place 49,057 m north
        // of an envelope's top edge on the ellipsoid lay 0.0005° outside the box grown from that edge's straight line.
        return distance > 0
            ? AxisOrder.IsGeographic(layerSrid)
                ? GeographicWidened(GeodesicDensified(filter, MostGeodesicStepMetres).Envelope, distance)
                : Widened(filter.Envelope, distance)
            : filter.Envelope;
    }

    /// <summary>A box grown by a distance on every side.</summary>
    public static Envelope Widened(Envelope box, double distance) =>
        new(box.MinX - distance, box.MinY - distance, box.MaxX + distance, box.MaxY + distance);

    /// <summary>
    /// A box in degrees grown by a distance in metres, never narrower than the circle it has to hold — as the
    /// datastore widens it (<c>PostGisFeatureSource.GeographicWidening</c>).
    /// </summary>
    /// <remarks>
    /// Latitude by the fewest metres a degree of it ever is; longitude by that divided by the cosine of the furthest
    /// latitude the box reaches. Near a pole, or across the antimeridian, every longitude.
    /// </remarks>
    public static Envelope GeographicWidened(Envelope box, double metres)
    {
        double dLat = metres / 110_574.0;
        double south = Math.Max(-90, box.MinY - dLat), north = Math.Min(90, box.MaxY + dLat);
        double furthest = Math.Max(Math.Abs(south), Math.Abs(north));

        if (furthest >= 89.9)
        {
            return new Envelope(-180, south, 180, north);
        }

        double dLon = metres / (111_320.0 * Math.Cos(furthest * Math.PI / 180));
        double west = box.MinX - dLon, east = box.MaxX + dLon;

        return west < -180 || east > 180
            ? new Envelope(-180, south, 180, north)
            : new Envelope(west, south, east, north);
    }

    /// <summary>
    /// The candidates the geometry engine says satisfy the filter, in batches the worker was built for.
    /// </summary>
    /// <param name="candidates">The candidates' identities.</param>
    /// <param name="geometries">Their geometries, in the same order, in the layer's reference.</param>
    /// <param name="spatial">The filter, its geometry already in the layer's reference.</param>
    /// <param name="layerSrid">The layer's reference.</param>
    /// <param name="projector">Moves a layer in degrees into metres for a distance.</param>
    /// <param name="engine">The geometry engine.</param>
    /// <param name="servedFrom">Where the layer is served from, for a refusal: <c>a GeoParquet file</c>.</param>
    /// <param name="cancellationToken">The deadline.</param>
    /// <returns>The identities that satisfy it, in the order given.</returns>
    /// <exception cref="QueryNotSupportedException">
    /// The distance reaches too far on a layer in degrees, or the engine refused — busy, too large, a deadline — in
    /// which case the query is refused with the engine's own sentence rather than answered in part.
    /// </exception>
    public static async Task<List<long>> SatisfyingAsync(
        IReadOnlyList<long> candidates,
        IReadOnlyList<Geometry> geometries,
        SpatialFilter spatial,
        int layerSrid,
        IProjector projector,
        IGeometryEngine engine,
        string servedFrom,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(geometries);
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(engine);

        List<long> satisfied = [];

        // <b>Points within a distance of a point are measured here, not in the engine.</b> The general path projects every
        // thousand candidates through the datastore's PROJ and asks the geometry worker; for two points the answer is one
        // distance — on the WGS 84 ellipsoid for EPSG:4326, as PostGIS's geography measures it, or on the plane for a
        // projected layer, as its geometry does. Measured on the VPS before: 50 km around a point over a MariaDB layer of
        // 200,000 places took 770 ms where PostGIS took 90.
        if (PointDistances(candidates, geometries, spatial, layerSrid) is { } measured)
        {
            return measured;
        }

        string? metric = null;
        Geometry right = spatial.Geometry;

        if (spatial.Distance > 0 && AxisOrder.IsGeographic(layerSrid))
        {
            Envelope box = spatial.Geometry.Envelope;
            double lon = (box.MinX + box.MaxX) / 2, lat = (box.MinY + box.MaxY) / 2;
            double reach = Math.Max(
                    Math.Max(Haversine(lon, lat, box.MinX, box.MinY), Haversine(lon, lat, box.MaxX, box.MaxY)),
                    Math.Max(Haversine(lon, lat, box.MinX, box.MaxY), Haversine(lon, lat, box.MaxX, box.MinY)))
                + spatial.Distance;

            if (reach > MostGeographicReachMetres)
            {
                throw new QueryNotSupportedException(
                    $"This layer is served from {servedFrom} in degrees, and a distance on it is measured in "
                    + $"metres around the filter, within {MostGeographicReachMetres / 1000:N0} km of its centre; this "
                    + $"filter and distance reach {reach / 1000:N0} km. Use a smaller filter or distance, or publish the "
                    + "data into the datastore, which measures on the ellipsoid at any distance.");
            }

            metric = string.Create(
                CultureInfo.InvariantCulture,
                $"+proj=aeqd +lat_0={lat:R} +lon_0={lon:R} +datum=WGS84 +units=m +no_defs");

            // <b>The filter's edges are geodesics, as `geography` reads them</b> — densified along great circles before
            // they are projected, or an edge would be the straight line between two projected corners. Measured
            // 2026-10-20: an envelope of 4° by 2.5° and 50 km left out a place 49,057 m from it on the ellipsoid,
            // because the envelope's northern edge bows a kilometre north as a geodesic and not as a projected chord.
            right = (await projector.ProjectToDefinitionAsync(
                    [GeodesicDensified(spatial.Geometry, MostGeodesicStepMetres)], layerSrid, metric, cancellationToken)
                .ConfigureAwait(false) is [var moved] ? moved : null)
                ?? throw new QueryNotSupportedException(
                    $"This layer is served from {servedFrom} in degrees, and the filter could not be moved into "
                    + "metres to measure a distance around it.");
        }

        const int Batch = 1_000;

        for (int start = 0; start < candidates.Count; start += Batch)
        {
            int count = Math.Min(Batch, candidates.Count - start);
            List<Geometry> left = new(count);

            for (int i = start; i < start + count; i++)
            {
                left.Add(geometries[i]);
            }

            if (metric is not null)
            {
                left = [.. await projector.ProjectToDefinitionAsync(left, layerSrid, metric, cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new QueryNotSupportedException(
                        $"This layer is served from {servedFrom} in degrees, and its features could not be moved "
                        + "into metres to measure a distance.")];
            }

            EngineRequest request = spatial.Distance > 0
                ? new EngineRequest(EngineOperation.WithinDistance, left, [right], metric is null ? layerSrid : 3857)
                {
                    Distance = spatial.Distance,
                }
                : new EngineRequest(EngineOperation.Relate, left, [spatial.Geometry], layerSrid)
                {
                    Pattern = PatternOf(spatial),
                };

            EngineResult result = await engine.ComputeAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.Refusal != EngineRefusal.None)
            {
                throw new QueryNotSupportedException(
                    $"This layer is served from {servedFrom}, and '{spatial.Relation}' is decided by the "
                    + $"geometry engine, which did not answer: {result.Message}");
            }

            foreach (int[] pair in result.Pairs ?? [])
            {
                satisfied.Add(candidates[start + pair[0]]);
            }
        }

        return satisfied;
    }

    /// <summary>The engine's name for a relation, with the feature on the left and the filter on the right.</summary>
    public static string PatternOf(SpatialFilter spatial)
    {
        ArgumentNullException.ThrowIfNull(spatial);

        return spatial.Relation switch
        {
            SpatialRelation.Within => "esriGeometryRelationWithin",
            SpatialRelation.Touches => "esriGeometryRelationTouch",
            SpatialRelation.Crosses => "esriGeometryRelationCross",
            SpatialRelation.Overlaps => "esriGeometryRelationOverlap",
            SpatialRelation.Contains => "T*****FF*",
            SpatialRelation.Relate => spatial.RelatePattern
                ?? throw new QueryNotSupportedException("A relation filter needs a DE-9IM pattern."),
            _ => throw new QueryNotSupportedException($"'{spatial.Relation}' is not decided by the geometry engine."),
        };
    }

    /// <summary>The longest geodesic edge a filter in degrees keeps before it is projected for a distance.</summary>
    /// <remarks>A 10 km chord is a few metres from its great circle at the latitudes this serves.</remarks>
    public const double MostGeodesicStepMetres = 10_000;

    /// <summary>
    /// A geometry in degrees whose every edge longer than <paramref name="stepMetres"/> is cut along its great circle —
    /// so a projection that keeps vertices and not edges draws the geodesic the datastore's <c>geography</c> measures.
    /// </summary>
    /// <param name="geometry">The geometry, longitude then latitude.</param>
    /// <param name="stepMetres">The longest edge kept.</param>
    /// <returns>The densified geometry; a point, or one already dense enough, as it was.</returns>
    public static Geometry GeodesicDensified(Geometry geometry, double stepMetres)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepMetres);

        XySequence Densify(XySequence coordinates)
        {
            double[] xy = coordinates.ToInterleavedArray();

            if (xy.Length < 4)
            {
                return coordinates;
            }

            List<double> dense = new(xy.Length) { xy[0], xy[1] };

            for (int i = 2; i < xy.Length; i += 2)
            {
                double lon1 = xy[i - 2], lat1 = xy[i - 1], lon2 = xy[i], lat2 = xy[i + 1];
                double metres = Haversine(lon1, lat1, lon2, lat2);
                int pieces = (int)Math.Ceiling(metres / stepMetres);

                for (int k = 1; k < pieces; k++)
                {
                    (double lon, double lat) = GreatCirclePoint(lon1, lat1, lon2, lat2, (double)k / pieces);
                    dense.Add(lon);
                    dense.Add(lat);
                }

                dense.Add(lon2);
                dense.Add(lat2);
            }

            return XySequence.Wrap([.. dense]);
        }

        LinearRing Ring(LinearRing ring) => ring.IsEmpty ? ring : new LinearRing(Densify(ring.Coordinates));

        Polygon Area(Polygon polygon) =>
            polygon.IsEmpty ? polygon : new Polygon(Ring(polygon.Shell), [.. System.Linq.Enumerable.Select(polygon.Holes, Ring)]);

        return geometry switch
        {
            { IsEmpty: true } or Point or MultiPoint => geometry,
            LinearRing ring => Ring(ring),
            LineString line => new LineString(Densify(line.Coordinates)),
            Polygon polygon => Area(polygon),
            MultiLineString lines => new MultiLineString(
                [.. System.Linq.Enumerable.Select(lines.Parts, l => (LineString)GeodesicDensified(l, stepMetres))]),
            MultiPolygon areas => new MultiPolygon([.. System.Linq.Enumerable.Select(areas.Parts, Area)]),
            _ => geometry,
        };
    }

    /// <summary>The point a fraction of the way along the great circle from one point to another, on a sphere.</summary>
    private static (double Lon, double Lat) GreatCirclePoint(double lon1, double lat1, double lon2, double lat2, double fraction)
    {
        const double Rad = Math.PI / 180;
        double p1 = lat1 * Rad, l1 = lon1 * Rad, p2 = lat2 * Rad, l2 = lon2 * Rad;
        double x1 = Math.Cos(p1) * Math.Cos(l1), y1 = Math.Cos(p1) * Math.Sin(l1), z1 = Math.Sin(p1);
        double x2 = Math.Cos(p2) * Math.Cos(l2), y2 = Math.Cos(p2) * Math.Sin(l2), z2 = Math.Sin(p2);
        double angle = Math.Acos(Math.Clamp((x1 * x2) + (y1 * y2) + (z1 * z2), -1, 1));

        if (angle < 1e-12)
        {
            return (lon1, lat1);
        }

        double a = Math.Sin((1 - fraction) * angle) / Math.Sin(angle), b = Math.Sin(fraction * angle) / Math.Sin(angle);
        double x = (a * x1) + (b * x2), y = (a * y1) + (b * y2), z = (a * z1) + (b * z2);

        return (Math.Atan2(y, x) / Rad, Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) / Rad);
    }

    /// <summary>The great-circle distance in metres between two points in degrees, on the mean radius.</summary>
    /// <summary>
    /// The candidates within the filter's distance when the filter and every candidate are single points and the layer is
    /// EPSG:4326 or projected; null to leave the question to the engine — any other shape, another geographic reference
    /// (its ellipsoid may not be WGS 84's), or a pair Vincenty's iteration does not settle.
    /// </summary>
    internal static List<long>? PointDistances(
        IReadOnlyList<long> candidates, IReadOnlyList<Geometry> geometries, SpatialFilter spatial, int layerSrid)
    {
        if (spatial.Distance <= 0 || spatial.Relation != SpatialRelation.Intersects
            || spatial.Geometry is not Point { IsEmpty: false } centre)
        {
            return null;
        }

        bool geographic = AxisOrder.IsGeographic(layerSrid);

        if (geographic && layerSrid != 4326)
        {
            return null;
        }

        List<long> within = [];

        for (int i = 0; i < geometries.Count; i++)
        {
            if (geometries[i] is not Point { IsEmpty: false } point)
            {
                return null;
            }

            double metres;

            if (geographic)
            {
                if (WgsDistance(centre.X, centre.Y, point.X, point.Y) is not { } d)
                {
                    return null;
                }

                metres = d;
            }
            else
            {
                double dx = point.X - centre.X, dy = point.Y - centre.Y;
                metres = Math.Sqrt((dx * dx) + (dy * dy));
            }

            if (metres <= spatial.Distance)
            {
                within.Add(candidates[i]);
            }
        }

        return within;
    }

    /// <summary>
    /// The distance between two longitude–latitude points on the WGS 84 ellipsoid, by Vincenty's inverse formula — within
    /// a tenth of a millimetre of Karney's, which PostGIS's geography uses, at the distances a filter reaches — or null
    /// where the iteration does not converge (nearly antipodal points).
    /// </summary>
    internal static double? WgsDistance(double lon1, double lat1, double lon2, double lat2)
    {
        const double A = 6_378_137.0, F = 1 / 298.257223563, B = A * (1 - F);
        double l = (lon2 - lon1) * Math.PI / 180;
        double u1 = Math.Atan((1 - F) * Math.Tan(lat1 * Math.PI / 180));
        double u2 = Math.Atan((1 - F) * Math.Tan(lat2 * Math.PI / 180));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
        double lambda = l, sinSigma, cosSigma, sigma, cosSqAlpha, cos2SigmaM;
        int iterations = 0;

        while (true)
        {
            double sinLambda = Math.Sin(lambda), cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(((cosU2 * sinLambda) * (cosU2 * sinLambda))
                + (((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda)) * ((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda))));

            if (sinSigma == 0)
            {
                return 0;
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1 - (sinAlpha * sinAlpha);
            cos2SigmaM = cosSqAlpha != 0 ? cosSigma - (2 * sinU1 * sinU2 / cosSqAlpha) : 0;
            double c = F / 16 * cosSqAlpha * (4 + (F * (4 - (3 * cosSqAlpha))));
            double previous = lambda;
            lambda = l + ((1 - c) * F * sinAlpha
                * (sigma + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))))));

            if (Math.Abs(lambda - previous) < 1e-12)
            {
                break;
            }

            if (++iterations > 200)
            {
                return null;
            }
        }

        double uSq = cosSqAlpha * ((A * A) - (B * B)) / (B * B);
        double bigA = 1 + (uSq / 16384 * (4096 + (uSq * (-768 + (uSq * (320 - (175 * uSq)))))));
        double bigB = uSq / 1024 * (256 + (uSq * (-128 + (uSq * (74 - (47 * uSq))))));
        double deltaSigma = bigB * sinSigma * (cos2SigmaM + (bigB / 4 * ((cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))
            - (bigB / 6 * cos2SigmaM * (-3 + (4 * sinSigma * sinSigma)) * (-3 + (4 * cos2SigmaM * cos2SigmaM))))));

        return B * bigA * (sigma - deltaSigma);
    }

    private static double Haversine(double lon1, double lat1, double lon2, double lat2)
    {
        const double R = 6_371_008.8;
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
        double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
        double h = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
