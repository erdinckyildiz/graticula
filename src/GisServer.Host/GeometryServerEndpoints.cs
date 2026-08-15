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
         "convexHull", "densify", "generalize",
         "toGeoCoordinateString", "fromGeoCoordinateString"];

    /// <summary>
    /// What runs in a worker process that can be killed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three of these were refused until Q-97 was answered, and six more until
    /// 2026-08-15.</b> The answer is not a cap — measurement showed no property
    /// of the input predicts the cost — it is a process with a deadline and a
    /// heap ceiling. See <see cref="GeometryWorkerPool"/>.
    /// </para>
    /// <para>
    /// <b>The owner removed the rule that kept the other six out.</b> They were
    /// refused because they <em>could</em> be expensive, and the instruction was
    /// that this is not the server's judgement to make: if a caller wants to do
    /// something absurd, let them, and put a timeout on it. The bound was already
    /// built — the same deadline and heap limit hold for a buffer as for an
    /// intersection — so the only thing standing between the caller and these
    /// operations was a policy nobody had asked for.
    /// </para>
    /// </remarks>
    private static readonly string[] Engine =
        ["intersect", "difference", "union",
         "cut", "buffer", "offset", "simplify", "relation", "distance"];

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
    /// <b>The list went from twelve to three on 2026-08-15.</b> <c>convexHull</c>,
    /// <c>densify</c> and <c>generalize</c> moved to <see cref="Supported"/>,
    /// computed in process — they had been refused on an argument about
    /// asymptotics that ADR-022 condition 2 already called the kind of reasoning
    /// measurement overturns. <c>cut</c>, <c>buffer</c>, <c>offset</c>,
    /// <c>simplify</c>, <c>relation</c> and <c>distance</c> moved to
    /// <see cref="Engine"/> when the owner ruled that the server bounds cost and
    /// does not decide usefulness.
    /// </para>
    /// <para>
    /// <b>What is left is not refused on cost at all.</b> All three are editing
    /// operations over existing features rather than calculations on the geometry
    /// sent, and the open question is whether they belong on this service or on
    /// FeatureServer. That is a design question, and it has not been answered.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Blocked = new(StringComparer.Ordinal)
    {
        ["findTransformations"] =
            "It lists the datum transformation paths between two spatial references, ranked. "
            + "The paths live in PROJ's own operation database, and this server does not have "
            + "PROJ \u2014 projection is done by the datastore (ADR-022 \u00a74), and PostGIS "
            + "exposes no SQL function that enumerates candidate operations. So this needs "
            + "either PROJ's proj.db in this process, which is about 9 MB of metadata and a "
            + "genuinely different cost from the datum grids ADR-022 \u00a74 declined to ship, "
            + "or a new route to the datastore's copy of it. That choice has not been made "
            + "\u2014 see Q-100. Returning the single path PROJ happened to pick, dressed as "
            + "a ranked list of one, would answer the question a caller asked with something "
            + "that is not an answer to it.",

        ["autoComplete"] =
            "It closes a polygon against its neighbours, which is an editing operation over a "
            + "set of existing features rather than a calculation on the geometry sent.",

        ["reshape"] =
            "An editing operation: it replaces part of a boundary with a supplied line. The "
            + "topology engine it needs is already running — this is a question of whether "
            + "editing an existing feature belongs on this service or on FeatureServer, and that "
            + "has not been answered.",

        ["trimExtend"] =
            "An editing operation on lines against a trimming geometry. Same open question as "
            + "reshape: it edits features rather than calculating on the geometry sent.",
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

        // <b>These two were not on any list until 2026-08-15</b> — neither
        // supported nor refused, so a caller asking for them got 404, which says
        // the operation does not exist rather than that nobody had written it.
        // The owner found them by comparing this service with a real one.
        geometry.MapMethods("/toGeoCoordinateString", GetOrPost, (
            HttpContext context, IProjector projector, CancellationToken cancellation) =>
            ToGeoCoordinateStringAsync(context, projector, cancellation));

        geometry.MapMethods("/fromGeoCoordinateString", GetOrPost, (
            HttpContext context, IProjector projector, CancellationToken cancellation) =>
            FromGeoCoordinateStringAsync(context, projector, cancellation));

        foreach (string operation in Engine)
        {
            string name = operation;
            geometry.MapMethods($"/{name}", GetOrPost, (
                HttpContext context, IGeometryEngine engine, CancellationToken cancellation) =>
                EngineAsync(context, engine, name, cancellation));
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
        serviceDescription =
            "Geometry operations. Those linear in their input run in process; those that need a "
            + "topology engine run in a worker process with a deadline.",

        // <b>What is here, said as a list.</b> ArcGIS clients probe by calling;
        // saying so up front turns a series of 501s into one document.
        supportedOperations = Supported.Concat(Engine).ToArray(),
        unsupportedOperations = Blocked.Keys,
        maximumVertices = MaximumVertices,
        maximumCandidatePairs = GeometryWorkerPool.MaximumCandidatePairs,
        deadlineSeconds = GeometryWorkerPool.Deadline.TotalSeconds,
        note = $"{string.Join(", ", Engine)} run in a separate worker process with a "
             + $"{GeometryWorkerPool.Deadline.TotalSeconds:0}-second deadline and a "
             + "1 GB heap ceiling. That, and nothing about the input, is the bound: measurement "
             + "(benchmarks/geometry-overlay) found a 6,408-vertex input costing 153 seconds and "
             + "16.7 GB where a real 72,919-vertex polygon cost 312 ms. maximumCandidatePairs is "
             + "an optional pre-flight, zero here meaning off — it was measured "
             + "under-predicting by fourteen times, and the server does not decide on the "
             + "caller's behalf what is worth attempting. Q-97.",
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
                    + " Available: " + string.Join(", ", Supported.Concat(Engine)) + ".",
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

    // ---------- grid and sexagesimal strings ----------

    /// <summary>
    /// The notations this server writes, by their ArcGIS names.
    /// </summary>
    /// <remarks>
    /// <b>GARS and GEOREF are absent and that is a gap, not a decision.</b> Both
    /// are simple cell schemes and neither is written here yet. They are named
    /// in the refusal so a caller learns which of the eight they can have.
    /// </remarks>
    private static readonly Dictionary<string, GeoCoordinateNotation> Notations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DD"] = GeoCoordinateNotation.DecimalDegrees,
            ["DDM"] = GeoCoordinateNotation.DegreesDecimalMinutes,
            ["DMS"] = GeoCoordinateNotation.DegreesMinutesSeconds,
            ["UTM"] = GeoCoordinateNotation.Utm,
            ["MGRS"] = GeoCoordinateNotation.Mgrs,
            ["USNG"] = GeoCoordinateNotation.Usng,
        };

    /// <summary>Writes coordinates as grid or sexagesimal strings.</summary>
    /// <remarks>
    /// <para>
    /// <b>Projection first, conversion in process.</b> The conversion is
    /// arithmetic on two doubles and belongs here (ADR-022 §4b). Getting the
    /// caller's coordinates into geographic degrees is a datum question, and
    /// that is the datastore's PROJ — the same exception <c>project</c> is.
    /// </para>
    /// <para>
    /// <b>A caller already in 4326 pays no round trip.</b> Checked rather than
    /// assumed: converting a batch of WGS84 points is the common case and would
    /// otherwise cost a database call to change nothing.
    /// </para>
    /// </remarks>
    private static async Task ToGeoCoordinateStringAsync(
        HttpContext context, IProjector projector, CancellationToken cancellation)
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

        if (!TryNotation(form, out GeoCoordinateNotation notation, out string? error)
            || !TryCoordinates(form, srid, out List<Geometry> points, out error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<Geometry> geographic = points;
        ProjectionProvenance provenance = new("none \u2014 already geographic", null);

        if (srid != 4326)
        {
            (geographic, provenance) = await projector
                .ProjectAsync(points, srid, 4326, cancellation)
                .ConfigureAwait(false);
        }

        int digits = FieldInt(form, "numOfDigits", notation switch
        {
            GeoCoordinateNotation.Mgrs or GeoCoordinateNotation.Usng => 5,
            GeoCoordinateNotation.Utm => 0,
            _ => 4,
        });

        bool spaces = !string.Equals(Field(form, "addSpaces"), "false",
            StringComparison.OrdinalIgnoreCase);

        List<string> strings = [];

        for (int i = 0; i < geographic.Count; i++)
        {
            Point point = (Point)geographic[i];

            if (!GeoCoordinateString.TryWrite(
                    point.X, point.Y, notation, digits, spaces, out string text, out error))
            {
                // <b>Named by index.</b> A caller sending two hundred coordinates
                // and getting "outside the UTM grid" back has no way to find
                // which one without bisecting their own request.
                await Fail(context, $"Coordinate {i}: {error}").ConfigureAwait(false);
                return;
            }

            strings.Add(text);
        }

        await Respond(context, "toGeoCoordinateString", new
        {
            strings,
            transformation = provenance.Engine,
            note = notation is GeoCoordinateNotation.Mgrs or GeoCoordinateNotation.Usng
                ? "MGRS and USNG name a square rather than a point, and the digits are "
                  + "truncated rather than rounded \u2014 rounding 99,999 up would name the "
                  + "square next door. Five digits per axis is one metre."
                : "Written on a WGS84-shaped ellipsoid. Coordinates in another reference are "
                  + "projected to 4326 by the datastore's PROJ first, and 'transformation' "
                  + "says what did it.",
        }).ConfigureAwait(false);
    }

    /// <summary>Reads grid or sexagesimal strings back into coordinates.</summary>
    private static async Task FromGeoCoordinateStringAsync(
        HttpContext context, IProjector projector, CancellationToken cancellation)
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

        if (!TryNotation(form, out GeoCoordinateNotation notation, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        string raw = Field(form, "strings") ?? string.Empty;

        if (raw.Length == 0)
        {
            error = "'strings' is required: a JSON array of coordinate strings.";
            await Fail(context, error).ConfigureAwait(false);
            return;
        }

        List<string> inputs = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                await Fail(context, "'strings' must be a JSON array.").ConfigureAwait(false);
                return;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                inputs.Add(element.GetString() ?? string.Empty);
            }
        }
        catch (JsonException e)
        {
            await Fail(context, $"'strings' is not valid JSON: {e.Message}")
                .ConfigureAwait(false);
            return;
        }

        if (inputs.Count > MaximumStrings)
        {
            await Fail(
                context,
                $"{inputs.Count} strings were sent and the limit is {MaximumStrings}. Each one "
                + "is parsed independently, so send them in batches.")
                .ConfigureAwait(false);
            return;
        }

        List<Geometry> points = [];

        for (int i = 0; i < inputs.Count; i++)
        {
            if (!GeoCoordinateString.TryRead(
                    inputs[i], notation, out double longitude, out double latitude, out error))
            {
                await Fail(context, $"String {i}: {error}").ConfigureAwait(false);
                return;
            }

            points.Add(new Point(longitude, latitude));
        }

        IReadOnlyList<Geometry> result = points;
        ProjectionProvenance provenance = new("none \u2014 already geographic", null);

        if (srid != 4326)
        {
            (result, provenance) = await projector
                .ProjectAsync(points, 4326, srid, cancellation)
                .ConfigureAwait(false);
        }

        await Respond(context, "fromGeoCoordinateString", new
        {
            coordinates = result
                .Select(g => new[] { ((Point)g).X, ((Point)g).Y }).ToArray(),
            transformation = provenance.Engine,
            note = "A grid reference names a square, so a reference shorter than ten digits "
                 + "reads back to the centre of the square it names rather than its "
                 + "south-west corner \u2014 a two-digit reference is a ten-kilometre square, "
                 + "and returning its corner would be five kilometres of avoidable error.",
        }).ConfigureAwait(false);
    }

    /// <summary>How many strings one request may convert.</summary>
    /// <remarks>
    /// <b>A designed limit rather than a framework one.</b> Parsing is linear and
    /// cheap, so this is about the response size and about there being a stated
    /// number at all: security.md's rule is that a framework limit is not a
    /// designed limit.
    /// </remarks>
    public const int MaximumStrings = 10_000;

    private static bool TryNotation(
        IFormCollection form, out GeoCoordinateNotation notation, out string? error)
    {
        notation = GeoCoordinateNotation.DecimalDegrees;
        error = null;

        string requested = Field(form, "conversionType") ?? string.Empty;

        if (requested.Length == 0)
        {
            error =
                "'conversionType' is required: one of " + string.Join(", ", Notations.Keys) + ".";
            return false;
        }

        if (Notations.TryGetValue(requested, out notation))
        {
            return true;
        }

        error =
            $"'{requested}' is not a notation this server writes. Available: "
            + string.Join(", ", Notations.Keys) + ". GARS and GEOREF are ArcGIS types that are "
            + "not implemented here \u2014 both are simple cell schemes and this is a gap "
            + "rather than a decision.";

        return false;
    }

    /// <summary>
    /// The coordinate list, which ArcGIS spells as bare number pairs.
    /// </summary>
    /// <remarks>
    /// <b>Points, not geometries.</b> <c>toGeoCoordinateString</c> takes
    /// <c>[[x, y], ...]</c> rather than the geometry wrapper every other
    /// operation here uses, and accepting the wrapper as well would mean two
    /// input shapes for one operation.
    /// </remarks>
    private static bool TryCoordinates(
        IFormCollection form, int srid, out List<Geometry> points, out string? error)
    {
        points = [];
        error = null;

        string raw = Field(form, "coordinates") ?? string.Empty;

        if (raw.Length == 0)
        {
            error = "'coordinates' is required: a JSON array of [x, y] pairs.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "'coordinates' must be a JSON array of [x, y] pairs.";
                return false;
            }

            int index = 0;

            foreach (JsonElement pair in document.RootElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                {
                    error = $"Coordinate {index} is not a pair of numbers.";
                    return false;
                }

                points.Add(new Point(pair[0].GetDouble(), pair[1].GetDouble()));
                index++;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException
                                       or FormatException)
        {
            error = $"'coordinates' is not a valid array of number pairs: {e.Message}";
            return false;
        }

        if (points.Count == 0)
        {
            error = "'coordinates' is empty.";
            return false;
        }

        if (points.Count > MaximumStrings)
        {
            error =
                $"{points.Count} coordinates were sent and the limit is {MaximumStrings}.";
            return false;
        }

        return true;
    }

    private static int FieldInt(IFormCollection form, string name, int fallback) =>
        int.TryParse(Field(form, name), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value)
            ? value
            : fallback;

    // ---------- the worker-backed operations ----------

    /// <summary>
    /// Everything that needs a topology engine, in a process with a deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The endpoint is thin because the interesting part is elsewhere.</b>
    /// All this does is read operands and hand them to
    /// <see cref="IGeometryEngine"/>; the bound that makes these operations safe
    /// to offer is a worker process being killed, and that lives in
    /// <see cref="GeometryWorkerPool"/>.
    /// </para>
    /// <para>
    /// <b>Nine operations rather than three since 2026-08-15.</b> The bound is
    /// what makes any of them offerable, and the bound does not care which one it
    /// is. See <see cref="Engine"/> for the decision that changed.
    /// </para>
    /// <para>
    /// <b>Every refusal is its own status.</b> A pre-flight refusal is a 400 —
    /// the caller sent something too expensive and can send something smaller.
    /// A deadline or an out-of-memory is a 503 with <c>Retry-After</c> absent
    /// deliberately: retrying the same request produces the same outcome, and
    /// saying "try in 30 seconds" would be a lie.
    /// </para>
    /// </remarks>
    private static async Task EngineAsync(
        HttpContext context,
        IGeometryEngine engine,
        string operation,
        CancellationToken cancellation)
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

        if (!TryOperands(form, operation, srid, out EngineRequest request, out string? error))
        {
            await Fail(context, error!).ConfigureAwait(false);
            return;
        }

        EngineResult result = await engine
            .ComputeAsync(request, cancellation)
            .ConfigureAwait(false);

        if (result.Refusal is not EngineRefusal.None)
        {
            int status = result.Refusal switch
            {
                EngineRefusal.TooLarge or EngineRefusal.Invalid => 400,
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

        // <b>Reported, because a caller cannot otherwise tell a cheap request
        // from one that nearly hit the deadline.</b> Somebody batching these
        // needs to know they are close to the edge before they cross it.
        object cost = new
        {
            candidatePairs = result.CandidatePairs,
            milliseconds = result.Milliseconds,
            candidatePairLimit = GeometryWorkerPool.MaximumCandidatePairs,
            deadlineSeconds = GeometryWorkerPool.Deadline.TotalSeconds,
        };

        if (result.Scalar is double scalar)
        {
            await Respond(context, operation, new { distance = scalar, cost, note = PlanarNote })
                .ConfigureAwait(false);
            return;
        }

        if (result.Pairs is not null)
        {
            await Respond(context, operation, new
            {
                relations = result.Pairs
                    .Select(pair => new { geometry1Index = pair[0], geometry2Index = pair[1] })
                    .ToArray(),

                // The first pair's DE-9IM, so a caller whose predicate matched
                // nothing can see what the relation actually was instead of
                // guessing at their pattern.
                firstMatrix = result.Matrix,
                cost,
            }).ConfigureAwait(false);
            return;
        }

        await Respond(context, operation, new
        {
            geometries = result.Geometries.Select(g => ToJson(g, srid)).ToArray(),
            cost,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads whatever operands the named operation takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nine operations spell their operands nine ways, and this is where that
    /// lives.</b> ArcGIS names them per operation — <c>target</c> and
    /// <c>cutter</c> for cut, <c>geometry1</c> and <c>geometry2</c> for distance,
    /// <c>geometries1</c> and <c>geometries2</c> for relation — and a caller
    /// following Esri's documentation should not have to translate. The
    /// generic <c>geometries</c> is accepted everywhere as well, because it is
    /// what the form pages on this server send.
    /// </para>
    /// <para>
    /// <b>A missing distance is an error rather than a zero.</b> A zero buffer
    /// returns the input and a zero offset returns nothing, and either would look
    /// to a caller like the operation silently doing nothing.
    /// </para>
    /// </remarks>
    private static bool TryOperands(
        IFormCollection form,
        string operation,
        int srid,
        out EngineRequest request,
        out string? error)
    {
        request = default;
        error = null;

        List<Geometry> left;
        List<Geometry> right = [];
        double distance = 0;
        string? pattern = null;

        switch (operation)
        {
            case "cut":
                if (!TryNamedGeometry(form, "target", srid, out left, out error)
                    || !TryNamedGeometry(form, "cutter", srid, out right, out error))
                {
                    return false;
                }

                break;

            case "distance":
                if (!TryNamedGeometry(form, "geometry1", srid, out left, out error)
                    || !TryNamedGeometry(form, "geometry2", srid, out right, out error))
                {
                    return false;
                }

                break;

            case "relation":
                if (!TryGeometries(form, srid, out left, out _, out error, "geometries1")
                    || !TryGeometries(form, srid, out right, out _, out error, "geometries2"))
                {
                    return false;
                }

                if (!TryRelation(form, out pattern, out error))
                {
                    return false;
                }

                break;

            case "buffer":
            case "offset":
            case "simplify":
            case "union":
                if (!TryGeometries(form, srid, out left, out _, out error))
                {
                    return false;
                }

                if (operation is "buffer" or "offset")
                {
                    string field = operation == "buffer" ? "distances" : "offsetDistance";

                    if (!TryDistance(form, field, out distance, out error))
                    {
                        return false;
                    }
                }

                break;

            default:
                // intersect and difference: a list against one shape, which
                // ArcGIS spells "geometry" beside "geometries".
                if (!TryGeometries(form, srid, out left, out _, out error)
                    || !TrySingleGeometry(form, srid, out right, out error))
                {
                    return false;
                }

                break;
        }

        EngineOperation kind = operation switch
        {
            "intersect" => EngineOperation.Intersect,
            "difference" => EngineOperation.Difference,
            "cut" => EngineOperation.Cut,
            "buffer" => EngineOperation.Buffer,
            "offset" => EngineOperation.Offset,
            "simplify" => EngineOperation.Simplify,
            "relation" => EngineOperation.Relate,
            "distance" => EngineOperation.Distance,
            _ => EngineOperation.Union,
        };

        request = new EngineRequest(kind, left, right, srid)
        {
            Distance = distance,
            Pattern = pattern,
        };

        return true;
    }

    /// <summary>
    /// The Esri relation name, or the DE-9IM pattern it stands for.
    /// </summary>
    /// <remarks>
    /// <b>Four of Esri's relation names are refused rather than approximated.</b>
    /// <c>InteriorIntersection</c>, <c>LineCoincidence</c>, <c>LineTouch</c> and
    /// <c>PointTouch</c> are refinements of the standard predicates whose exact
    /// semantics are Esri's, and mapping them to the nearest DE-9IM pattern would
    /// produce answers that are right most of the time. A wrong spatial predicate
    /// is not a degraded answer — it is a caller filtering the wrong features and
    /// never finding out.
    /// </remarks>
    private static bool TryRelation(IFormCollection form, out string? pattern, out string? error)
    {
        pattern = null;
        error = null;

        string relation = Field(form, "relation") ?? string.Empty;
        string parameter = Field(form, "relationParam") ?? string.Empty;

        if (relation.Length == 0 && parameter.Length == 0)
        {
            error =
                "'relation' is required — one of esriGeometryRelationDisjoint, "
                + "esriGeometryRelationIntersection, esriGeometryRelationWithin, "
                + "esriGeometryRelationTouch, esriGeometryRelationCross, "
                + "esriGeometryRelationOverlap, or esriGeometryRelationRelation with a DE-9IM "
                + "pattern in 'relationParam'.";
            return false;
        }

        if (relation is "esriGeometryRelationRelation" or "" || parameter.Length > 0)
        {
            if (parameter.Length == 0)
            {
                error = "'relationParam' is required: it carries the DE-9IM pattern to match.";
                return false;
            }

            pattern = parameter;
            return true;
        }

        switch (relation)
        {
            case "esriGeometryRelationInteriorIntersection":
            case "esriGeometryRelationLineCoincidence":
            case "esriGeometryRelationLineTouch":
            case "esriGeometryRelationPointTouch":
                error =
                    $"'{relation}' is not offered. It is a refinement of a standard predicate "
                    + "whose exact meaning is Esri's rather than OGC's, and approximating it "
                    + "would return answers that are wrong in the cases it exists to "
                    + "distinguish. Send esriGeometryRelationRelation with the DE-9IM pattern "
                    + "you want in 'relationParam'.";
                return false;

            case "esriGeometryRelationDisjoint":
            case "esriGeometryRelationIntersection":
            case "esriGeometryRelationIn":
            case "esriGeometryRelationWithin":
            case "esriGeometryRelationTouch":
            case "esriGeometryRelationCross":
            case "esriGeometryRelationOverlap":
                pattern = relation;
                return true;

            default:
                // <b>Checked here rather than in the engine.</b> Anything that is
                // not a name we know has to be a DE-9IM pattern, and letting a
                // misspelt relation name travel to the worker produced
                // "Should be length 9: esriGeometryRelationIntersection" — the
                // topology library's complaint about a string it was never meant
                // to see.
                if (!IsDe9im(relation))
                {
                    error =
                        $"'{relation}' is neither a relation this server knows nor a DE-9IM "
                        + "pattern. A DE-9IM pattern is nine characters of 0, 1, 2, T, F or *.";
                    return false;
                }

                pattern = relation;
                return true;
        }
    }

    /// <summary>Whether a string is shaped like a DE-9IM pattern.</summary>
    private static bool IsDe9im(string value)
    {
        if (value.Length != 9)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ('0' or '1' or '2' or 'T' or 'F' or '*'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a required distance, which may be negative.</summary>
    private static bool TryDistance(
        IFormCollection form, string field, out double distance, out string? error)
    {
        distance = 0;
        error = null;

        string raw = Field(form, field) ?? string.Empty;

        // ArcGIS's buffer takes a comma-separated list, one distance per ring.
        // We buffer at one distance and say so rather than silently using the
        // first of several a caller meant as several.
        if (raw.Contains(',', StringComparison.Ordinal))
        {
            error =
                $"'{field}' takes one distance here. ArcGIS accepts a list and returns a ring "
                + "per distance; this server buffers once, so send one value and repeat the "
                + "request for the others.";
            return false;
        }

        if (raw.Length == 0)
        {
            error = $"'{field}' is required, in the units of the spatial reference.";
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
            || double.IsNaN(distance) || double.IsInfinity(distance))
        {
            error = $"'{field}' must be a number.";
            return false;
        }

        return true;
    }

    /// <summary>One geometry under a name this operation chose.</summary>
    private static bool TryNamedGeometry(
        IFormCollection form, string field, int srid,
        out List<Geometry> geometries, out string? error)
    {
        geometries = [];
        error = null;

        string raw = Field(form, field) ?? string.Empty;

        if (raw.Length == 0)
        {
            error = $"'{field}' is required.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (!ArcGisGeometryReader.TryRead(
                    document.RootElement, srid, out Geometry? geometry, out error))
            {
                return false;
            }

            geometries = [geometry!];
            return true;
        }
        catch (JsonException e)
        {
            error = $"'{field}' is not valid JSON: {e.Message}";
            return false;
        }
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

                // <b>What the server can find out, since it cannot find out the
                // accuracy.</b> Whether a datum was crossed is readable from the
                // two references alone, and it is the difference between a
                // transformation that is exact by construction and one that can
                // be metres out with no error and no visual signature (D-32).
                datumShift = provenance.DatumShift,
                caution = provenance.Caution,
                note = "The transformation path was chosen by PROJ. Where several exist they "
                     + "differ by metres, and pinning one is not yet supported. When "
                     + "datumShift is true, read 'caution' before treating this as "
                     + "authoritative.",
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
        out string? error,
        string field = "geometries")
    {
        geometries = [];
        kind = GeometryKind.Point;
        error = null;

        // <b>The field name is a parameter because 'relation' has two lists.</b>
        // ArcGIS spells them geometries1 and geometries2, and every other
        // operation on this service spells its list 'geometries'.
        string? raw = Field(form, field);

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"'{field}' is required: {{\"geometryType\":\"esriGeometryPolygon\","
                  + $"\"{field}\":[ ... ]}}.";
            return false;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            error = $"'{field}' is not valid JSON: {e.Message}";
            return false;
        }

        // Both shapes are sent: a bare array, and the documented wrapper object.
        JsonElement array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty(field, out JsonElement inner)
                || root.TryGetProperty("geometries", out inner) ? inner : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
            error = $"'{field}' must be an array, or an object with a '{field}' array.";
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
