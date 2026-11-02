using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Geocoding;
using Graticula.Geometries;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Geocoding;

/// <summary>
/// ArcGIS's GeocodeServer over the configured <see cref="IGeocoder"/> — ADR-258.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four resources, and only when a geocoder is configured</b>: the service, <c>findAddressCandidates</c>,
/// <c>suggest</c> and <c>reverseGeocode</c>, under <see cref="GeocoderSettings.Path"/>. A deployment that never
/// set <c>Graticula:Geocoding:PhotonUrl</c> has no route here and no entry in the directory — absent, not a
/// service that answers 503 for ever (Q-226, owner decision 2026-11-02).
/// </para>
/// <para>
/// <b>WGS 84 at the engine, any reference at the door.</b> A <c>location</c> or <c>searchExtent</c> in another
/// reference is projected to 4326 before the engine is asked, and the answers are projected to <c>outSR</c>
/// after, through the same <see cref="IProjector"/> every other face uses.
/// </para>
/// <para>
/// <b>An engine that does not answer is a 503 with a sentence</b>, not a 500 and not a stack trace — the
/// service exists, and what is missing is the thing behind it.
/// </para>
/// </remarks>
internal static partial class GeocodeServerEndpoints
{
    private static readonly string[] GetOrPost = ["GET", "POST"];

    private const int Wgs84 = 4326;

    /// <summary>Maps the service, when one is configured.</summary>
    /// <param name="app">The application.</param>
    /// <param name="settings">The geocoder's settings, or null when none is configured — and then nothing is mapped.</param>
    public static void Map(WebApplication app, GeocoderSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (settings is null)
        {
            return;
        }

        RouteGroupBuilder group = app
            .MapGroup(GeocoderSettings.Path)
            .AddEndpointFilter(SharingFilter)
            .Governed(SharingGovernedExtensions.ByConfiguration);

        group.MapMethods("", GetOrPost, Describe);
        group.MapMethods("/findAddressCandidates", GetOrPost, FindAddressCandidatesAsync);
        group.MapMethods("/suggest", GetOrPost, SuggestAsync);
        group.MapMethods("/reverseGeocode", GetOrPost, ReverseGeocodeAsync);
    }

    /// <summary>Whether the services directory lists the geocode service in <paramref name="folder"/> to this caller.</summary>
    /// <param name="settings">The geocoder's settings, or null.</param>
    /// <param name="folder">The folder being listed, or null for the root.</param>
    /// <param name="current">Who is asking.</param>
    /// <returns>Whether it is listed.</returns>
    internal static bool DirectoryLists(GeocoderSettings? settings, string? folder, RequestPrincipal current) =>
        settings is not null
        && string.Equals(folder, GeocoderSettings.Folder, StringComparison.OrdinalIgnoreCase)
        && Allowed(settings, current);

    private static bool Allowed(GeocoderSettings settings, RequestPrincipal current) =>
        LayerAccess.Evaluate(settings.Sharing, null, current.Principal, current.Authorization).IsAllowed();

    private const string RefusalMessage =
        "No service 'Utilities/Geocode/GeocodeServer' is visible to you. It may not exist, or it may not be "
        + "shared with you — this response is deliberately the same for both.";

    private static async ValueTask<object?> SharingFilter(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;
        GeocoderSettings settings = context.RequestServices.GetRequiredService<GeocoderSettings>();

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()
            ?? throw new InvalidOperationException(
                "No principal was resolved for this request. The authentication middleware must run before any "
                + "endpoint, including for anonymous callers.");

        if (Allowed(settings, current))
        {
            return await next(invocation).ConfigureAwait(false);
        }

        // 404 rather than 403, as every unshared service answers; a caller with no token is challenged instead.
        return TokenRequired.Applies(context)
            ? TokenRequired.Result(context)
            : Results.Json(
                new { error = new { code = 404, message = RefusalMessage, details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status404NotFound);
    }

    // ---------- the service ----------

    private static IResult Describe(HttpContext context)
    {
        Dictionary<string, object?> document =
            GeocodeServerWriter.ServiceDocument(FeatureServerMetadataWriter.CurrentVersion, "Photon");

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            string root = context.Request.Path.Value!.TrimEnd('/');

            return Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    "Geocode (GeocodeServer)",
                    document,
                    [
                        ("findAddressCandidates", $"{root}/findAddressCandidates"),
                        ("suggest", $"{root}/suggest"),
                        ("reverseGeocode", $"{root}/reverseGeocode"),
                    ],
                    "Supported operations"),
                "text/html; charset=utf-8");
        }

        return Results.Json(document);
    }

    // ---------- findAddressCandidates ----------

    private static async Task<IResult> FindAddressCandidatesAsync(
        HttpContext context, IGeocoder geocoder, IProjector projector, CancellationToken cancellation)
    {
        Func<string, string?> p = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        // `SingleLine` is matched without regard to case, so `singleLine` — what the Maps SDK sends — is the same name.
        string? line = p(GeocodeServerWriter.SingleLineField);

        if (string.IsNullOrWhiteSpace(line))
        {
            line = string.Join(", ", GeocodeServerWriter.AddressFields
                .Select(p)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim()));
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return BadRequest(
                "SingleLine, or at least one of the address fields, is required.",
                $"Address fields: {string.Join(", ", GeocodeServerWriter.AddressFields)}.");
        }

        if (!TryCount(p("maxLocations"), GeocodeServerWriter.DefaultLocations, GeocodeServerWriter.MaximumLocations,
                "maxLocations", out int max, out IResult? refusal)
            || !TryReference(p("outSR"), Wgs84, "outSR", out int outWkid, out refusal))
        {
            return refusal!;
        }

        (bool locationOk, (double, double)? near, refusal) =
            await LocationAsync(p("location"), projector, required: false, cancellation).ConfigureAwait(false);

        if (!locationOk)
        {
            return refusal!;
        }

        (bool extentOk, GeocodeBox? within, refusal) =
            await SearchExtentAsync(p("searchExtent"), projector, cancellation).ConfigureAwait(false);

        if (!extentOk)
        {
            return refusal!;
        }

        string? magicKey = p("magicKey") is { Length: > 0 } key ? key.Trim() : null;

        IReadOnlyList<GeocodePlace> places;

        try
        {
            // <b>A suggestion's key is found again by searching its text</b>: Photon has no lookup by
            // OpenStreetMap id, and the Maps SDK sends the suggestion's text with its key. Asked for a few more
            // than one so the suggested place is among them even when the text is shared by several.
            places = await geocoder.SearchAsync(
                    new GeocodeSearch(line.Trim(), magicKey is null ? max : Math.Max(max, 10), near, within, p("langCode")),
                    cancellation)
                .ConfigureAwait(false);
        }
        catch (GeocoderUnavailableException e)
        {
            return Unavailable(context, e);
        }

        List<(GeocodePlace Place, double Score)> scored;

        if (magicKey is not null
            && places.FirstOrDefault(place => string.Equals(place.Key, magicKey, StringComparison.Ordinal)) is { } chosen)
        {
            // The caller picked this one from a suggestion: it is the match, wholly.
            scored = [(chosen, 100.0)];
        }
        else
        {
            // Stable, so Photon's own ranking — which knows importance — orders places that score alike.
            scored =
            [
                .. places
                    .Select(place => (Place: place, Score: GeocodeServerWriter.Score(line, place)))
                    .OrderByDescending(c => c.Score)
                    .Take(max),
            ];
        }

        // One projection call for every point and extent corner, rather than one per candidate.
        List<double> lonLat = [];

        foreach ((GeocodePlace place, _) in scored)
        {
            GeocodeBox box = GeocodeServerWriter.ExtentOf(place);
            lonLat.AddRange([place.Longitude, place.Latitude, box.West, box.South, box.East, box.North]);
        }

        (bool projectedOk, double[] xy, refusal) =
            await FromWgs84Async(lonLat, outWkid, projector, cancellation).ConfigureAwait(false);

        if (!projectedOk)
        {
            return refusal!;
        }

        string? outFields = p("outFields");
        List<object> candidates = [];

        for (int i = 0; i < scored.Count; i++)
        {
            (GeocodePlace place, double score) = scored[i];
            int at = i * 6;

            double? distance = near is { } from
                ? GeocodeServerWriter.DistanceMeters(from.Item1, from.Item2, place.Longitude, place.Latitude)
                : null;

            GeocodeBox extent = new(
                Math.Min(xy[at + 2], xy[at + 4]), Math.Min(xy[at + 3], xy[at + 5]),
                Math.Max(xy[at + 2], xy[at + 4]), Math.Max(xy[at + 3], xy[at + 5]));

            Dictionary<string, object?> attributes =
                GeocodeServerWriter.Attributes(place, score, xy[at], xy[at + 1], extent, distance);

            candidates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["address"] = attributes["Match_addr"],
                ["location"] = new { x = xy[at], y = xy[at + 1] },
                ["score"] = score,
                ["attributes"] = GeocodeServerWriter.Choose(attributes, outFields),
                ["extent"] = new { xmin = extent.West, ymin = extent.South, xmax = extent.East, ymax = extent.North },
            });
        }

        return Answer(context, "findAddressCandidates", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["spatialReference"] = GeocodeServerWriter.SpatialReference(outWkid),
            ["candidates"] = candidates,
        });
    }

    // ---------- suggest ----------

    private static async Task<IResult> SuggestAsync(
        HttpContext context, IGeocoder geocoder, IProjector projector, CancellationToken cancellation)
    {
        Func<string, string?> p = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (p("text") is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return BadRequest("'text' is required: the characters typed so far.");
        }

        if (!TryCount(p("maxSuggestions"), GeocodeServerWriter.DefaultSuggestions,
                GeocodeServerWriter.MaximumSuggestions, "maxSuggestions", out int max, out IResult? refusal))
        {
            return refusal!;
        }

        (bool locationOk, (double, double)? near, refusal) =
            await LocationAsync(p("location"), projector, required: false, cancellation).ConfigureAwait(false);

        if (!locationOk)
        {
            return refusal!;
        }

        (bool extentOk, GeocodeBox? within, refusal) =
            await SearchExtentAsync(p("searchExtent"), projector, cancellation).ConfigureAwait(false);

        if (!extentOk)
        {
            return refusal!;
        }

        IReadOnlyList<GeocodePlace> places;

        try
        {
            places = await geocoder.SearchAsync(
                    new GeocodeSearch(text.Trim(), max, near, within, p("langCode")), cancellation)
                .ConfigureAwait(false);
        }
        catch (GeocoderUnavailableException e)
        {
            return Unavailable(context, e);
        }

        return Answer(context, "suggest", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["suggestions"] = places
                .Take(max)
                .Select(place => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["text"] = GeocodeServerWriter.Labels(place).Long,
                    ["magicKey"] = GeocodeServerWriter.MagicKey(place) ?? string.Empty,
                    ["isCollection"] = false,
                })
                .ToArray(),
        });
    }

    // ---------- reverseGeocode ----------

    private static async Task<IResult> ReverseGeocodeAsync(
        HttpContext context, IGeocoder geocoder, IProjector projector, CancellationToken cancellation)
    {
        Func<string, string?> p = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        string? location = p("location");

        if (!TryPoint(location, out double x, out double y, out int inWkid, out IResult? refusal))
        {
            return refusal!;
        }

        // The answer is in outSR when one is named, otherwise in the reference the location came in — INFERRED,
        // ADR-258 §5: a client that sent Web Mercator expects Web Mercator back.
        if (!TryReference(p("outSR"), inWkid, "outSR", out int outWkid, out refusal))
        {
            return refusal!;
        }

        double? distance = null;

        if (p("distance") is { Length: > 0 } rawDistance)
        {
            if (!double.TryParse(rawDistance, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                || d <= 0 || double.IsInfinity(d))
            {
                return BadRequest("'distance' must be a positive number of metres.");
            }

            distance = d;
        }

        (bool ok, double[] lonLat, refusal) =
            await ToWgs84Async([x, y], inWkid, projector, cancellation).ConfigureAwait(false);

        if (!ok)
        {
            return refusal!;
        }

        GeocodePlace? place;

        try
        {
            place = await geocoder.ReverseAsync(lonLat[0], lonLat[1], distance, p("langCode"), cancellation)
                .ConfigureAwait(false);
        }
        catch (GeocoderUnavailableException e)
        {
            return Unavailable(context, e);
        }

        if (place is null)
        {
            // The reference's own refusal for a position with nothing near it.
            return BadRequest(
                "Cannot perform query. Invalid query parameters.",
                "Unable to find address for the specified location.");
        }

        (ok, double[] xy, refusal) =
            await FromWgs84Async([place.Longitude, place.Latitude], outWkid, projector, cancellation).ConfigureAwait(false);

        if (!ok)
        {
            return refusal!;
        }

        return Answer(context, "reverseGeocode", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["address"] = GeocodeServerWriter.ReverseAddress(place),
            ["location"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = xy[0],
                ["y"] = xy[1],
                ["spatialReference"] = GeocodeServerWriter.SpatialReference(outWkid),
            },
        });
    }

    // ---------- parameters ----------

    private static bool TryCount(string? raw, int fallback, int ceiling, string name, out int value, out IResult? refusal)
    {
        refusal = null;
        value = fallback;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            refusal = BadRequest($"'{name}' must be a whole number from 1 to {ceiling}.");
            return false;
        }

        // Above the ceiling is answered with the ceiling, as the reference documents, rather than refused.
        value = Math.Min(value, ceiling);
        return true;
    }

    /// <summary>A spatial reference given as a code or as <c>{"wkid":…}</c>; the fallback when not sent.</summary>
    private static bool TryReference(string? raw, int fallback, string name, out int wkid, out IResult? refusal)
    {
        refusal = null;
        wkid = fallback;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out wkid) && wkid > 0)
        {
            return true;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(raw);

            if (json.RootElement.ValueKind == JsonValueKind.Object
                && (json.RootElement.TryGetProperty("wkid", out JsonElement code)
                    || json.RootElement.TryGetProperty("latestWkid", out code))
                && code.TryGetInt32(out wkid) && wkid > 0)
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // Falls through to the sentence below.
        }

        refusal = BadRequest($"'{name}' must name a spatial reference by its well-known id, as 3857 or {{\"wkid\":3857}}.");
        return false;
    }

    /// <summary>A point given as <c>x,y</c> or as <c>{"x":…,"y":…,"spatialReference":…}</c>.</summary>
    private static bool TryPoint(string? raw, out double x, out double y, out int wkid, out IResult? refusal)
    {
        x = y = 0;
        wkid = Wgs84;
        refusal = null;

        const string Shape = "'location' must be a point, as x,y in longitude and latitude or as "
            + "{\"x\":…,\"y\":…,\"spatialReference\":{\"wkid\":…}}.";

        if (string.IsNullOrWhiteSpace(raw))
        {
            refusal = BadRequest(Shape);
            return false;
        }

        string text = raw.Trim();

        if (!text.StartsWith('{'))
        {
            string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.IsFinite(x) && double.IsFinite(y))
            {
                return true;
            }

            refusal = BadRequest(Shape);
            return false;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(text);
            JsonElement root = json.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("x", out JsonElement xe) && xe.TryGetDouble(out x)
                && root.TryGetProperty("y", out JsonElement ye) && ye.TryGetDouble(out y))
            {
                if (root.TryGetProperty("spatialReference", out JsonElement sr)
                    && !TryReference(sr.GetRawText(), Wgs84, "location.spatialReference", out wkid, out refusal))
                {
                    return false;
                }

                return true;
            }
        }
        catch (JsonException)
        {
            // Falls through.
        }

        refusal = BadRequest(Shape);
        return false;
    }

    private static async Task<(bool Ok, (double, double)? Near, IResult? Refusal)> LocationAsync(
        string? raw, IProjector projector, bool required, CancellationToken cancellation)
    {
        if (!required && string.IsNullOrWhiteSpace(raw))
        {
            return (true, null, null);
        }

        if (!TryPoint(raw, out double x, out double y, out int wkid, out IResult? refusal))
        {
            return (false, null, refusal);
        }

        (bool ok, double[] lonLat, refusal) = await ToWgs84Async([x, y], wkid, projector, cancellation).ConfigureAwait(false);

        return ok ? (true, (lonLat[0], lonLat[1]), null) : (false, null, refusal);
    }

    /// <summary>A <c>searchExtent</c> given as <c>xmin,ymin,xmax,ymax</c> or as an envelope object.</summary>
    private static async Task<(bool Ok, GeocodeBox? Within, IResult? Refusal)> SearchExtentAsync(
        string? raw, IProjector projector, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (true, null, null);
        }

        const string Shape = "'searchExtent' must be an envelope, as xmin,ymin,xmax,ymax in longitude and latitude or "
            + "as {\"xmin\":…,\"ymin\":…,\"xmax\":…,\"ymax\":…,\"spatialReference\":{\"wkid\":…}}.";

        double[] v = new double[4];
        int wkid = Wgs84;
        string text = raw.Trim();
        bool read = false;

        if (!text.StartsWith('{'))
        {
            string[] parts = text.Split(',', StringSplitOptions.TrimEntries);

            read = parts.Length == 4
                && parts.Select((part, i) => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    .All(parsed => parsed);
        }
        else
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(text);
                JsonElement root = json.RootElement;
                string[] names = ["xmin", "ymin", "xmax", "ymax"];

                read = root.ValueKind == JsonValueKind.Object
                    && names.Select((name, i) => root.TryGetProperty(name, out JsonElement e) && e.TryGetDouble(out v[i]))
                        .All(parsed => parsed);

                if (read && root.TryGetProperty("spatialReference", out JsonElement sr)
                    && !TryReference(sr.GetRawText(), Wgs84, "searchExtent.spatialReference", out wkid, out IResult? bad))
                {
                    return (false, null, bad);
                }
            }
            catch (JsonException)
            {
                read = false;
            }
        }

        if (!read || v.Any(n => !double.IsFinite(n)))
        {
            return (false, null, BadRequest(Shape));
        }

        (bool ok, double[] lonLat, IResult? refusal) =
            await ToWgs84Async([v[0], v[1], v[2], v[3]], wkid, projector, cancellation).ConfigureAwait(false);

        if (!ok)
        {
            return (false, null, refusal);
        }

        return (true, new GeocodeBox(
            Math.Min(lonLat[0], lonLat[2]), Math.Min(lonLat[1], lonLat[3]),
            Math.Max(lonLat[0], lonLat[2]), Math.Max(lonLat[1], lonLat[3])), null);
    }

    // ---------- projection ----------

    private static bool IsWgs84(int wkid) => wkid == Wgs84;

    private static int Canonical(int wkid) => wkid is 102100 or 102113 ? 3857 : wkid;

    private static Task<(bool Ok, double[] Values, IResult? Refusal)> ToWgs84Async(
        IReadOnlyList<double> xy, int wkid, IProjector projector, CancellationToken cancellation) =>
        ProjectAsync(xy, Canonical(wkid), Wgs84, projector, cancellation);

    private static Task<(bool Ok, double[] Values, IResult? Refusal)> FromWgs84Async(
        IReadOnlyList<double> lonLat, int wkid, IProjector projector, CancellationToken cancellation) =>
        ProjectAsync(lonLat, Wgs84, Canonical(wkid), projector, cancellation);

    /// <summary>Projects pairs of numbers as points, in one call.</summary>
    private static async Task<(bool Ok, double[] Values, IResult? Refusal)> ProjectAsync(
        IReadOnlyList<double> xy, int from, int to, IProjector projector, CancellationToken cancellation)
    {
        if (from == to || xy.Count == 0)
        {
            return (true, [.. xy], null);
        }

        List<Geometry> points = [];

        for (int i = 0; i + 1 < xy.Count; i += 2)
        {
            points.Add(new Point(xy[i], xy[i + 1]));
        }

        IReadOnlyList<Geometry> projected;

        try
        {
            (projected, _) = await projector.ProjectAsync(points, from, to, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (false, [], BadRequest(
                $"Coordinates in {from} could not be projected to {to}.",
                $"Name a spatial reference this server's projection engine knows; {Wgs84} always works."));
        }

        double[] values = new double[xy.Count];

        for (int i = 0; i < projected.Count; i++)
        {
            if (projected[i] is not Point { IsEmpty: false } point)
            {
                return (false, [], BadRequest($"A coordinate has no position in {to}."));
            }

            values[2 * i] = point.X;
            values[(2 * i) + 1] = point.Y;
        }

        return (true, values, null);
    }

    // ---------- answers ----------

    private static IResult Answer(HttpContext context, string operation, Dictionary<string, object?> document) =>
        RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept)
            ? Results.Content(
                RestDirectory.Document(context.Request.Path, $"{operation} (GeocodeServer)", document),
                "text/html; charset=utf-8")
            : Results.Json(document);

    private static IResult BadRequest(string message, params string[] details) =>
        Results.Json(
            new { error = new { code = 400, message, details } },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult Unavailable(HttpContext context, GeocoderUnavailableException e)
    {
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Graticula.Geocoding");
        GeocoderDidNotAnswer(logger, e.Message);

        return Results.Json(
            new
            {
                error = new
                {
                    code = 503,
                    message = "The geocoder this service is answered by is not answering.",
                    details = new[] { e.Message, "The service exists; retry shortly, or ask the operator to check the geocoder." },
                },
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    [LoggerMessage(
        EventId = 2581,
        Level = LogLevel.Warning,
        Message = "The GeocodeServer's engine did not answer: {Reason} The request was answered 503. ADR-258.")]
    private static partial void GeocoderDidNotAnswer(ILogger logger, string reason);
}
