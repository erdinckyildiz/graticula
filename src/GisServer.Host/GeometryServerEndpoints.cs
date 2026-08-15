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
        ["project", "areasAndLengths", "lengths", "labelPoints",
         "convexHull", "densify", "generalize"];

    /// <summary>Overlay, which runs in a worker process it can be killed in.</summary>
    /// <remarks>
    /// <b>These were refused until Q-97 was answered.</b> The answer is not a
    /// cap — measurement showed no property of the input predicts the cost — it
    /// is a process with a deadline and a heap ceiling. See
    /// <see cref="OverlayWorkerPool"/>.
    /// </remarks>
    private static readonly string[] Overlay = ["intersect", "difference", "union"];

    /// <summary>
    /// Operations not implemented, each with the reason it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One reason each, because they do not share one.</b> Every refusal used
    /// to say <em>"it needs general polygon overlay"</em>, which is true of
    /// <c>cut</c> and false of <c>distance</c> — <c>ST_Distance</c> does no
    /// overlay at all. The owner noticed by comparing this service against a
    /// real ArcGIS one: 22 operations there, 7 here, and a refusal that blamed
    /// the same cause for all of them. Telling a caller something untrue about
    /// why they cannot have a thing is worse than the missing thing.
    /// </para>
    /// <para>
    /// <b>The list shrank on 2026-08-15</b> — <c>convexHull</c>, <c>densify</c>
    /// and <c>generalize</c> moved to <see cref="Supported"/>, computed in
    /// process. They were refused on an argument about asymptotics that
    /// ADR-022 condition 2 had already flagged as the kind of reasoning
    /// measurement overturns.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Blocked = new(StringComparer.Ordinal)
    {
        ["cut"] =
            "It splits a polygon by a line, which is general overlay: measurement found no cap "
            + "on input size bounds that work \u2014 a 6,408-vertex adversarial input cost 153 "
            + "seconds and 16.7 GB where a real 72,919-vertex polygon cost 312 ms. It belongs in "
            + "the overlay worker beside intersect, union and difference, and is not there yet.",

        ["buffer"] =
            "Offsetting a boundary needs curve construction and self-intersection repair. It is "
            + "bounded by the input, unlike overlay, so this is a matter of not having written "
            + "it rather than of it being unsafe to offer.",

        ["offset"] =
            "Same as buffer: curve offsetting, bounded by the input, not yet written.",

        ["simplify"] =
            "ArcGIS 'simplify' repairs topology \u2014 it makes a geometry valid \u2014 which is a "
            + "different and much harder operation than reducing vertices. Reducing vertices is "
            + "'generalize', and that is available. Offering topology repair under this name "
            + "when it only generalised would be the worst kind of compatibility.",

        ["relation"] =
            "It evaluates a DE-9IM pattern between two geometries, which needs a topology engine. "
            + "One exists in the overlay worker; wiring this to it is work nobody has done.",

        ["distance"] =
            "Minimum distance between two geometries is O(n\u00d7m) over segment pairs in the "
            + "general case, which is bounded but not cheap, and the containment case needs a "
            + "point-in-polygon test to answer zero correctly. Not written, and not refused for "
            + "any deeper reason than that.",

        ["autoComplete"] =
            "It closes a polygon against its neighbours, which is an editing operation over a "
            + "set of existing features rather than a calculation on the geometry sent.",

        ["reshape"] =
            "An editing operation: it replaces part of a boundary with a supplied line. Needs the "
            + "same topology engine as relation.",

        ["trimExtend"] =
            "An editing operation on lines against a trimming geometry. Needs the same topology "
            + "engine.",
    };

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
            .AddEndpointFilter(SharingFilter)
            .AddEndpointFilter(FormFilter)
            .Governed(SharingGovernedExtensions.BySystemService);

        geometry.MapGet("", Describe);

        // <b>GET as well as POST, on every operation.</b> Two reasons, and both
        // are decisions rather than convenience. Each of these is a pure
        // function — nothing is stored and running one twice differs from
        // running it once only in the electricity — so GET is the honest verb.
        // And GET is the only verb the browsing cookie authenticates
        // (Authentication.CookieToken), so it is the only verb a form page can
        // use. POST stays for clients with a bearer token and a body too large
        // for a URL, which is what ArcGIS clients send.
        geometry.MapMethods("/project", GetOrPost, ProjectAsync);
        geometry.MapMethods("/areasAndLengths", GetOrPost, AreasAndLengths);
        geometry.MapMethods("/lengths", GetOrPost, Lengths);
        geometry.MapMethods("/labelPoints", GetOrPost, LabelPoints);

        // <b>In process, on flat arrays.</b> The geometry arrives in the request,
        // so there is nothing to push down to \u2014 sending it to the datastore
        // would create the round trip that four benchmark rounds identified as
        // this system's ceiling, to avoid writing a monotone chain. See
        // GeometryOperations.
        geometry.MapMethods("/convexHull", GetOrPost, ConvexHull);
        geometry.MapMethods("/densify", GetOrPost, Densify);
        geometry.MapMethods("/generalize", GetOrPost, Generalize);

        foreach (string operation in Overlay)
        {
            string name = operation;
            geometry.MapMethods($"/{name}", GetOrPost, (
                HttpContext context, IOverlay overlay, CancellationToken cancellation) =>
                OverlayAsync(context, overlay, name, cancellation));
        }

        foreach (string operation in Blocked.Keys)
        {
            string name = operation;
            geometry.MapMethods($"/{name}", GetOrPost, (HttpContext context) =>
                Refuse(context, name));
        }
    }

    private static readonly string[] GetOrPost = ["GET", "POST"];

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

    /// <summary>
    /// Shows the operation's form instead of running it, when that is what was
    /// asked for.
    /// </summary>
    /// <remarks>
    /// <b>A filter, so no handler can forget.</b> Eight handlers each checking
    /// is eight chances to ship one that queries the moment its link is clicked
    /// — which is exactly the correction the layer query page needed, and there
    /// the check lived in the handler.
    /// </remarks>
    private static ValueTask<object?> FormFilter(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;

        if (HttpMethods.IsGet(context.Request.Method)
            && RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept)
            && GeometryPage.WantsForm(context.Request)
            && OperationOf(context) is { } operation)
        {
            return ValueTask.FromResult<object?>(Html(
                GeometryPage.Form(context.Request.Path, operation, context.Request.Query)));
        }

        return next(invocation);
    }

    /// <summary>The operation this path names, or null if this surface has no page for it.</summary>
    private static GeometryPage.Operation? OperationOf(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        int slash = path.LastIndexOf('/');

        return slash < 0 ? null : GeometryPage.Find(path[(slash + 1)..]);
    }

    private static IResult Html(string page) => Results.Content(page, "text/html; charset=utf-8");

    /// <summary>The service document.</summary>
    private static IResult Describe(HttpContext context)
    {
        var document = new
        {
        currentVersion = FeatureServerMetadataWriter.CurrentVersion,
        serviceDescription = "Geometry operations that are linear in the size of their input.",

        // <b>What is here, said as a list.</b> ArcGIS clients probe by calling;
        // saying so up front turns a series of 501s into one document.
        supportedOperations = Supported.Concat(Overlay).ToArray(),
        unsupportedOperations = Blocked.Keys,
        maximumVertices = MaximumVertices,
        maximumCandidatePairs = OverlayWorkerPool.MaximumCandidatePairs,
        overlayDeadlineSeconds = OverlayWorkerPool.Deadline.TotalSeconds,
        note = "Overlay operations run in a separate worker process with a "
             + $"{OverlayWorkerPool.Deadline.TotalSeconds:0}-second deadline and a "
             + "1 GB heap ceiling, and a pre-flight refuses inputs whose estimated crossing count "
             + "exceeds maximumCandidatePairs. Measurement (benchmarks/geometry-overlay) found a "
             + "6,408-vertex input costing 153 seconds and 16.7 GB where a real 72,919-vertex "
             + "polygon cost 312 ms, so no cap on input size bounds the work — the bound is the "
             + "process, not the input. Q-97.",
        };

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            // <b>Each operation is a link to a page you can run it from.</b> The
            // owner asked why the capabilities were not listed under Utilities;
            // they were, as a bulleted list of words. A name you cannot click is
            // documentation, and this is a services directory.
            string root = context.Request.Path.Value!.TrimEnd('/');

            var links = GeometryPage.Operations
                .Select(o => (Label: o.Name, Href: $"{root}/{o.Name}"))
                .ToArray();

            return Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    "Geometry (GeometryServer)",
                    document,
                    links,
                    "Supported operations"),
                "text/html; charset=utf-8");
        }

        return Results.Ok(document);
    }

    /// <summary>Refuses an operation that needs overlay, and says why.</summary>
    /// <remarks>
    /// <b>Rendered as a page for a browser.</b> These are reachable by clicking:
    /// the service document lists them under unsupportedOperations, and somebody
    /// following that name deserves the reason rather than a JSON blob.
    /// </remarks>
    private static Task Refuse(HttpContext context, string operation)
    {
        object document = new
        {
            error = new
            {
                code = 501,
                    message =
                        $"'{operation}' is not implemented. "
                    + (Blocked.TryGetValue(operation, out string? why)
                        ? why
                        : "No reason is recorded, which is itself a defect.")
                    + " Available: " + string.Join(", ", Supported.Concat(Overlay)) + ".",
            },
        };

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;

            return Html(RestDirectory.Document(
                context.Request.Path, $"{operation} (not implemented)", document))
                .ExecuteAsync(context);
        }

        return Results.Json(document, statusCode: StatusCodes.Status501NotImplemented)
            .ExecuteAsync(context);
    }

    // ---------- overlay ----------

    /// <summary>
    /// intersect, union and difference, in a process with a deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The endpoint is thin because the interesting part is elsewhere.</b>
    /// All this does is read two sets of geometries and hand them to
    /// <see cref="IOverlay"/>; the bound that makes the operation safe to offer
    /// is a worker process being killed, and that lives in
    /// <see cref="OverlayWorkerPool"/>.
    /// </para>
    /// <para>
    /// <b>Every refusal is its own status.</b> A pre-flight refusal is a 400 —
    /// the caller sent something too expensive and can send something smaller.
    /// A deadline or an out-of-memory is a 503 with <c>Retry-After</c> absent
    /// deliberately: retrying the same request produces the same outcome, and
    /// saying "try in 30 seconds" would be a lie.
    /// </para>
    /// </remarks>
    private static async Task OverlayAsync(
        HttpContext context, IOverlay overlay, string operation, CancellationToken cancellation)
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

        if (!TryGeometries(form, srid, out List<Geometry> left, out _, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        List<Geometry> right = [];

        // union takes one set; intersect and difference take a second operand,
        // which ArcGIS spells "geometry" beside the "geometries" list.
        if (!string.Equals(operation, "union", StringComparison.Ordinal))
        {
            if (!TrySingleGeometry(form, srid, out right, out error))
            {
                await Fail(context, error!).ConfigureAwait(false);
                return;
            }
        }

        OverlayOperation kind = operation switch
        {
            "intersect" => OverlayOperation.Intersect,
            "difference" => OverlayOperation.Difference,
            _ => OverlayOperation.Union,
        };

        OverlayResult result = await overlay
            .ComputeAsync(kind, left, right, srid, cancellation)
            .ConfigureAwait(false);

        if (result.Refusal is not OverlayRefusal.None)
        {
            int status = result.Refusal switch
            {
                OverlayRefusal.TooLarge or OverlayRefusal.Invalid => 400,
                _ => 503,
            };

            await Results.Json(
                new
                {
                    error = new
                    {
                        code = status,
                        message = result.Message,
                        reason = result.Refusal.ToString(),
                        candidatePairs = result.CandidatePairs,
                    },
                },
                statusCode: status).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await Respond(context, operation, new
        {
            geometries = result.Geometries.Select(g => ToJson(g, srid)).ToArray(),

            // <b>Reported, because a caller cannot otherwise tell a cheap
            // overlay from one that nearly hit the deadline.</b> Somebody
            // batching these needs to know they are close to the edge before
            // they cross it.
            cost = new
            {
                candidatePairs = result.CandidatePairs,
                milliseconds = result.Milliseconds,
                candidatePairLimit = OverlayWorkerPool.MaximumCandidatePairs,
                deadlineSeconds = OverlayWorkerPool.Deadline.TotalSeconds,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>The single-geometry operand, which ArcGIS calls "geometry".</summary>
    private static bool TrySingleGeometry(
        IFormCollection form, int srid, out List<Geometry> geometries, out string? error)
    {
        geometries = [];
        error = null;

        string raw = form["geometry"].ToString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            error =
                "'geometry' is required: it is the shape the list in 'geometries' is overlaid "
                + "against. Only 'union' takes a single list.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (!ArcGisGeometryReader.TryRead(document.RootElement, srid, out Geometry? geometry,
                    out error))
            {
                return false;
            }

            geometries = [geometry!];
            return true;
        }
        catch (JsonException e)
        {
            error = $"'geometry' is not valid JSON: {e.Message}";
            return false;
        }
    }

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

        await Respond(context, "project", new
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
        }).ConfigureAwait(false);

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

        await Respond(context, "areasAndLengths", new
        {
            areas = geometries.Select(GeometryMeasures.Area).ToArray(),
            lengths = geometries.Select(GeometryMeasures.Length).ToArray(),
            note = PlanarNote,
        }).ConfigureAwait(false);
    }

    /// <summary>Planar length of each geometry.</summary>
    private static async Task Lengths(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        await Respond(context, "lengths", new
        {
            lengths = geometries.Select(GeometryMeasures.Length).ToArray(),
            note = PlanarNote,
        }).ConfigureAwait(false);
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

        await Respond(context, "labelPoints", new
        {
            labelPoints = geometries
                .Select(GeometryMeasures.LabelPoint)
                .Where(p => p is not null)
                .Select(p => ToJson(p!, srid))
                .ToArray(),
            note = "A point guaranteed to be inside the polygon, not its centroid — the centroid "
                 + "of a crescent falls outside it, which puts the label in the sea.",
        }).ConfigureAwait(false);
    }

    /// <summary>The smallest convex polygon containing every input geometry.</summary>
    /// <remarks>
    /// <b>One hull for the whole set, which is what ArcGIS returns.</b> Hulling
    /// each geometry separately would be a different and less useful operation,
    /// and a caller who wanted that can send one geometry at a time.
    /// </remarks>
    private static async Task ConvexHull(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(context, out int srid, out string? sridError))
        {
            await Fail(context, sridError!).ConfigureAwait(false);
            return;
        }

        Geometry hull = GeometryOperations.ConvexHull(geometries);

        await Respond(context, "convexHull", new
        {
            geometry = ToJson(hull, srid),
            note = "The hull of every input geometry together, which is what ArcGIS returns. "
                 + "A hull of fewer than three distinct points is a point or a line rather than "
                 + "a degenerate polygon.",
        }).ConfigureAwait(false);
    }

    /// <summary>Adds vertices so no segment exceeds a length.</summary>
    private static async Task Densify(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(context, out int srid, out string? sridError))
        {
            await Fail(context, sridError!).ConfigureAwait(false);
            return;
        }

        if (!TryPositive(context, "maxSegmentLength", out double step, out string? stepError))
        {
            await Fail(context, stepError!).ConfigureAwait(false);
            return;
        }

        await Respond(context, "densify", new
        {
            geometries = geometries
                .Select(g => ToJson(GeometryOperations.Densify(g, step), srid)).ToArray(),
            note = "Every original coordinate survives at its original value; densifying only "
                 + "adds. Planar: the length is in the units of the spatial reference, and this "
                 + "is not the geodesic densify ArcGIS also offers.",
        }).ConfigureAwait(false);
    }

    /// <summary>Removes vertices within a tolerance of the line they sit on.</summary>
    private static async Task Generalize(HttpContext context)
    {
        if (!TryMeasurable(context, out List<Geometry> geometries, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        if (!TrySrid(context, out int srid, out string? sridError))
        {
            await Fail(context, sridError!).ConfigureAwait(false);
            return;
        }

        if (!TryPositive(context, "maxDeviation", out double tolerance, out string? tolError,
                allowZero: true))
        {
            await Fail(context, tolError!).ConfigureAwait(false);
            return;
        }

        await Respond(context, "generalize", new
        {
            geometries = geometries
                .Select(g => ToJson(GeometryOperations.Generalize(g, tolerance), srid)).ToArray(),
            note = "Douglas-Peucker. Every surviving vertex is an original one, and a ring keeps "
                 + "enough coordinates to still enclose something. This does NOT repair topology "
                 + "\u2014 that is ArcGIS 'simplify', which this server does not offer rather than "
                 + "offering this in its place.",
        }).ConfigureAwait(false);
    }

    /// <summary>Reads a spatial reference from a request already read as a form.</summary>
    private static bool TrySrid(HttpContext context, out int srid, out string? error)
    {
        srid = 0;
        error = null;

        return TryForm(context, out IFormCollection form, out error)
               && TrySrid(form, "sr", out srid, out error);
    }

    /// <summary>Reads a numeric parameter, refusing the values that make no sense.</summary>
    private static bool TryPositive(
        HttpContext context, string name, out double value, out string? error,
        bool allowZero = false)
    {
        value = 0;
        error = null;

        if (!TryForm(context, out IFormCollection form, out error))
        {
            return false;
        }

        string? raw = Field(form, name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"'{name}' is required, in the units of the spatial reference.";
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"'{name}' must be a number.";
            return false;
        }

        // <b>Zero is a real answer for one of these and an infinite loop for the
        // other.</b> A zero deviation removes exactly the collinear vertices; a
        // zero segment length asks for infinitely many.
        if (value < 0 || (!allowZero && value == 0))
        {
            error = allowZero
                ? $"'{name}' cannot be negative."
                : $"'{name}' must be greater than zero \u2014 zero would ask for an unbounded "
                  + "number of vertices.";
            return false;
        }

        return true;
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

        // <b>A GET carries its parameters in the query, and means the same
        // thing.</b> Converted here rather than threading a second collection
        // through every reader, because the parse rules — the vertex cap, the
        // wkid forms, the two shapes of 'geometries' — must not have two
        // implementations that can disagree about what a request said.
        if (HttpMethods.IsGet(context.Request.Method))
        {
            form = new FormCollection(context.Request.Query.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

            return true;
        }

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

    /// <summary>
    /// Writes the answer, as HTML when the caller is a browser.
    /// </summary>
    /// <remarks>
    /// <b>The same object either way.</b> The HTML is a rendering of the JSON
    /// document, not a second document — so what a person reads on the page and
    /// what a client parses cannot describe different results.
    /// </remarks>
    private static Task Respond(HttpContext context, string title, object document)
    {
        if (!RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            return Results.Json(document).ExecuteAsync(context);
        }

        // A way back to the form. Without it the only route to a second attempt
        // is the browser's back button, and the answer page is a dead end.
        (string, string)[] links =
        [
            ("Change the parameters", context.Request.Path.Value!),
            ("This answer as JSON", context.Request.Path + context.Request.QueryString + "&f=json"),
        ];

        return Html(RestDirectory.Document(
            context.Request.Path, title, document, links, "Then"))
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Refuses the request, and for a browser puts the reason above the form
    /// rather than in a document it cannot read.
    /// </summary>
    /// <remarks>
    /// <b>The values are kept.</b> A refusal that clears the box the caller
    /// spent five minutes pasting a polygon into is a refusal they will work
    /// around by not using the page.
    /// </remarks>
    private static Task Fail(HttpContext context, string message)
    {
        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept)
            && OperationOf(context) is { } operation)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return Html(GeometryPage.Form(
                context.Request.Path, operation, context.Request.Query, message))
                .ExecuteAsync(context);
        }

        return Results.Json(
            new { error = new { code = 400, message } },
            statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }
}
