using System;
using System.Linq;

namespace Graticula.Geometries;

/// <summary>
/// <c>geometryPrecision</c> for a provider that rounds in this process — a GeoParquet layer (ADR-066) and an Oracle one
/// (ADR-205) — rather than in PostGIS.
/// </summary>
public static class GeometryRounding
{
    /// <summary>Rounds every coordinate to a number of decimal places.</summary>
    /// <remarks>
    /// What <c>geometryPrecision</c> means in the ArcGIS REST documentation: decimal places.
    /// PostGIS's path snaps to a grid with <c>st_reduceprecision</c>, which can also repair or
    /// drop a part a rounding collapsed; this rounds and changes nothing else, so the two agree
    /// on every coordinate and may differ on a sliver that rounding closes.
    /// </remarks>
    public static Geometry Round(Geometry geometry, int places)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        places = Math.Clamp(places, 0, 15);

        XySequence Sequence(XySequence coordinates)
        {
            double[] values = coordinates.ToInterleavedArray();

            for (int i = 0; i < values.Length; i++)
            {
                values[i] = Math.Round(values[i], places, MidpointRounding.AwayFromZero);
            }

            return XySequence.Wrap(values);
        }

        LinearRing Ring(LinearRing ring) => ring.IsEmpty ? ring : new LinearRing(Sequence(ring.Coordinates));

        Polygon Area(Polygon polygon) =>
            polygon.IsEmpty ? polygon : new Polygon(Ring(polygon.Shell), [.. polygon.Holes.Select(Ring)]);

        return geometry switch
        {
            { IsEmpty: true } => geometry,
            Point p => new Point(
                Math.Round(p.X, places, MidpointRounding.AwayFromZero),
                Math.Round(p.Y, places, MidpointRounding.AwayFromZero)),
            LinearRing ring => Ring(ring),
            LineString line => new LineString(Sequence(line.Coordinates)),
            Polygon polygon => Area(polygon),
            MultiPoint multi => new MultiPoint([.. multi.Parts.Select(p => (Point)Round(p, places))]),
            MultiLineString multi => new MultiLineString([.. multi.Parts.Select(l => (LineString)Round(l, places))]),
            MultiPolygon multi => new MultiPolygon([.. multi.Parts.Select(Area)]),
            _ => geometry,
        };
    }
}
