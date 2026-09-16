using System;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis.Pbf;

/// <summary>
/// How coordinates become integers in a PBF response: a grid size, an origin and which corner
/// the origin is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A PBF geometry is always integers</b> — the published FeatureCollection specification says
/// coordinates <i>will always be returned as integers and delta-encoded</i>, with a transform in the
/// payload that turns them back into world coordinates. So there is no full-precision PBF, only a
/// grid the caller chose or one this server chose for it.
/// </para>
/// <para>
/// <b>The caller's grid is <c>quantizationParameters</c></b>: <c>tolerance</c> is the cell size in
/// the output reference's units, <c>extent</c> places the origin, and <c>originPosition</c> says
/// whether the origin is the extent's upper-left or lower-left corner. <c>mode=view</c> lets a
/// vertex that lands on the cell of the one before it go, and a part that collapses with it;
/// <c>mode=edit</c> keeps every vertex, because an editor that rounds a shape it did not change
/// writes the rounding back.
/// </para>
/// <para>
/// <b>Without one, the grid is fine enough not to be seen</b> — a billionth of a degree (about a
/// tenth of a millimetre) or a tenth of a millimetre in a projected reference, in edit mode, from an
/// origin of zero. That is a choice of this server's and not something the specification fixes.
/// </para>
/// </remarks>
public sealed record PbfQuantization
{
    /// <summary>The cell size, in the output reference's units.</summary>
    public required double Tolerance { get; init; }

    /// <summary>The origin's x.</summary>
    public required double OriginX { get; init; }

    /// <summary>The origin's y.</summary>
    public required double OriginY { get; init; }

    /// <summary>Whether the origin is the upper-left corner, so integer y grows downwards.</summary>
    public required bool UpperLeft { get; init; }

    /// <summary>Whether a vertex on the cell of the one before it is dropped.</summary>
    public required bool View { get; init; }

    /// <summary>
    /// The cell size for Z and M, which <c>quantizationParameters</c> has no field for — ADR-077 §9.
    /// </summary>
    /// <remarks>
    /// <b>A tenth of a millimetre, from zero, whatever the request says about x and y.</b> The caller's
    /// tolerance is in the output reference's units, which for a geographic reference are degrees, and an
    /// elevation in metres rounded to a degree would be no elevation at all.
    /// </remarks>
    public double OrdinateScale { get; init; } = 1e-4;

    /// <summary>The integer a Z or M value is written as, on <see cref="OrdinateScale"/>.</summary>
    public long Ordinate(double value) => (long)Math.Round(value / OrdinateScale, MidpointRounding.AwayFromZero);

    /// <summary>The grid used when a request names none.</summary>
    /// <param name="srid">The reference the coordinates are written in.</param>
    public static PbfQuantization Default(int srid) => new()
    {
        Tolerance = srid > 0 && AxisOrder.IsGeographic(srid) ? 1e-9 : 1e-4,
        OriginX = 0,
        OriginY = 0,
        UpperLeft = true,
        View = false,
    };

    /// <summary>The integer column of <paramref name="x"/>.</summary>
    public long X(double x) => (long)Math.Round((x - OriginX) / Tolerance, MidpointRounding.AwayFromZero);

    /// <summary>The integer row of <paramref name="y"/>.</summary>
    public long Y(double y) => (long)Math.Round(
        (UpperLeft ? OriginY - y : y - OriginY) / Tolerance, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Reads <c>quantizationParameters</c>.
    /// </summary>
    /// <param name="json">The parameter's value.</param>
    /// <param name="outputSrid">The reference the response is written in, to check the extent's against.</param>
    /// <param name="quantization">The grid, when this returns true.</param>
    /// <param name="error">Why it was refused, when this returns false.</param>
    /// <returns>Whether the value could be used.</returns>
    /// <remarks>
    /// <b>An extent in another reference is refused rather than transformed.</b> Its corner would be
    /// the origin of a grid in different units, and every coordinate would decode somewhere else with
    /// no error to say so.
    /// </remarks>
    public static bool TryParse(string? json, int outputSrid, out PbfQuantization? quantization, out string? error)
    {
        quantization = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            quantization = Default(outputSrid);
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "quantizationParameters must be a JSON object.";
                return false;
            }

            bool view = true;

            if (root.TryGetProperty("mode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String)
            {
                switch (mode.GetString()!.ToLowerInvariant())
                {
                    case "view":
                        view = true;
                        break;
                    case "edit":
                        view = false;
                        break;
                    default:
                        error = $"quantizationParameters mode '{mode.GetString()}' is not one of view or edit.";
                        return false;
                }
            }

            bool upperLeft = true;

            if (root.TryGetProperty("originPosition", out JsonElement origin) && origin.ValueKind == JsonValueKind.String)
            {
                switch (origin.GetString()!.ToLowerInvariant())
                {
                    case "upperleft":
                        upperLeft = true;
                        break;
                    case "lowerleft":
                        upperLeft = false;
                        break;
                    default:
                        error = $"quantizationParameters originPosition '{origin.GetString()}' is not one of upperLeft or lowerLeft.";
                        return false;
                }
            }

            PbfQuantization fallback = Default(outputSrid);
            double tolerance = fallback.Tolerance;

            if (root.TryGetProperty("tolerance", out JsonElement given))
            {
                if (!TryNumber(given, out tolerance) || !(tolerance > 0) || double.IsInfinity(tolerance))
                {
                    error = "quantizationParameters tolerance must be a positive number.";
                    return false;
                }
            }

            double originX = 0;
            double originY = 0;

            if (root.TryGetProperty("extent", out JsonElement extent) && extent.ValueKind == JsonValueKind.Object)
            {
                if (!TryNumber(extent, "xmin", out double xmin)
                    || !TryNumber(extent, "ymin", out double ymin)
                    || !TryNumber(extent, "ymax", out double ymax))
                {
                    error = "quantizationParameters extent must carry numeric xmin, ymin and ymax.";
                    return false;
                }

                if (extent.TryGetProperty("spatialReference", out JsonElement reference)
                    && reference.ValueKind == JsonValueKind.Object
                    && reference.TryGetProperty("wkid", out JsonElement wkid)
                    && wkid.TryGetInt32(out int code)
                    && code != outputSrid
                    && !(IsWebMercator(code) && IsWebMercator(outputSrid)))
                {
                    error =
                        $"quantizationParameters extent is in wkid {code} and the response is written in {outputSrid}. "
                        + "Its corner would be the origin of a grid in other units, so it is refused rather than "
                        + "used; send the extent in the output reference, or outSR to match it.";
                    return false;
                }

                originX = xmin;
                originY = upperLeft ? ymax : ymin;
            }

            quantization = new PbfQuantization
            {
                Tolerance = tolerance,
                OriginX = originX,
                OriginY = originY,
                UpperLeft = upperLeft,
                View = view,
            };

            return true;
        }
        catch (JsonException)
        {
            error = "quantizationParameters is not valid JSON.";
            return false;
        }
    }

    private static bool IsWebMercator(int srid) => srid is 3857 or 102100 or 900913;

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element) && TryNumber(element, out value);
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }
}
