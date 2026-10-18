using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Host;

/// <summary>
/// Geodesic measurement — ADR-198, reopening ADR-145 §2's Alternative B by owner decision of 2026-10-18 (V-90).
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured by the datastore, on the ellipsoid of the reference's own geographic system.</b> PostGIS's
/// <c>geography</c> computes lengths, areas and distances with Karney's algorithms (GeographicLib), which is the Tier 2
/// library this would otherwise have adopted; the geometry is moved into its reference's geographic system — not into
/// WGS 84, so no datum shift enters a measure — and cast. The datastore pool is the one the transformations already
/// use (ADR-160), so nothing new is configured.
/// </para>
/// <para>
/// <b>Three calculations, as ArcGIS names them.</b> <c>planar</c> is ADR-145's, unchanged. <c>geodesic</c> (or
/// <c>geodesic=true</c>) measures each edge as the shortest path on the ellipsoid. <c>preserveShape</c> measures the
/// shape as it is drawn in the reference: its edges are densified in the reference's plane first, to a thousandth of
/// the shape's diagonal, and the densified shape is measured on the ellipsoid.
/// </para>
/// <para>
/// <b>A geodesic buffer is PostGIS's geography buffer</b>, made in a projection the datastore picks for the shape
/// (a UTM zone, or a polar or Lambert azimuthal one) and returned to the ellipsoid. It is not Esri's true geodesic
/// buffer; within one zone the difference is well under the width of a line on any map, and across a hemisphere it is
/// not. ADR-198 §3 records it.
/// </para>
/// </remarks>
internal static partial class GeometryServerEndpoints
{
    /// <summary>How a measure is computed.</summary>
    private enum Calculation
    {
        /// <summary>In the plane of the reference — ADR-145.</summary>
        Planar,

        /// <summary>Each edge as a geodesic on the ellipsoid.</summary>
        Geodesic,

        /// <summary>The shape as drawn in the reference, measured on the ellipsoid.</summary>
        PreserveShape,
    }

    private const string GeodesicNote =
        "Measured on the ellipsoid of the reference's geographic system (PostGIS geography, Karney's algorithms).";

    /// <summary>The calculation a request asks for — <c>geodesic=true</c> or <c>calculationType</c> — or why not.</summary>
    private static (Calculation Calculation, string? Error) CalculationOf(IFormCollection form)
    {
        string? type = Field(form, "calculationType");

        if (type is null)
        {
            return (string.Equals(Field(form, "geodesic"), "true", StringComparison.OrdinalIgnoreCase)
                ? Calculation.Geodesic
                : Calculation.Planar, null);
        }

        return type.ToLowerInvariant() switch
        {
            "planar" => (Calculation.Planar, null),
            "geodesic" => (Calculation.Geodesic, null),
            "preserveshape" => (Calculation.PreserveShape, null),
            _ => (Calculation.Planar, $"`calculationType={type}` is not one this geometry service knows: planar, geodesic "
                + "or preserveShape."),
        };
    }

    /// <summary>A measure that is to be made on the ellipsoid: the request, its reference and how.</summary>
    private sealed record Ellipsoidal(IFormCollection Form, int Srid, Calculation Calculation);

    /// <summary>
    /// The request when <c>lengths</c>, <c>areasAndLengths</c> or <c>densify</c> is to be measured on the ellipsoid —
    /// a geodesic or preserveShape calculation, or a linear unit in a geographic reference — and null when it is planar
    /// or cannot be read, which the planar path then answers or refuses.
    /// </summary>
    private static async Task<Ellipsoidal?> EllipsoidalAsync(HttpContext context, string operation, CancellationToken cancellation)
    {
        if (!TryForm(context, out IFormCollection form, out _)
            || !TrySrid(form, operation == "densify" ? "sr" : SridField(form), out int srid, out _))
        {
            return null;
        }

        (Calculation calculation, string? error) = CalculationOf(form);

        if (error is not null)
        {
            return null;
        }

        if (calculation == Calculation.Planar)
        {
            bool geographic = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false) is { Angular: true };
            bool metres = await LinearInGeographicAsync(context, form, "lengthUnit", srid, cancellation).ConfigureAwait(false)
                || (operation == "areasAndLengths" && geographic && Field(form, "areaUnit") is not null);

            if (!metres)
            {
                return null;
            }

            calculation = Calculation.Geodesic;
        }

        return new Ellipsoidal(form, srid, calculation);
    }

    /// <summary>Whether a linear unit named in a field is linear while the reference is geographic.</summary>
    /// <remarks>
    /// <b>Metres in degrees can only mean a measure on the ellipsoid</b>, and ADR-145 refused it as one; with a geodesic
    /// calculation to hand, a buffer or a measure asked that way is answered geodesically (ADR-198 §2, INFERRED).
    /// </remarks>
    private static async Task<bool> LinearInGeographicAsync(
        HttpContext context, IFormCollection form, string field, int srid, CancellationToken cancellation)
    {
        if (Field(form, field) is not { } text || !LinearUnits.ContainsKey(UnitKey(text, "unit")))
        {
            return false;
        }

        return await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false) is { Angular: true };
    }

    /// <summary>
    /// The metres in one of the unit a field names for a geodesic answer — the named linear unit, or else the
    /// reference's own unit when it is linear, and metres when it is angular — or why not.
    /// </summary>
    private static async Task<(double Metres, string? Error)> GeodesicUnitAsync(
        HttpContext context, IFormCollection form, string field, int srid, CancellationToken cancellation)
    {
        if (Field(form, field) is { } text)
        {
            string key = UnitKey(text, "unit");

            if (LinearUnits.TryGetValue(key, out (string Name, double Metres) unit))
            {
                return (unit.Metres, null);
            }

            return (0, $"`{field}={text}` is not a linear unit, and a geodesic measure is a distance on the ellipsoid. Name "
                + "metres (9001), kilometres (9036), feet (9002), US survey feet (9003), miles (9093), nautical miles (9030), "
                + "yards (9096) or centimetres (1033).");
        }

        ReferenceUnit? reference = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false);
        return (reference is { Angular: false } ? reference.Metres : 1, null);
    }

    /// <summary>The square metres in one of the unit <c>areaUnit</c> names for a geodesic area, or why not.</summary>
    private static async Task<(double SquareMetres, string? Error)> GeodesicAreaUnitAsync(
        HttpContext context, IFormCollection form, int srid, CancellationToken cancellation)
    {
        if (Field(form, "areaUnit") is { } text)
        {
            return AreaUnits.TryGetValue(UnitKey(text, "areaUnit"), out (string Name, double SquareMetres) unit)
                ? (unit.SquareMetres, null)
                : (0, $"`areaUnit={text}` is not an area unit this geometry service knows. It knows esriSquareMeters, "
                    + "esriSquareKilometers, esriHectares, esriAres, esriAcres, esriSquareFeet, esriSquareYards, "
                    + "esriSquareMiles and esriSquareUsFeet.");
        }

        ReferenceUnit? reference = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false);
        return (reference is { Angular: false } ? reference.Metres * reference.Metres : 1, null);
    }

    /// <summary>The geographic system a reference is built on, or why it cannot be measured on an ellipsoid.</summary>
    private static async Task<(int Geographic, string? Error)> EllipsoidOfAsync(
        NpgsqlDataSource db, int srid, CancellationToken cancellation)
    {
        int? geographic = await GeographicOfAsync(db, Plain(srid), cancellation).ConfigureAwait(false);

        return geographic is { } g
            ? (g, null)
            : (0, $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)} names no geographic system the datastore knows, so "
                + "there is no ellipsoid to measure on. Measure it planar, or send it in a reference the datastore knows.");
    }

    /// <summary>The SQL that puts each input on the ellipsoid as <c>geog</c>, densified first for preserveShape.</summary>
    private static string OnTheEllipsoid(Calculation calculation) =>
        "with input as (select t.n, ST_SetSRID(ST_GeomFromWKB(t.g), @srid) as shape "
        + "from unnest(@geometries) with ordinality as t(g, n)), "
        + "drawn as (select n, "
        + (calculation == Calculation.PreserveShape
            ? "ST_Segmentize(shape, greatest(sqrt(power(ST_XMax(shape) - ST_XMin(shape), 2) "
                + "+ power(ST_YMax(shape) - ST_YMin(shape), 2)) / 1000, 1e-9))"
            : "shape")
        + " as shape from input) "
        + "select n, ST_Dimension(shape) as dimension, ST_Transform(shape, @geographic)::geography as geog from drawn";

    private static NpgsqlParameter Geometries(IEnumerable<Geometry> geometries) =>
        new("geometries", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = geometries.Select(g => WkbWriter.ToArray(g)).ToArray(),
        };

    /// <summary>Each geometry's geodesic length (a polygon's perimeter) and area, in metres and square metres.</summary>
    private static async Task<(List<(double Length, double Area)>? Measures, string? Error)> MeasureOnTheEllipsoidAsync(
        HttpContext context, List<Geometry> geometries, int srid, Calculation calculation, CancellationToken cancellation)
    {
        NpgsqlDataSource db = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        (int geographic, string? error) = await EllipsoidOfAsync(db, srid, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            return (null, error);
        }

        await using NpgsqlCommand command = db.CreateCommand(
            $"select case when dimension = 2 then ST_Perimeter(geog) else ST_Length(geog) end, ST_Area(geog) "
            + $"from ({OnTheEllipsoid(calculation)}) e order by n");
        command.Parameters.AddWithValue("srid", Plain(srid));
        command.Parameters.AddWithValue("geographic", geographic);
        command.Parameters.Add(Geometries(geometries));

        try
        {
            List<(double, double)> measures = new(geometries.Count);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                measures.Add((reader.GetDouble(0), reader.GetDouble(1)));
            }

            return (measures, null);
        }
        catch (PostgresException e)
        {
            return (null, $"The datastore could not measure on the ellipsoid: {e.MessageText}");
        }
    }

    /// <summary><c>areasAndLengths</c> and <c>lengths</c> on the ellipsoid, answered in the units asked.</summary>
    private static async Task GeodesicMeasuresAsync(
        HttpContext context, string operation, List<Geometry> geometries, IFormCollection form, int srid,
        Calculation calculation, CancellationToken cancellation)
    {
        (double lengthMetres, string? lengthError) = await GeodesicUnitAsync(context, form, "lengthUnit", srid, cancellation)
            .ConfigureAwait(false);
        (double areaMetres, string? areaError) = operation == "areasAndLengths"
            ? await GeodesicAreaUnitAsync(context, form, srid, cancellation).ConfigureAwait(false)
            : (1, null);

        if ((lengthError ?? areaError) is { } unitError)
        {
            await Fail(context, unitError).ConfigureAwait(false);
            return;
        }

        (List<(double Length, double Area)>? measures, string? error) =
            await MeasureOnTheEllipsoidAsync(context, geometries, srid, calculation, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await Fail(context, error).ConfigureAwait(false);
            return;
        }

        double[] lengths = [.. measures!.Select(m => m.Length / lengthMetres)];

        await Respond(context, operation, operation == "areasAndLengths"
            ? new { areas = measures!.Select(m => m.Area / areaMetres).ToArray(), lengths, note = GeodesicNote }
            : new { lengths, note = GeodesicNote }).ConfigureAwait(false);
    }

    /// <summary>The geodesic distance between two geometries, in the unit <c>distanceUnit</c> names.</summary>
    private static async Task GeodesicDistanceAsync(
        HttpContext context, IFormCollection form, Geometry first, Geometry second, int srid, CancellationToken cancellation)
    {
        (double unitMetres, string? unitError) = await GeodesicUnitAsync(context, form, "distanceUnit", srid, cancellation)
            .ConfigureAwait(false);

        if (unitError is not null)
        {
            await Fail(context, unitError).ConfigureAwait(false);
            return;
        }

        NpgsqlDataSource db = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        (int geographic, string? error) = await EllipsoidOfAsync(db, srid, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await Fail(context, error).ConfigureAwait(false);
            return;
        }

        await using NpgsqlCommand command = db.CreateCommand(
            $"select ST_Distance(a.geog, b.geog) from ({OnTheEllipsoid(Calculation.Geodesic)}) a, "
            + $"({OnTheEllipsoid(Calculation.Geodesic)}) b where a.n = 1 and b.n = 2");
        command.Parameters.AddWithValue("srid", Plain(srid));
        command.Parameters.AddWithValue("geographic", geographic);
        command.Parameters.Add(Geometries([first, second]));

        try
        {
            double metres = Convert.ToDouble(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            await Respond(context, "distance", new { distance = metres / unitMetres, note = GeodesicNote })
                .ConfigureAwait(false);
        }
        catch (PostgresException e)
        {
            await Fail(context, $"The datastore could not measure on the ellipsoid: {e.MessageText}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A buffer on the ellipsoid: each geometry buffered by a distance in metres through PostGIS's geography buffer,
    /// merged when asked, and answered in <c>outSR</c> or the request's own reference.
    /// </summary>
    private static async Task GeodesicBufferAsync(
        HttpContext context, IFormCollection form, IReadOnlyList<Geometry> geometries, double distance, bool union, int srid,
        CancellationToken cancellation)
    {
        // The distance's unit: named, or metres in a geographic reference, or the reference's own.
        (double unitMetres, string? unitError) = await GeodesicUnitAsync(context, form, "unit", srid, cancellation)
            .ConfigureAwait(false);

        if (unitError is not null)
        {
            await Fail(context, unitError).ConfigureAwait(false);
            return;
        }

        int answer = NamedReference(form, "outSR") ?? srid;
        NpgsqlDataSource db = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        (int geographic, string? error) = await EllipsoidOfAsync(db, srid, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await Fail(context, error).ConfigureAwait(false);
            return;
        }

        string buffered = "ST_Transform(ST_Buffer(geog, @metres, 'quad_segs=32')::geometry, @answer)";

        await using NpgsqlCommand command = db.CreateCommand(union
            ? $"select ST_AsBinary(ST_Union({buffered})) from ({OnTheEllipsoid(Calculation.Geodesic)}) e"
            : $"select ST_AsBinary({buffered}) from ({OnTheEllipsoid(Calculation.Geodesic)}) e order by n");
        command.Parameters.AddWithValue("srid", Plain(srid));
        command.Parameters.AddWithValue("geographic", geographic);
        command.Parameters.AddWithValue("metres", distance * unitMetres);
        command.Parameters.AddWithValue("answer", Plain(answer));
        command.Parameters.Add(Geometries(geometries));

        try
        {
            List<Geometry> buffers = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                if (!await reader.IsDBNullAsync(0, cancellation).ConfigureAwait(false))
                {
                    buffers.Add(WkbReader.Read((byte[])reader.GetValue(0)));
                }
            }

            await Respond(context, "buffer", new
            {
                geometries = buffers.Select(g => ToJson(g, answer)).ToArray(),
                note = GeodesicNote + " A buffer is made in a projection the datastore picks for each shape.",
            }).ConfigureAwait(false);
        }
        catch (PostgresException e)
        {
            await Fail(context, $"The datastore could not buffer on the ellipsoid: {e.MessageText}").ConfigureAwait(false);
        }
    }

    /// <summary>A densify along geodesics: no segment longer than the step, in the unit asked or metres.</summary>
    private static async Task<(IReadOnlyList<Geometry>? Densified, string? Error)> GeodesicDensifyAsync(
        HttpContext context, IFormCollection form, IReadOnlyList<Geometry> geometries, double step, int srid,
        CancellationToken cancellation)
    {
        (double unitMetres, string? unitError) = await GeodesicUnitAsync(context, form, "lengthUnit", srid, cancellation)
            .ConfigureAwait(false);

        if (unitError is not null)
        {
            return (null, unitError);
        }

        NpgsqlDataSource db = context.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        (int geographic, string? error) = await EllipsoidOfAsync(db, srid, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            return (null, error);
        }

        await using NpgsqlCommand command = db.CreateCommand(
            $"select ST_AsBinary(ST_Transform(ST_Segmentize(geog, @metres)::geometry, @srid)) "
            + $"from ({OnTheEllipsoid(Calculation.Geodesic)}) e order by n");
        command.Parameters.AddWithValue("srid", Plain(srid));
        command.Parameters.AddWithValue("geographic", geographic);
        command.Parameters.AddWithValue("metres", step * unitMetres);
        command.Parameters.Add(Geometries(geometries));

        try
        {
            List<Geometry> densified = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                densified.Add(WkbReader.Read((byte[])reader.GetValue(0)));
            }

            return (densified, null);
        }
        catch (PostgresException e)
        {
            return (null, $"The datastore could not densify on the ellipsoid: {e.MessageText}");
        }
    }
}
