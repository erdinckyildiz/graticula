using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// The parameters <c>MapServer/identify</c> takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tolerance is in pixels and the geometry is in map units, and that is the
/// whole awkwardness of this operation.</b> A client sends where it clicked, how big
/// its map is on screen, and how many pixels of slop to allow; the server has to turn
/// those into an extent. Get it wrong and a click on a road finds nothing, which
/// reads as a layer with no data.
/// </para>
/// <para>
/// <b><c>mapExtent</c> and <c>imageDisplay</c> are required for exactly that
/// reason.</b> Without them there is no scale, so a tolerance in pixels means
/// nothing. ArcGIS requires them too; this refuses rather than assuming a scale,
/// because an assumed one is wrong by whatever factor the client's map differs by.
/// </para>
/// </remarks>
internal sealed class MapServerIdentifyParameters
{
    private MapServerIdentifyParameters()
    {
    }

    /// <summary>The extent to search, already grown by the tolerance.</summary>
    public Envelope Around { get; private init; }

    /// <summary>The CRS of that extent.</summary>
    public int Srid { get; private init; }

    /// <summary>The layers to ask.</summary>
    public IReadOnlyList<PublishedLayer> Layers { get; private init; } = [];

    /// <summary>Whether to return each feature's shape.</summary>
    public bool ReturnGeometry { get; private init; }

    /// <summary>Reads the parameters.</summary>
    /// <param name="parameter">Reads one parameter by name, case-insensitively.</param>
    /// <param name="available">The service's layers.</param>
    /// <param name="parsed">The parameters.</param>
    /// <param name="error">Why not, when they did not parse.</param>
    /// <returns>Whether they parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        IReadOnlyList<PublishedLayer> available,
        out MapServerIdentifyParameters? parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(available);

        parsed = null;
        error = null;

        if (!TryPoint(parameter("geometry"), parameter("geometryType"), out double x, out double y, out error))
        {
            return false;
        }

        if (!TryExtent(parameter("mapExtent"), out Envelope map, out error))
        {
            return false;
        }

        if (!TryDisplay(parameter("imageDisplay"), out int width, out int height, out error))
        {
            return false;
        }

        double tolerance = 3;

        if (parameter("tolerance") is { Length: > 0 } asked
            && (!double.TryParse(
                    asked, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance)
                || tolerance < 0))
        {
            error = "`tolerance` is a number of pixels.";
            return false;
        }

        // Pixels into map units, one axis each, because a stretched map has two
        // resolutions and a click is round in neither.
        double slopX = tolerance * (map.Width / width);
        double slopY = tolerance * (map.Height / height);

        // A zero tolerance still needs an extent with area: a point-in-polygon test
        // against a degenerate box matches nothing at every provider.
        slopX = Math.Max(slopX, map.Width / width / 2);
        slopY = Math.Max(slopY, map.Height / height / 2);

        List<PublishedLayer> layers = [];

        if (!TryLayers(parameter("layers"), available, layers, out error))
        {
            return false;
        }

        parsed = new MapServerIdentifyParameters
        {
            Around = new Envelope(x - slopX, y - slopY, x + slopX, y + slopY),
            Srid = ReferenceOf(parameter("sr")) ?? 4326,
            Layers = layers,
            ReturnGeometry = !string.Equals(
                parameter("returnGeometry")?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
        };

        return true;
    }

    /// <summary>
    /// Reads the clicked point, in either shape a client sends it.
    /// </summary>
    /// <remarks>
    /// <b><c>x,y</c> or <c>{"x":…,"y":…}</c>.</b> Both are in the wild and both are
    /// documented; a parser taking one refuses half the clients for no reason they
    /// can see.
    /// </remarks>
    private static bool TryPoint(
        string? geometry, string? geometryType, out double x, out double y, out string? error)
    {
        x = 0;
        y = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(geometry))
        {
            error = "`geometry` is required: the point that was clicked.";
            return false;
        }

        if (geometryType is { Length: > 0 }
            && !string.Equals(geometryType.Trim(), "esriGeometryPoint", StringComparison.OrdinalIgnoreCase))
        {
            error = $"`geometryType={geometryType}` is not supported. This server identifies at a "
                + "point; an envelope or a polygon asks a different question, and answering it "
                + "with the point at its centre would be a different answer wearing the same shape.";

            return false;
        }

        string text = geometry.Trim();

        if (text.StartsWith('{'))
        {
            try
            {
                JsonElement document = JsonDocument.Parse(text).RootElement;

                if (document.TryGetProperty("x", out JsonElement px)
                    && document.TryGetProperty("y", out JsonElement py)
                    && px.TryGetDouble(out x)
                    && py.TryGetDouble(out y))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Falls through to the message below.
            }

            error = "`geometry` is JSON but has no numeric `x` and `y`.";
            return false;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
        {
            error = "`geometry` is `x,y` or a point object.";
            return false;
        }

        return true;
    }

    private static bool TryExtent(string? value, out Envelope extent, out string? error)
    {
        extent = Envelope.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "`mapExtent` is required: without it a tolerance in pixels has no scale to be "
                + "measured against.";

            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        double[] numbers = new double[4];

        if (parts.Length != 4)
        {
            error = "`mapExtent` is four comma-separated numbers: minx,miny,maxx,maxy.";
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                error = $"`{parts[i]}` in `mapExtent` is not a number.";
                return false;
            }
        }

        if (numbers[2] <= numbers[0] || numbers[3] <= numbers[1])
        {
            error = "`mapExtent` needs a positive width and height.";
            return false;
        }

        extent = new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static bool TryDisplay(string? value, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "`imageDisplay` is required: width,height,dpi.";
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

        // dpi is the third value and is not used: the tolerance is in pixels and the
        // extent is in map units, so the physical size of a pixel never enters.
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            || width <= 0
            || height <= 0)
        {
            error = "`imageDisplay` is width,height,dpi with positive pixel dimensions.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads <c>layers</c>, which identify writes differently from export.
    /// </summary>
    /// <remarks>
    /// <b><c>top</c>, <c>visible</c>, <c>all</c>, each optionally with ids.</b> This
    /// server has no per-request visibility and no layer order beyond the service's
    /// own, so all three mean the same thing here and the ids are what matters.
    /// Saying so is better than accepting the word and quietly meaning something else.
    /// </remarks>
    private static bool TryLayers(
        string? value,
        IReadOnlyList<PublishedLayer> available,
        List<PublishedLayer> layers,
        out string? error)
    {
        error = null;

        List<PublishedLayer> drawable = [];

        foreach (PublishedLayer layer in available)
        {
            if (layer.Definition.GeometryColumn is { Length: > 0 })
            {
                drawable.Add(layer);
            }
        }

        string text = value?.Trim() ?? string.Empty;
        int colon = text.IndexOf(':', StringComparison.Ordinal);

        string ids = colon >= 0 ? text[(colon + 1)..] : text;

        if (ids.Length == 0 || colon == text.Length - 1)
        {
            layers.AddRange(drawable);
            return true;
        }

        foreach (string part in ids.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                // `layers=all` with no ids: every drawable layer.
                layers.Clear();
                layers.AddRange(drawable);
                return true;
            }

            foreach (PublishedLayer layer in drawable)
            {
                if (layer.LayerIndex == id)
                {
                    layers.Add(layer);
                    break;
                }
            }
        }

        if (layers.Count == 0)
        {
            error = "`layers` named no layer this service has.";
            return false;
        }

        return true;
    }

    private static int? ReferenceOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare))
        {
            return bare;
        }

        try
        {
            JsonElement document = JsonDocument.Parse(text).RootElement;

            foreach (string name in (string[])["latestWkid", "wkid"])
            {
                if (document.TryGetProperty(name, out JsonElement code) && code.TryGetInt32(out int wkid))
                {
                    return wkid;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
