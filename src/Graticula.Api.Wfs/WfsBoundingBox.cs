using System;
using System.Globalization;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Reads the <c>bbox</c> parameter, which is the filter almost every client sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own reader because its axis order is decided by a value inside it.</b>
/// The parameter is four numbers and an optional CRS — <c>bbox=a,b,c,d,urn:…::4326</c>
/// — and whether the first number is a longitude or a latitude depends on the
/// fifth field. A reader that assumed one order would work for web-Mercator maps
/// and put every WGS 84 map in the wrong place, which is the failure Q-96 already
/// recorded once for tiles.
/// </para>
/// <para>
/// <b>The default when no CRS is given is the layer's own.</b> WFS 2.0 says the
/// default is the layer's default CRS, which is what this server advertises in
/// the capabilities, so a client that omits it gets what it saw there.
/// </para>
/// </remarks>
public static class WfsBoundingBox
{
    /// <summary>Reads a bbox parameter into a spatial filter.</summary>
    /// <param name="text">The parameter value.</param>
    /// <param name="defaultSrid">The layer's own reference.</param>
    /// <param name="filter">The restriction it describes.</param>
    /// <param name="srid">The reference its geometry is in.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryParse(
        string? text,
        int defaultSrid,
        out SpatialFilter? filter,
        out int srid,
        out WfsFault? fault)
    {
        filter = null;
        fault = null;
        srid = defaultSrid;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length is not (4 or 5))
        {
            fault = WfsFault.Invalid(
                "bbox",
                $"A bbox is four numbers and an optional coordinate reference, and '{text}' has "
                + $"{parts.Length} field(s).");

            return false;
        }

        double[] numbers = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                    parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                fault = WfsFault.Invalid("bbox", $"'{parts[i]}' is not a number.");
                return false;
            }
        }

        if (parts.Length == 5)
        {
            if (!GmlGeometryReader.TrySrsName(parts[4], out srid))
            {
                fault = WfsFault.Invalid(
                    "bbox",
                    $"'{parts[4]}' is not a coordinate reference this server recognises. Use "
                    + "urn:ogc:def:crs:EPSG::<code>.");

                return false;
            }
        }

        bool latitudeFirst = WfsNames.IsLatitudeFirst(srid);

        (double minX, double minY) = latitudeFirst
            ? (numbers[1], numbers[0])
            : (numbers[0], numbers[1]);

        (double maxX, double maxY) = latitudeFirst
            ? (numbers[3], numbers[2])
            : (numbers[2], numbers[3]);

        if (maxX < minX || maxY < minY)
        {
            fault = WfsFault.Invalid(
                "bbox",
                "A bbox's upper corner is below or left of its lower corner. Written in the "
                + "order the coordinate reference defines, it reads "
                + $"({minX.ToString(CultureInfo.InvariantCulture)}, "
                + $"{minY.ToString(CultureInfo.InvariantCulture)}) to "
                + $"({maxX.ToString(CultureInfo.InvariantCulture)}, "
                + $"{maxY.ToString(CultureInfo.InvariantCulture)}).");

            return false;
        }

        // EnvelopeIntersects rather than Intersects: a bbox is the index test, and
        // WFS defines it as an envelope comparison rather than an exact one. Asking
        // for the exact relation would be a different and slower question than the
        // client asked.
        filter = new SpatialFilter(
            new Polygon(new LinearRing(XySequence.Wrap(
            [
                minX, minY,
                maxX, minY,
                maxX, maxY,
                minX, maxY,
                minX, minY,
            ]))),
            SpatialRelation.EnvelopeIntersects);

        return true;
    }
}
