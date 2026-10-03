using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Mensuration on an image service — ADR-155: ArcGIS's <c>measure</c> (a point, a distance and its azimuth, an area and
/// its perimeter, a centroid, and their 3D forms over an elevation model), and the image-space operations
/// <c>computePixelLocation</c>, <c>imageToMap</c> and <c>mapToImage</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the ellipsoid, whatever the image's reference.</b> A measurement is projected to WGS 84 and taken there —
/// Vincenty's distance and azimuth, an area by spherical excess on the authalic sphere — so Web Mercator's stretched
/// metres are not reported as ground.
/// </para>
/// <para>
/// <b>Heights from shadows and from a building's base and top are refused</b>: they need the camera's position, a
/// sensor model this server's images do not have — every image here looks straight down.
/// </para>
/// </remarks>
internal static partial class ImageServerEndpoints
{
    private const double SemiMajor = 6_378_137.0;
    private const double Flattening = 1 / 298.257223563;
    private const double AuthalicRadius = 6_371_007.181;

    private static readonly Dictionary<string, (double Metres, string Name)> LinearUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esriMeters"] = (1, "Meters"),
        ["esriKilometers"] = (1000, "Kilometers"),
        ["esriFeet"] = (0.3048, "Feet"),
        ["esriYards"] = (0.9144, "Yards"),
        ["esriMiles"] = (1609.344, "Miles"),
        ["esriNauticalMiles"] = (1852, "Nautical Miles"),
        ["esriCentimeters"] = (0.01, "Centimeters"),
    };

    private static readonly Dictionary<string, (double SquareMetres, string Name)> AreaUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esriSquareMeters"] = (1, "Square Meters"),
        ["esriSquareKilometers"] = (1_000_000, "Square Kilometers"),
        ["esriHectares"] = (10_000, "Hectares"),
        ["esriAcres"] = (4046.8564224, "Acres"),
        ["esriSquareFeet"] = (0.09290304, "Square Feet"),
        ["esriSquareMiles"] = (2_589_988.110336, "Square Miles"),
    };

    /// <summary>ArcGIS's <c>measure</c>.</summary>
    private static async Task MeasureOperationAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, ICoverageReaderFactory readers, IProjector projector,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        string operation = parameter("measureOperation") ?? "esriMensurationPoint";
        string linearName = parameter("linearUnit") is { Length: > 0 } l ? l : "esriMeters";
        string areaName = parameter("areaUnit") is { Length: > 0 } a ? a : "esriSquareMeters";
        bool radians = string.Equals(parameter("angularUnit"), "esriRadians", StringComparison.OrdinalIgnoreCase);

        if (!LinearUnits.TryGetValue(linearName, out (double Metres, string Name) linear))
        {
            await RefuseAsync(context, 400, $"`linearUnit` '{linearName}' is not one this server converts to: {string.Join(", ", LinearUnits.Keys)}.")
                .ConfigureAwait(false);
            return;
        }

        if (!AreaUnits.TryGetValue(areaName, out (double SquareMetres, string Name) area))
        {
            await RefuseAsync(context, 400, $"`areaUnit` '{areaName}' is not one this server converts to: {string.Join(", ", AreaUnits.Keys)}.")
                .ConfigureAwait(false);
            return;
        }

        if (operation.Contains("Height", StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, 400, $"`{operation}` measures a height from a shadow or from a base and a top, which needs "
                + "the camera's position — a sensor model this image does not have. Its images look straight down; measure a "
                + "height on an elevation model with esriMensurationPoint3D.").ConfigureAwait(false);
            return;
        }

        (Geometry? from, string? error) = await AreaGeometryAsync(Rename(parameter, "fromGeometry"), coverage.Info, projector, cancellation)
            .ConfigureAwait(false);

        if (from is null)
        {
            await RefuseAsync(context, 400, error!.Replace("`geometry`", "`fromGeometry`", StringComparison.Ordinal)).ConfigureAwait(false);
            return;
        }

        int srid = coverage.Info.Srid;
        object reference = new { wkid = srid, latestWkid = srid };
        bool threeD = operation.EndsWith("3D", StringComparison.OrdinalIgnoreCase);

        if (threeD && coverage.Info.Bands.Count != 1)
        {
            await RefuseAsync(context, 400, $"`{operation}` reads heights from an elevation model, one band of them; this image "
                + $"has {coverage.Info.Bands.Count.ToString(CultureInfo.InvariantCulture)} bands.").ConfigureAwait(false);
            return;
        }

        async Task<double?> HeightAsync(double x, double y)
        {
            CoverageInfo info = coverage.Info;

            if (x < info.Extent.MinX || x > info.Extent.MaxX || y < info.Extent.MinY || y > info.Extent.MaxY)
            {
                return null;
            }

            int column = Math.Clamp((int)((x - info.Extent.MinX) / info.PixelWidth), 0, info.Width - 1);
            int row = Math.Clamp((int)((info.Extent.MaxY - y) / info.PixelHeight), 0, info.Height - 1);
            using ICoverageReader reader = await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
            double value = (await reader.ReadAsync(0, column, row, 1, 1, cancellation).ConfigureAwait(false)).Samples[0];
            return double.IsNaN(value) || value == info.Bands[0].NoData ? null : value;
        }

        object Measured(double value, string unit) => new
        {
            value,
            displayValue = value.ToString("0.###", CultureInfo.InvariantCulture) + " " + unit,
            uncertainty = (double?)null,
            unit,
        };

        switch (operation)
        {
            case "esriMensurationPoint" or "esriMensurationPoint3D" when from is Point point:
            {
                double? z = threeD ? await HeightAsync(point.X, point.Y).ConfigureAwait(false) : null;

                if (threeD && z is null)
                {
                    await RefuseAsync(context, 400, "The point has no height: it is off the elevation model, or on no data.").ConfigureAwait(false);
                    return;
                }

                await Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    name = coverage.QualifiedName,
                    sensorName = string.Empty,
                    point = threeD ? (object)new { x = point.X, y = point.Y, z, spatialReference = reference } : new { x = point.X, y = point.Y, spatialReference = reference },
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            case "esriMensurationCentroid" or "esriMensurationCentroid3D" when from is Polygon or MultiPolygon:
            {
                (double cx, double cy) = Centroid(from);
                await Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    name = coverage.QualifiedName,
                    sensorName = string.Empty,
                    centroid = new { x = cx, y = cy, spatialReference = reference },
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            case "esriMensurationAreaAndPerimeter" or "esriMensurationAreaAndPerimeter3D" when from is Polygon or MultiPolygon:
            {
                List<List<(double Lon, double Lat)>> rings = [];

                foreach (IReadOnlyList<(double X, double Y)> ring in Rings(from))
                {
                    rings.Add(await ToGeographicAsync(ring, srid, projector, cancellation).ConfigureAwait(false));
                }

                double squareMetres = Math.Abs(rings.Take(1).Sum(RingArea)) - rings.Skip(1).Sum(r => Math.Abs(RingArea(r)));
                double metres = rings.Sum(r => r.Zip(r.Skip(1)).Sum(p => Inverse(p.First, p.Second).Distance));
                (double cx, double cy) = Centroid(from);

                await Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    name = coverage.QualifiedName,
                    sensorName = string.Empty,
                    area = Measured(squareMetres / area.SquareMetres, area.Name),
                    perimeter = Measured(metres / linear.Metres, linear.Name),
                    centroid = new { x = cx, y = cy, spatialReference = reference },
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            case "esriMensurationDistanceAndAngle" or "esriMensurationDistanceAndAngle3D" when from is Point start:
            {
                (Geometry? to, string? toError) = await AreaGeometryAsync(Rename(parameter, "toGeometry"), coverage.Info, projector, cancellation)
                    .ConfigureAwait(false);

                if (to is not Point end)
                {
                    await RefuseAsync(context, 400, toError?.Replace("`geometry`", "`toGeometry`", StringComparison.Ordinal)
                        ?? "`toGeometry` is the point the distance is measured to.").ConfigureAwait(false);
                    return;
                }

                List<(double Lon, double Lat)> both = await ToGeographicAsync([(start.X, start.Y), (end.X, end.Y)], srid, projector, cancellation)
                    .ConfigureAwait(false);
                (double ground, double azimuth) = Inverse(both[0], both[1]);
                double angle = radians ? azimuth * Math.PI / 180 : azimuth;
                string angleUnit = radians ? "Radians" : "Degrees";

                if (!threeD)
                {
                    await Microsoft.AspNetCore.Http.Results.Ok(new
                    {
                        name = coverage.QualifiedName,
                        sensorName = string.Empty,
                        distance = Measured(ground / linear.Metres, linear.Name),
                        azimuthAngle = Measured(angle, angleUnit),
                    }).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                double? z1 = await HeightAsync(start.X, start.Y).ConfigureAwait(false);
                double? z2 = await HeightAsync(end.X, end.Y).ConfigureAwait(false);

                if (z1 is null || z2 is null)
                {
                    await RefuseAsync(context, 400, "A point has no height: it is off the elevation model, or on no data.").ConfigureAwait(false);
                    return;
                }

                double rise = z2.Value - z1.Value;
                double elevation = Math.Atan2(rise, ground) * 180 / Math.PI;

                await Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    name = coverage.QualifiedName,
                    sensorName = string.Empty,
                    distance = Measured(Math.Sqrt((ground * ground) + (rise * rise)) / linear.Metres, linear.Name),
                    azimuthAngle = Measured(angle, angleUnit),
                    elevationAngle = Measured(radians ? elevation * Math.PI / 180 : elevation, angleUnit),
                    heightDifference = Measured(rise / linear.Metres, linear.Name),
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            default:
                await RefuseAsync(context, 400, $"`measureOperation` '{operation}' with a {from.GetType().Name.ToLowerInvariant()} is not a "
                    + "measurement this server takes: esriMensurationPoint and Point3D from a point; DistanceAndAngle and "
                    + "DistanceAndAngle3D from a point to a point; AreaAndPerimeter and Centroid from a polygon.").ConfigureAwait(false);
                return;
        }
    }

    /// <summary>A parameter read under another name — a measurement's <c>fromGeometry</c> as the area reader's <c>geometry</c>.</summary>
    private static Func<string, string?> Rename(Func<string, string?> parameter, string asGeometry) =>
        name => name.Equals("geometry", StringComparison.OrdinalIgnoreCase) ? parameter(asGeometry) : parameter(name);

    private static IEnumerable<IReadOnlyList<(double X, double Y)>> Rings(Geometry geometry) => geometry switch
    {
        Polygon polygon => new[] { polygon.Shell }.Concat(polygon.Holes)
            .Select(r => (IReadOnlyList<(double, double)>)[.. Enumerable.Range(0, r.Coordinates.Count).Select(i => (r.Coordinates.X(i), r.Coordinates.Y(i)))]),
        MultiPolygon many => many.Parts.SelectMany(Rings),
        _ => [],
    };

    private static (double X, double Y) Centroid(Geometry geometry)
    {
        IReadOnlyList<(double X, double Y)> ring = Rings(geometry).First();
        double twice = 0, cx = 0, cy = 0;

        for (int i = 0; i + 1 < ring.Count; i++)
        {
            double cross = (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
            twice += cross;
            cx += (ring[i].X + ring[i + 1].X) * cross;
            cy += (ring[i].Y + ring[i + 1].Y) * cross;
        }

        return twice == 0 ? ring[0] : (cx / (3 * twice), cy / (3 * twice));
    }

    private static async Task<List<(double Lon, double Lat)>> ToGeographicAsync(
        IReadOnlyList<(double X, double Y)> points, int srid, IProjector projector, CancellationToken cancellation)
    {
        if (srid == 4326)
        {
            return [.. points];
        }

        (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync([.. points.Select(p => new Point(p.X, p.Y))], srid, 4326, cancellation)
            .ConfigureAwait(false);
        return [.. projected.Cast<Point>().Select(p => (p.X, p.Y))];
    }

    /// <summary>A ring's area on the authalic sphere, square metres, signed by its winding.</summary>
    private static double RingArea(List<(double Lon, double Lat)> ring)
    {
        double sum = 0;

        for (int i = 0; i + 1 < ring.Count; i++)
        {
            double l1 = ring[i].Lon * Math.PI / 180, l2 = ring[i + 1].Lon * Math.PI / 180;
            double p1 = ring[i].Lat * Math.PI / 180, p2 = ring[i + 1].Lat * Math.PI / 180;
            sum += (l2 - l1) * (2 + Math.Sin(p1) + Math.Sin(p2));
        }

        return sum * AuthalicRadius * AuthalicRadius / 2;
    }

    /// <summary>Vincenty's inverse on WGS 84: the distance in metres and the initial azimuth in degrees clockwise from north.</summary>
    internal static (double Distance, double Azimuth) Inverse((double Lon, double Lat) a, (double Lon, double Lat) b)
    {
        double f = Flattening, semiMinor = SemiMajor * (1 - f);
        double l = (b.Lon - a.Lon) * Math.PI / 180;
        double u1 = Math.Atan((1 - f) * Math.Tan(a.Lat * Math.PI / 180)), u2 = Math.Atan((1 - f) * Math.Tan(b.Lat * Math.PI / 180));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
        double lambda = l, sinSigma = 0, cosSigma = 0, sigma = 0, cos2Alpha = 0, cos2SigmaM = 0, sinLambda = 0, cosLambda = 0;

        for (int i = 0; i < 200; i++)
        {
            sinLambda = Math.Sin(lambda);
            cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(Math.Pow(cosU2 * sinLambda, 2) + Math.Pow((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda), 2));

            if (sinSigma == 0)
            {
                return (0, 0);
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cos2Alpha = 1 - (sinAlpha * sinAlpha);
            cos2SigmaM = cos2Alpha == 0 ? 0 : cosSigma - (2 * sinU1 * sinU2 / cos2Alpha);
            double c = f / 16 * cos2Alpha * (4 + (f * (4 - (3 * cos2Alpha))));
            double previous = lambda;
            lambda = l + ((1 - c) * f * sinAlpha * (sigma + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))))));

            if (Math.Abs(lambda - previous) < 1e-12)
            {
                break;
            }
        }

        double uSquared = cos2Alpha * ((SemiMajor * SemiMajor) - (semiMinor * semiMinor)) / (semiMinor * semiMinor);
        double bigA = 1 + (uSquared / 16384 * (4096 + (uSquared * (-768 + (uSquared * (320 - (175 * uSquared)))))));
        double bigB = uSquared / 1024 * (256 + (uSquared * (-128 + (uSquared * (74 - (47 * uSquared))))));
        double deltaSigma = bigB * sinSigma * (cos2SigmaM + (bigB / 4 * ((cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))
            - (bigB / 6 * cos2SigmaM * (-3 + (4 * sinSigma * sinSigma)) * (-3 + (4 * cos2SigmaM * cos2SigmaM))))));
        double distance = semiMinor * bigA * (sigma - deltaSigma);
        double azimuth = Math.Atan2(cosU2 * sinLambda, (cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda)) * 180 / Math.PI;
        return (distance, (azimuth + 360) % 360);
    }

    /// <summary>
    /// ArcGIS's <c>computePixelLocation</c>: where ground points fall in an image of the catalog, as columns and rows of
    /// its full resolution, from its top-left corner.
    /// </summary>
    private static async Task PixelLocationAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, ICoverageReaderFactory readers, IProjector projector,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (await ImageOfAsync(context, coverage, parameter, readers, cancellation).ConfigureAwait(false) is not { } image)
        {
            return;
        }

        if (!TryPoints(parameter("geometries"), out List<(double X, double Y)> points, out int? srid, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        if (srid is { } given && given != coverage.Info.Srid)
        {
            (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync([.. points.Select(p => new Point(p.X, p.Y))], given, coverage.Info.Srid, cancellation)
                .ConfigureAwait(false);
            points = [.. projected.Cast<Point>().Select(p => (p.X, p.Y))];
        }

        double pixel = image.PixelSize;
        await Microsoft.AspNetCore.Http.Results.Ok(new
        {
            geometries = points.Select(p => new { x = (p.X - image.Extent.MinX) / pixel, y = (image.Extent.MaxY - p.Y) / pixel, z = 0d }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ArcGIS's <c>imageToMap</c> and <c>mapToImage</c>: a geometry's coordinates moved between an image's columns and rows
    /// and the map. This server's images are north-up grids, so the move is the grid's own.
    /// </summary>
    private static async Task ImageMapAsync(
        HttpContext context, string serviceName, bool toMap, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (await ImageOfAsync(context, coverage, parameter, readers, cancellation).ConfigureAwait(false) is not { } image)
        {
            return;
        }

        if (parameter("geometry") is not { Length: > 0 } text)
        {
            await RefuseAsync(context, 400, "`geometry` is the geometry to move, as Esri JSON.").ConfigureAwait(false);
            return;
        }

        double pixel = image.PixelSize;
        (double, double) Move(double x, double y) => toMap
            ? (image.Extent.MinX + (x * pixel), image.Extent.MaxY - (y * pixel))
            : ((x - image.Extent.MinX) / pixel, (image.Extent.MaxY - y) / pixel);

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            object moved = MoveJson(document.RootElement, Move);
            int srid = coverage.Info.Srid;
            await Microsoft.AspNetCore.Http.Results.Ok(new
            {
                geometry = moved,
                spatialReference = toMap ? new { wkid = srid, latestWkid = srid } : null,
            }).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            await RefuseAsync(context, 400, "`geometry` is a point, a polyline or a polygon as Esri JSON.").ConfigureAwait(false);
        }
    }

    /// <summary>A geometry's JSON with each x and y moved.</summary>
    private static Dictionary<string, object> MoveJson(JsonElement geometry, Func<double, double, (double, double)> move)
    {
        Dictionary<string, object> result = [];

        double[][] Path(JsonElement path) => [.. path.EnumerateArray().Select(p =>
        {
            (double x, double y) = move(p[0].GetDouble(), p[1].GetDouble());
            return new[] { x, y };
        })];

        if (geometry.TryGetProperty("x", out JsonElement px))
        {
            (double x, double y) = move(px.GetDouble(), geometry.GetProperty("y").GetDouble());
            result["x"] = x;
            result["y"] = y;
        }
        else if (geometry.TryGetProperty("paths", out JsonElement paths))
        {
            result["paths"] = paths.EnumerateArray().Select(Path).ToArray();
        }
        else if (geometry.TryGetProperty("rings", out JsonElement rings))
        {
            result["rings"] = rings.EnumerateArray().Select(Path).ToArray();
        }
        else
        {
            throw new FormatException("Not a point, polyline or polygon.");
        }

        return result;
    }

    /// <summary>The catalog image a request's <c>rasterId</c> names — the only one, for a service over one file.</summary>
    private static async Task<CatalogImage?> ImageOfAsync(
        HttpContext context, PublishedCoverage coverage, Func<string, string?> parameter, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        List<CatalogImage> images = await CatalogAsync(coverage, readers, cancellation).ConfigureAwait(false);

        if (parameter("rasterId") is not { Length: > 0 } text)
        {
            if (images.Count == 1)
            {
                return images[0];
            }

            await RefuseAsync(context, 400, "`rasterId` names the image of the mosaic, by its object id.").ConfigureAwait(false);
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && images.FirstOrDefault(i => i.Id == id) is { } image)
        {
            return image;
        }

        await RefuseAsync(context, 400, $"This image service has no image {text}.").ConfigureAwait(false);
        return null;
    }

    /// <summary>Points as ArcGIS's <c>geometries</c> sends them: a list, or <c>{geometryType, geometries}</c>.</summary>
    private static bool TryPoints(string? text, out List<(double X, double Y)> points, out int? srid, out string? error)
    {
        points = [];
        srid = null;
        error = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text ?? "[]");
            JsonElement list = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("geometries", out JsonElement inner)
                ? inner : document.RootElement;

            foreach (JsonElement point in list.EnumerateArray())
            {
                points.Add((point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble()));

                if (srid is null && point.TryGetProperty("spatialReference", out JsonElement reference) && reference.TryGetProperty("wkid", out JsonElement wkid))
                {
                    srid = wkid.GetInt32() == 102100 ? 3857 : wkid.GetInt32();
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            error = "`geometries` is a list of points as Esri JSON.";
            return false;
        }

        if (points.Count == 0)
        {
            error = "`geometries` names at least one point.";
            return false;
        }

        return true;
    }
}
