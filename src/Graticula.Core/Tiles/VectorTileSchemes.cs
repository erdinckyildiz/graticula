using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>One built-in tiling scheme: what it is called, and the ground it was derived from.</summary>
/// <param name="Id">Its name, as a request and the catalogue carry it.</param>
/// <param name="Title">What a person reads.</param>
/// <param name="AreaOfUse">The reference's area of use in degrees, as the EPSG register states it.</param>
/// <param name="Projected">That area projected into the reference — the envelope of its edges sampled 65 times each.</param>
/// <param name="Scheme">The grid <see cref="VectorTileScheme.Derive"/> makes of it.</param>
public sealed record BuiltInTileScheme(
    string Id, string Title, Envelope AreaOfUse, Envelope Projected, VectorTileScheme Scheme);

/// <summary>
/// The tiling schemes this server offers by name — ADR-096 §5.2: the Turkish national grids.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's motivating case, and the only built-ins.</b> TUREF — Turkey's realisation of ITRF96 —
/// in its seven 3° transverse Mercator zones, EPSG:5253 to 5259 (TM27 to TM45, false easting 500 km), and
/// the same seven zones as the register's 3-degree Gauss-Krüger codes EPSG:5269 to 5275, whose only
/// difference is the zone number in front of the easting (zone 10 is 10,500,000 where TM30 is 500,000).
/// Which of the two a basemap is drawn in is its publisher's choice, and both are in use. *INFERRED*: the
/// owner named *TUREF TM zones and ITRF96-based ones*; TUREF is the ITRF96 realisation, and these two
/// families are every TUREF projected zone the register has. Anything else — ED50 / TM30 (EPSG:2320), a
/// custom grid over a municipality — is a custom scheme, by its numbers or by its code alone.
/// </para>
/// <para>
/// <b>Frozen numbers, not a lookup made at start.</b> <see cref="VectorTileScheme.Derive"/> is run here over
/// an envelope written down from the EPSG register's area of use (register v12.013, read from
/// <c>proj.db</c> 2026-09-29), projected with the Krüger series on GRS80 and checked against PostGIS by
/// <c>ATileSchemeIsCutInItsOwnReferenceTests</c>. Deriving at start from the deployment's own PROJ would
/// make the grid — and so every tile address a client has cached — depend on which PROJ a server was
/// installed with; a register update that nudged an area of use by a hundredth of a degree would move a
/// kilometre-rounded origin rarely, but rarely is not never.
/// </para>
/// </remarks>
public static class VectorTileSchemes
{
    /// <summary>Every built-in, TM zones first, west to east.</summary>
    public static IReadOnlyList<BuiltInTileScheme> BuiltIn { get; } = Make();

    /// <summary>A built-in by name, without case, or null.</summary>
    /// <param name="id">The name.</param>
    /// <returns>The built-in, or null.</returns>
    public static BuiltInTileScheme? Find(string? id) =>
        id is null
            ? null
            : BuiltIn.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The built-ins laid out in a reference, for a console that offers the one that matches a service's data.</summary>
    /// <param name="srid">The reference.</param>
    /// <returns>The built-ins in it — none, or one.</returns>
    public static IEnumerable<BuiltInTileScheme> In(int srid) => BuiltIn.Where(b => b.Scheme.Srid == srid);

    private static BuiltInTileScheme[] Make()
    {
        // (EPSG TM code, central meridian, area of use W S E N in degrees, projected envelope in the TM zone).
        // The projected envelopes are the Krüger-series projection of each area's four edges, 65 samples an
        // edge; the southern edge's minimum is on the central meridian, the northern corners are the maximum.
        (int Code, int Meridian, double W, double S, double E, double N,
            double MinX, double MinY, double MaxX, double MaxY)[] zones =
        [
            (5253, 27, 25.62, 36.50, 28.50, 42.11, 376360.753, 4041024.597, 634391.181, 4664944.163),
            (5254, 30, 28.50, 36.06, 31.50, 41.46, 364852.207, 3992200.244, 635147.793, 4592746.422),
            (5255, 33, 31.50, 35.97, 34.50, 42.07, 364698.428, 3982213.909, 635301.572, 4660501.004),
            (5256, 36, 34.50, 35.81, 37.50, 42.15, 364425.870, 3964460.795, 635574.130, 4669387.350),
            (5257, 39, 37.50, 36.66, 40.50, 41.19, 365885.923, 4058779.658, 634114.077, 4562758.834),
            (5258, 42, 40.50, 37.02, 43.50, 41.60, 366513.237, 4098730.531, 633486.763, 4608296.059),
            (5259, 45, 43.50, 36.97, 44.83, 41.02, 366425.794, 4093195.160, 485701.340, 4543878.435),
        ];

        List<BuiltInTileScheme> made = [];

        foreach (var zone in zones)
        {
            Envelope degrees = new(zone.W, zone.S, zone.E, zone.N);
            Envelope tm = new(zone.MinX, zone.MinY, zone.MaxX, zone.MaxY);

            made.Add(Built($"turef-tm{zone.Meridian}", $"TUREF / TM{zone.Meridian}", zone.Code, degrees, tm));
        }

        foreach (var zone in zones)
        {
            // Gauss-Krüger zone n is the same projection with n × 1,000,000 in front of the easting.
            int number = zone.Meridian / 3;
            double shift = number * 1_000_000.0;
            Envelope degrees = new(zone.W, zone.S, zone.E, zone.N);
            Envelope gk = new(zone.MinX + shift, zone.MinY, zone.MaxX + shift, zone.MaxY);

            made.Add(Built(
                $"turef-gk{number}", $"TUREF / 3-degree Gauss-Kruger zone {number}", zone.Code + 16, degrees, gk));
        }

        return [.. made];
    }

    private static BuiltInTileScheme Built(string id, string title, int srid, Envelope degrees, Envelope projected)
    {
        string? wrong = VectorTileScheme.Derive(id, srid, projected, out VectorTileScheme? scheme);

        return wrong is null
            ? new BuiltInTileScheme(id, title, degrees, projected, scheme!)
            : throw new InvalidOperationException($"The built-in scheme {id} does not derive: {wrong}");
    }
}
