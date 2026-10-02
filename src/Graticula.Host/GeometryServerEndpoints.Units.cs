using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The units a geometry request names — ADR-145. A distance or a measure asked for in metres, feet or miles is converted
/// from and to the reference's own unit; one that cannot be honoured — a metre in a geographic reference, a geodesic
/// measure — is refused by name, where it used to be answered in the reference's units with a 200 (E2 of the ArcGIS
/// reviewer's GeometryServer pass: a 1,000-metre buffer in EPSG:4326 came back 1,000 degrees wide).
/// </summary>
internal static partial class GeometryServerEndpoints
{
    /// <summary>Esri's linear units, by code and by name, in metres.</summary>
    private static readonly Dictionary<string, (string Name, double Metres)> LinearUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["9001"] = ("metres", 1), ["esriMeters"] = ("metres", 1),
        ["9036"] = ("kilometres", 1000), ["esriKilometers"] = ("kilometres", 1000),
        ["9002"] = ("feet", 0.3048), ["esriFeet"] = ("feet", 0.3048), ["esriInternationalFeet"] = ("feet", 0.3048),
        ["9003"] = ("US survey feet", 1200.0 / 3937), ["esriUSSurveyFeet"] = ("US survey feet", 1200.0 / 3937),
        ["9093"] = ("miles", 1609.344), ["esriMiles"] = ("miles", 1609.344), ["esriStatuteMiles"] = ("miles", 1609.344),
        ["9035"] = ("US survey miles", 1609.347218694437), ["esriUSSurveyMiles"] = ("US survey miles", 1609.347218694437),
        ["9030"] = ("nautical miles", 1852), ["esriNauticalMiles"] = ("nautical miles", 1852),
        ["9096"] = ("yards", 0.9144), ["esriYards"] = ("yards", 0.9144),
        ["1033"] = ("centimetres", 0.01), ["esriCentimeters"] = ("centimetres", 0.01),
    };

    /// <summary>Esri's area units, by name, in square metres.</summary>
    private static readonly Dictionary<string, (string Name, double SquareMetres)> AreaUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esriSquareMeters"] = ("square metres", 1),
        ["esriSquareKilometers"] = ("square kilometres", 1e6),
        ["esriHectares"] = ("hectares", 1e4),
        ["esriAres"] = ("ares", 100),
        ["esriAcres"] = ("acres", 4046.8564224),
        ["esriSquareFeet"] = ("square feet", 0.09290304),
        ["esriSquareYards"] = ("square yards", 0.83612736),
        ["esriSquareMiles"] = ("square miles", 2589988.110336),
        ["esriSquareUsFeet"] = ("square US survey feet", Math.Pow(1200.0 / 3937, 2)),
    };

    /// <summary>
    /// A geodesic measure asked for — <c>geodesic=true</c>, or a <c>calculationType</c> other than planar — refused by
    /// name, since this service measures in the plane of the reference (ADR-022 §5); null when none is asked.
    /// </summary>
    private static string? GeodesicAsked(IFormCollection form)
    {
        if (string.Equals(Field(form, "geodesic"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return "`geodesic=true` asks for a measure on the ellipsoid, and this geometry service measures in the plane of "
                + "the reference. Send the geometry in a projected reference suited to the area (a UTM zone, or TM30 in "
                + "Türkiye) for distances that hold there, or omit `geodesic` for planar ones.";
        }

        string? type = Field(form, "calculationType");

        if (type is not null && !type.Equals("planar", StringComparison.OrdinalIgnoreCase))
        {
            return $"`calculationType={type}` asks for a measure on the ellipsoid, and this geometry service measures in the "
                + "plane of the reference: `calculationType=planar`. Send the geometry in a projected reference suited to "
                + "the area for measures that hold there.";
        }

        return null;
    }

    /// <summary>
    /// How many of the reference's units one unit named in a field is — the factor a distance in that unit is multiplied
    /// by — or 1 when the field is absent; refused when the unit is unknown, or linear in a geographic reference.
    /// </summary>
    private static async Task<(double Factor, string? Error)> InputScaleAsync(
        HttpContext context, IFormCollection form, string field, int srid, CancellationToken cancellation)
    {
        if (Field(form, field) is not { } text)
        {
            return (1, null);
        }

        (double unitMetres, string unitName, string? error) = await LinearAsync(context, text, field, srid, cancellation)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return (0, error);
        }

        ReferenceUnit reference = (await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false))!;
        _ = unitName;
        return (unitMetres / reference.Metres, null);
    }

    /// <summary>The metres in a linear unit named in a field, checked against the reference it applies to.</summary>
    private static async Task<(double Metres, string Name, string? Error)> LinearAsync(
        HttpContext context, string text, string field, int srid, CancellationToken cancellation)
    {
        string key = UnitKey(text, "unit");

        if (key == "9102" || key.Equals("esriDecimalDegrees", StringComparison.OrdinalIgnoreCase))
        {
            ReferenceUnit? own = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false);
            return own is { Angular: true }
                ? (own.Metres, "degrees", null)
                : (0, string.Empty, $"`{field}` names degrees, and EPSG:{srid.ToString(CultureInfo.InvariantCulture)} is not a geographic reference.");
        }

        if (!LinearUnits.TryGetValue(key, out (string Name, double Metres) unit))
        {
            return (0, string.Empty, $"`{field}={text}` is not a linear unit this geometry service knows. It knows metres (9001), "
                + "kilometres (9036), feet (9002), US survey feet (9003), miles (9093), US survey miles (9035), nautical miles "
                + "(9030), yards (9096) and centimetres (1033), by code or by Esri's name.");
        }

        ReferenceUnit? reference = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false);

        if (reference is null)
        {
            return (0, string.Empty, $"`{field}` names {unit.Name}, and this server cannot tell what unit "
                + $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)} is in, so it cannot convert. Omit it to work in the "
                + "reference's own units.");
        }

        if (reference.Angular)
        {
            return (0, string.Empty, $"`{field}` names {unit.Name}, and EPSG:{srid.ToString(CultureInfo.InvariantCulture)} is "
                + "geographic: its coordinates are degrees, and a distance in " + unit.Name + " there is a geodesic one, "
                + "which this geometry service does not measure. Send the geometry in a projected reference suited to the "
                + "area (a UTM zone, or TM30 in Türkiye), or omit the unit to work in degrees.");
        }

        return (unit.Metres, unit.Name, null);
    }

    /// <summary>
    /// The factor a length in the reference's units is multiplied by to be in the unit a field names, or 1 when absent.
    /// </summary>
    private static async Task<(double Factor, string? Error)> OutputScaleAsync(
        HttpContext context, IFormCollection form, string field, int srid, CancellationToken cancellation)
    {
        (double factor, string? error) = await InputScaleAsync(context, form, field, srid, cancellation).ConfigureAwait(false);
        return error is null ? (1 / factor, null) : (0, error);
    }

    /// <summary>
    /// The factor an area in the reference's squared units is multiplied by to be in the unit <c>areaUnit</c> names, or
    /// 1 when absent.
    /// </summary>
    private static async Task<(double Factor, string? Error)> AreaScaleAsync(
        HttpContext context, IFormCollection form, int srid, CancellationToken cancellation)
    {
        if (Field(form, "areaUnit") is not { } text)
        {
            return (1, null);
        }

        string key = UnitKey(text, "areaUnit");

        if (!AreaUnits.TryGetValue(key, out (string Name, double SquareMetres) unit))
        {
            return (0, $"`areaUnit={text}` is not an area unit this geometry service knows. It knows esriSquareMeters, "
                + "esriSquareKilometers, esriHectares, esriAres, esriAcres, esriSquareFeet, esriSquareYards, esriSquareMiles "
                + "and esriSquareUsFeet, as {\"areaUnit\":\"esriSquareMeters\"} or by name.");
        }

        ReferenceUnit? reference = await UnitOfAsync(context, srid, cancellation).ConfigureAwait(false);

        if (reference is null || reference.Angular)
        {
            return (0, $"`areaUnit` names {unit.Name}, and EPSG:{srid.ToString(CultureInfo.InvariantCulture)} "
                + (reference is null ? "is in a unit this server cannot tell" : "is geographic: an area in square degrees is not one "
                + "in " + unit.Name + ", and a geodesic area is not measured here")
                + ". Send the geometry in a projected reference suited to the area, or omit `areaUnit`.");
        }

        return (reference.Metres * reference.Metres / unit.SquareMetres, null);
    }

    /// <summary>
    /// A reference named in a field that must be the request's own — <c>bufferSR</c>, <c>outSR</c> — refused when it is
    /// another, since buffering in it and projecting back is not done here.
    /// </summary>
    private static string? SameReference(IFormCollection form, string field, int srid)
    {
        if (Field(form, field) is not { } text)
        {
            return null;
        }

        string key = UnitKey(text, "wkid");

        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int named))
        {
            return null;
        }

        int asked = named == 102100 ? 3857 : named;
        int own = srid == 102100 ? 3857 : srid;

        return asked == own
            ? null
            : $"`{field}={text}` asks for the work to be done in, or answered in, EPSG:{asked.ToString(CultureInfo.InvariantCulture)}, "
                + $"and this geometry service works in the request's own EPSG:{own.ToString(CultureInfo.InvariantCulture)}. "
                + "Project the geometry there first with `project`, or omit the field.";
    }

    /// <summary>A unit as sent: a bare code or name, or a JSON object holding it under a member.</summary>
    private static string UnitKey(string text, string member)
    {
        string trimmed = text.Trim();

        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;

            foreach (string name in new[] { member, "wkid", "latestWkid", "unit", "areaUnit" })
            {
                if (root.TryGetProperty(name, out JsonElement value))
                {
                    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
                }
            }
        }
        catch (JsonException)
        {
        }

        return trimmed;
    }

    private static Task<ReferenceUnit?> UnitOfAsync(HttpContext context, int srid, CancellationToken cancellation) =>
        context.RequestServices.GetRequiredService<IProjector>().UnitOfAsync(srid, cancellation);
}
