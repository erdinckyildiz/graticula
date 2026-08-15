using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Geometries;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GisServer.Host;

/// <summary>
/// The ArcGIS GeometryServer surface — the half of it that is safe to expose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Half, and the split is the point.</b>
/// <see href="../../benchmarks/geometry-overlay/RESULTS.md">Measurement</see>
/// invalidated A-042: a 6,408-vertex adversarial input costs 153 seconds and
/// 16.7 GB where a real 72,919-vertex national outline costs 312 ms and 17 MB.
/// The run that produced it took the host down. So <c>intersect</c>,
/// <c>difference</c>, <c>union</c> and <c>cut</c> are refused pending
/// <see href="../../docs/open-questions.md">Q-97</see>, and everything here is
/// linear in the vertex count.
/// </para>
/// <para>
/// <b>A vertex cap is the right control for these and was the wrong one for
/// those.</b> That distinction is A-042's actual error: it applied one mechanism
/// to two kinds of work. For a single pass over coordinates, input size bounds
/// the work exactly. For general overlay it bounds nothing.
/// </para>
/// <para>
/// <b>Refused, not absent.</b> A missing route answers 404, which a client reads
/// as *this server has no GeometryServer*. A route that explains itself tells
/// somebody what happened and where the reasoning is.
/// </para>
/// </remarks>
internal static class GeometryServerEndpoints
{
    /// <summary>
    /// The most vertices a single request may carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sound here for a reason that does not generalise.</b> Everything on
    /// this surface is one pass over the coordinates, so 500,000 vertices is
    /// 500,000 units of work and the cap is exactly a bound. The measured
    /// counter-example — 6,408 vertices costing 153 seconds — was general
    /// overlay, whose cost is set by how the geometries interact rather than by
    /// how large they are. None of that machinery is reachable from here.
    /// </para>
    /// <para>
    /// Half a million is roughly seven copies of the largest polygon in the test
    /// corpus, and about 12 MB of JSON. Generous on purpose: the cap exists so a
    /// request cannot be unbounded, not to ration ordinary work.
    /// </para>
    /// </remarks>
    public const int MaximumVertices = 500_000;

    /// <summary>What this surface offers, all of it linear in the input.</summary>
    private static readonly string[] Supported =
        ["project", "areasAndLengths", "lengths", "labelPoints"];

    /// <summary>Operations that need general overlay, and are therefore refused.</summary>
    private static readonly string[] Blocked =
        ["intersect", "difference", "union", "cut", "buffer", "offset", "relation", "autoComplete",
         "reshape", "trimExtend", "convexHull", "simplify", "densify", "generalize", "distance"];

    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>A group, so the sharing check cannot be forgotten on one route.</b>
        // Owner correction 2026-08-15: "geometry server is also a service. we
        // might make all services public, private or organization." Until this
        // group existed the geometry service was reachable anonymously — not
        // because anyone decided it should be, but because sharing lived on
        // layers and this service has none. Five handlers each remembering to
        // call a guard is five chances to forget; the filter is one.
        RouteGroupBuilder geometry = app
            .MapGroup("/rest/services/Utilities/Geometry/GeometryServer")
            .AddEndpointFilter(SharingFilter);

        geometry.MapGet("", Describe);
        geometry.MapPost("/project", ProjectAsync);
        geometry.MapPost("/areasAndLengths", AreasAndLengths);
        geometry.MapPost("/lengths", Lengths);
        geometry.MapPost("/labelPoints", LabelPoints);

        foreach (string operation in Blocked)
        {
            string name = operation;
            geometry.MapPost($"/{name}", (HttpContext context) => Refuse(context, name));
        }
    }

    /// <summary>The name this service carries in <c>system_service</c>.</summary>
    public const string ServiceName = "Geometry";

    /// <summary>
    /// What a caller who may not use this service gets.
    /// </summary>
    /// <remarks>
    /// <b>404, matching every other unshared resource.</b> A 403 would confirm
    /// the service exists, and a service made private stops answering strangers
    /// entirely — including about itself.
    /// </remarks>
    private static readonly IResult Refusal = Results.Json(
        new
        {
            error = new
            {
                code = 404,
                message =
                    "No service 'Utilities/Geometry/GeometryServer' is visible to you. It may not "
                    + "exist, or it may not be shared with you — this response is deliberately the "
                    + "same for both. An administrator can change its sharing with "
                    + "PUT /admin/services/Geometry/sharing.",
            },
        },
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Refuses the request unless this service's sharing admits the caller.
    /// </summary>
    /// <remarks>
    /// <b>404, matching <see cref="Authorize.RefuseReadAsync"/>.</b> A private
    /// geometry service that answered 403 would confirm it exists; the whole
    /// point of making it private is that it does not answer strangers at all.
    /// </remarks>
    private static async ValueTask<object?> SharingFilter(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;

        PostgresSystemServices services =
            context.RequestServices.GetRequiredService<PostgresSystemServices>();

        SystemService? service = await services
            .FindAsync(ServiceName, context.RequestAborted)
            .ConfigureAwait(false);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()
            ?? throw new InvalidOperationException(
                "No principal was resolved for this request. The authentication middleware must "
                + "run before any endpoint, including for anonymous callers.");

        // Absent from the table means absent from the server. A row that was
        // deleted is a service that was removed, not one that defaults to open.
        bool allowed = service is { } found
            && LayerAccess
                .Evaluate(found.Sharing, null, current.Principal, current.Authorization)
                .IsAllowed();

        if (allowed)
        {
            return await next(invocation).ConfigureAwait(false);
        }

        // <b>Returned, not executed here.</b> Writing the response from inside
        // the filter left the POST body unread, and Kestrel reset the connection
        // rather than sending the 404 — the client saw "the response ended
        // prematurely", which is a worse answer than any status code. Handing
        // the result back lets the framework finish the request properly.
        return Refusal;
    }

    /// <summary>The service document.</summary>
    private static IResult Describe(HttpContext context)
    {
        var document = new
        {
        currentVersion = FeatureServerMetadataWriter.CurrentVersion,
        serviceDescription = "Geometry operations that are linear in the size of their input.",

        // <b>What is here, said as a list.</b> ArcGIS clients probe by calling;
        // saying so up front turns a series of 501s into one document.
        supportedOperations = Supported,
        unsupportedOperations = Blocked,
        maximumVertices = MaximumVertices,
        note = "Operations requiring general polygon overlay are not offered. Measurement "
             + "(benchmarks/geometry-overlay) found a 6,408-vertex input costing 153 seconds and "
             + "16.7 GB where a real 72,919-vertex polygon cost 312 ms — so no cap on input size "
             + "bounds the work, and an unauthenticated request could take the server down. "
             + "Tracked as Q-97.",
        };

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            return Results.Content(
                RestDirectory.Document(context.Request.Path, "Geometry (GeometryServer)", document),
                "text/html; charset=utf-8");
        }

        return Results.Ok(document);
    }

    /// <summary>Refuses an operation that needs overlay, and says why.</summary>
    private static Task Refuse(HttpContext context, string operation) =>
        Results.Json(
            new
            {
                error = new
                {
                    code = 501,
                    message =
                        $"'{operation}' is not implemented. It needs general polygon overlay, and "
                        + "measurement found that no cap on input size bounds that work: a "
                        + "6,408-vertex adversarial input cost 153 seconds and 16.7 GB where a "
                        + "real 72,919-vertex polygon cost 312 ms and 17 MB. Offering it would "
                        + "mean one request could take this server down. See "
                        + "benchmarks/geometry-overlay/RESULTS.md and Q-97. The operations that "
                        + "are linear in their input — project, areasAndLengths, lengths, "
                        + "labelPoints — are available.",
                },
            },
            statusCode: StatusCodes.Status501NotImplemented)
            .ExecuteAsync(context);

    // ---------- project ----------

    /// <summary>
    /// Moves geometries between coordinate reference systems.
    /// </summary>
    /// <remarks>
    /// <b>The response says which PROJ did it.</b> geometry-crs-policy §3: several
    /// transformation paths usually exist between two systems and they differ by
    /// metres, which for cadastral and survey work is legally significant. PROJ
    /// picks one, and falls back to a ballpark transformation when the grids for
    /// the accurate path are missing — without failing. A silent default is the
    /// problem; a documented one is not.
    /// </remarks>
    private static async Task ProjectAsync(
        HttpContext context, IProjector projector, CancellationToken cancellation)
    {
        if (!TryForm(context, out IFormCollection form, out string? formError))
        {
            await Fail(context, formError!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(form, "inSR", out int inSr, out string? sridError)
            || !TrySrid(form, "outSR", out int outSr, out sridError))
        {
            await Fail(context, sridError!).ConfigureAwait(false);
            return;
        }

        if (!TryGeometries(form, inSr, out List<Geometry> geometries, out GeometryKind kind,
                out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        (IReadOnlyList<Geometry> projected, ProjectionProvenance provenance) =
            await projector.ProjectAsync(geometries, inSr, outSr, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            geometries = projected.Select(g => ToJson(g, outSr)).ToArray(),
            transformation = new
            {
                engine = provenance.Engine,
                fromSR = inSr,
                toSR = outSr,

                // Null rather than a number, because ST_Transform does not report
                // which pipeline it chose. Pinning a pipeline is what
                // geometry-crs-policy §3 asks for and nobody has designed it.
                accuracyMetres = provenance.Accuracy,
                note = "The transformation path was chosen by PROJ. Where several exist they "
                     + "differ by metres, and pinning one is not yet supported.",
            },
        }).ExecuteAsync(context).ConfigureAwait(false);

        _ = kind;
    }

    // ---------- measures ----------

    /// <summary>Planar area and perimeter of each polygon.</summary>
    private static async Task AreasAndLengths(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            areas = geometries.Select(GeometryMeasures.Area).ToArray(),
            lengths = geometries.Select(GeometryMeasures.Length).ToArray(),
            note = PlanarNote,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Planar length of each geometry.</summary>
    private static async Task Lengths(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            lengths = geometries.Select(GeometryMeasures.Length).ToArray(),
            note = PlanarNote,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A point inside each polygon, for placing a label.</summary>
    private static async Task LabelPoints(HttpContext context)
    {
        if (!TryForm(context, out IFormCollection form, out string? formError))
        {
            await Fail(context, formError!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(form, "sr", out int srid, out string? sridError))
        {
            await Fail(context, sridError!).ConfigureAwait(false);
            return;
        }

        if (!TryGeometries(form, srid, out List<Geometry> geometries, out _, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            labelPoints = geometries
                .Select(GeometryMeasures.LabelPoint)
                .Where(p => p is not null)
                .Select(p => ToJson(p!, srid))
                .ToArray(),
            note = "A point guaranteed to be inside the polygon, not its centroid — the centroid "
                 + "of a crescent falls outside it, which puts the label in the sea.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private const string PlanarNote =
        "Planar measurement, in the units of the spatial reference. These are NOT geodesic: in "
        + "Web Mercator, area is overstated by sec squared of the latitude — roughly 1.75x at "
        + "Istanbul and 4x at Helsinki. Geodesic measurement is a different calculation and is "
        + "not offered rather than being offered wrongly.";

    // ---------- reading the request ----------

    private static bool TryMeasurable(
        HttpContext context, out List<Geometry> geometries, out string? error)
    {
        geometries = [];

        if (!TryForm(context, out IFormCollection form, out error))
        {
            return false;
        }

        if (!TrySrid(form, "sr", out int srid, out error))
        {
            return false;
        }

        return TryGeometries(form, srid, out geometries, out _, out error);
    }

    private static bool TryForm(HttpContext context, out IFormCollection form, out string? error)
    {
        error = null;

        if (context.Request.HasFormContentType)
        {
            try
            {
                form = context.Request.Form;
                return true;
            }
            catch (System.IO.InvalidDataException e)
            {
                // <b>A body too large is the caller's problem, not a fault.</b>
                // Uncaught this reached the exception handler as a 500 saying
                // "the server failed to handle this request" — for a request
                // that was refused correctly, by a limit nobody had documented,
                // before the documented one could apply.
                form = FormCollection.Empty;
                error =
                    "The request body is larger than this server will read in one form. "
                    + $"The documented bound is {MaximumVertices:N0} vertices; split the batch. "
                    + $"({e.Message})";
                return false;
            }
        }

        form = FormCollection.Empty;

        // ArcGIS clients post these form-encoded. Saying so beats a parse error
        // about a field that was never going to be found.
        error = "GeometryServer operations are posted form-encoded, as ArcGIS clients send them. "
              + "Set Content-Type: application/x-www-form-urlencoded.";
        return false;
    }

    private static bool TrySrid(IFormCollection form, string name, out int srid, out string? error)
    {
        srid = 0;
        error = null;

        string? raw = Field(form, name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"'{name}' is required and names the spatial reference as a well-known id.";
            return false;
        }

        // An SR can arrive as a bare number or as {"wkid":3857}, and both are
        // what real clients send.
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
        {
            srid = Canonical(srid);
            return true;
        }

        try
        {
            JsonElement json = JsonDocument.Parse(raw).RootElement;

            if (json.TryGetProperty("wkid", out JsonElement wkid) && wkid.TryGetInt32(out srid))
            {
                srid = Canonical(srid);
                return true;
            }
        }
        catch (JsonException)
        {
            // Falls through to the message below, which is more use than the
            // parser's.
        }

        error = $"'{name}' must be a well-known id, either as a number or as {{\"wkid\": 3857}}.";
        return false;
    }

    /// <summary>102100 and 102113 are Esri's codes for EPSG:3857.</summary>
    private static int Canonical(int wkid) => wkid switch
    {
        102100 or 102113 => 3857,
        _ => wkid,
    };

    /// <summary>
    /// Reads the <c>geometries</c> field, enforcing the vertex cap.
    /// </summary>
    /// <remarks>
    /// <b>The cap is counted while parsing, not after.</b> Counting afterwards
    /// means a 200 MB body is fully materialised before being refused, which
    /// makes the cap an accounting exercise rather than a defence.
    /// </remarks>
    private static bool TryGeometries(
        IFormCollection form,
        int srid,
        out List<Geometry> geometries,
        out GeometryKind kind,
        out string? error)
    {
        geometries = [];
        kind = GeometryKind.Point;
        error = null;

        string? raw = Field(form, "geometries");

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "'geometries' is required: {\"geometryType\":\"esriGeometryPolygon\","
                  + "\"geometries\":[ ... ]}.";
            return false;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            error = $"'geometries' is not valid JSON: {e.Message}";
            return false;
        }

        // Both shapes are sent: a bare array, and the documented wrapper object.
        JsonElement array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("geometries", out JsonElement inner) ? inner : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
            error = "'geometries' must be an array, or an object with a 'geometries' array.";
            return false;
        }

        int vertices = 0;

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (!ArcGisGeometryReader.TryRead(element, srid, out Geometry? geometry, out error))
            {
                return false;
            }

            vertices += geometry!.CoordinateCount;

            if (vertices > MaximumVertices)
            {
                error =
                    $"The request carries more than {MaximumVertices:N0} vertices. Every operation "
                    + "on this surface is a single pass over the coordinates, so this cap is a "
                    + "real bound on the work — unlike a vertex cap on overlay, which measurement "
                    + "showed bounds nothing (Q-97). Split the batch.";
                geometries = [];
                return false;
            }

            geometries.Add(geometry);
            kind = geometry.Kind;
        }

        return true;
    }

    private static string? Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.ToString()
                : null;

    private static JsonElement ToJson(Geometry geometry, int srid)
    {
        using System.IO.MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            ArcGisGeometryWriter.Write(writer, geometry, srid);
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static Task Fail(HttpContext context, string message) =>
        Results.Json(
            new { error = new { code = 400, message } },
            statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
}
