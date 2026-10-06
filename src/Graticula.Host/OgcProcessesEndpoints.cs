using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Formats;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Graticula.Host;

/// <summary>
/// OGC API – Processes – Part 1: Core 1.0 at <c>/ogc/processes/v1</c> — ADR-174: the geometry service's operations as
/// processes, and OGC's echo process for the conformance suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>The geometry service's engine, not a second one.</b> Each process is one <see cref="EngineOperation"/> run through
/// <see cref="IGeometryEngine"/> with the geometry service's own deadline, pre-flight and wait bounds, so a buffer asked
/// for here and one asked for at <c>GeometryServer/buffer</c> are the same computation under the same limits.
/// </para>
/// <para>
/// <b>Governed by the geometry service's sharing.</b> The processes are that service's operations under another
/// grammar; a caller the geometry service is not shared with sees no processes and can run none, and a stopped geometry
/// service runs nothing. Otherwise this face would be a door round the sharing the owner chose for it (2026-08-15).
/// </para>
/// <para>
/// <b>Geometries are GeoJSON, in the reference the request names.</b> A <c>crs</c> input names it — an OGC URI, a URN
/// or <c>EPSG:n</c> — and distances are in its units; without one it is CRS84, longitude first, and a distance is in
/// degrees. That is honest rather than convenient: a buffer of 100 metres is asked for in a metric reference.
/// </para>
/// </remarks>
internal static class OgcProcessesEndpoints
{
    private const string Root = "/ogc/processes/v1";
    private const string Rel = "http://www.opengis.net/def/rel/ogc/1.0/";
    private const string Exceptions = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/";
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

    private static readonly string[] ConformanceClasses =
    [
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/ogc-process-description",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Filtering, as every standard face is governed</b>: what a caller may not run is absent from every answer.
        app.MapGet(Root, LandingAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/conformance", ConformanceAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/api", ApiAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/processes", ProcessesAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/processes/{processId}", ProcessAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapPost(Root + "/processes/{processId}/execution", ExecuteAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/jobs", JobsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/jobs/{jobId}", JobAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/jobs/{jobId}/results", ResultsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapDelete(Root + "/jobs/{jobId}", DismissAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    public static (string Label, string Href) DirectoryLink() => ("OGC API Processes", Root);

    // ---------------------------------------------------------------- the processes

    /// <summary>A process input: its id, what it is, its JSON Schema, and how many it takes.</summary>
    private sealed record Parameter(string Id, string Title, string Description, JsonObject Schema, int MinOccurs = 1, int MaxOccurs = 1);

    /// <summary>A process: what it takes and gives, and the computation that does it.</summary>
    private sealed record ProcessDefinition(
        string Id, string Title, string Description, IReadOnlyList<Parameter> Inputs, IReadOnlyList<Parameter> Outputs,
        Func<RunContext, Task<Dictionary<string, JsonNode?>>> Run);

    /// <summary>What a run is given: its inputs, the engine and the geometry service's bounds.</summary>
    private sealed record RunContext(JsonObject Inputs, IGeometryEngine Engine, SystemService Service, CancellationToken Cancellation);

    /// <summary>A refusal of a run's inputs, said as a 400 the caller can act on.</summary>
    private sealed class InputException(string message) : Exception(message);

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly JsonObject GeometrySchema = Schema(
        """{"type":"object","format":"geojson-geometry","contentMediaType":"application/geo+json","required":["type"],"properties":{"type":{"type":"string","enum":["Point","MultiPoint","LineString","MultiLineString","Polygon","MultiPolygon"]}}}""");

    private static readonly JsonObject CrsSchema = Schema(
        $$"""{"type":"string","format":"uri","default":"{{Crs84}}"}""");

    private static Parameter CrsInput() => new("crs", "Reference",
        "The coordinate reference system the geometries and distances are in: an OGC CRS URI, a URN or EPSG:n. CRS84 when absent, "
        + "so a distance is then in degrees.", CrsSchema, MinOccurs: 0);

    private static Parameter GeometryOutput() => new("result", "Result", "The resulting geometry, in the request's reference.", GeometrySchema);

    private static readonly IReadOnlyList<ProcessDefinition> Catalogue =
    [
        // <b>OGC's echo process</b>, which returns its inputs as its outputs. The conformance suite runs every execution
        // mode against the process it is told to (echoprocessid), and a process whose outputs are its inputs is the only
        // one whose answer it can check without knowing what it computes. It touches no data and no engine.
        new("echo", "Echo", "Returns its inputs as its outputs — OGC's test process for the execution modes.",
            [
                new("stringInput", "String", "A string, returned as stringOutput.", Schema("""{"type":"string"}""")),
                new("doubleInput", "Number", "A number, returned as doubleOutput.", Schema("""{"type":"number"}"""), MinOccurs: 0),
                new("booleanInput", "Boolean", "A boolean, returned as booleanOutput.", Schema("""{"type":"boolean"}"""), MinOccurs: 0),
                new("boundingBoxInput", "Bounding box", "A bounding box, returned as boundingBoxOutput.", Schema(
                    """{"type":"object","required":["bbox"],"properties":{"bbox":{"type":"array","minItems":4,"maxItems":6,"items":{"type":"number"}},"crs":{"type":"string","format":"uri","default":""" + "\"" + Crs84 + "\"" + """}}}"""),
                    MinOccurs: 0),
                new("complexObjectInput", "Object", "Any JSON object, returned as complexObjectOutput.", Schema("""{"type":"object"}"""), MinOccurs: 0),
            ],
            [
                new("stringOutput", "String", "stringInput.", Schema("""{"type":"string"}""")),
                new("doubleOutput", "Number", "doubleInput.", Schema("""{"type":"number"}""")),
                new("booleanOutput", "Boolean", "booleanInput.", Schema("""{"type":"boolean"}""")),
                new("boundingBoxOutput", "Bounding box", "boundingBoxInput.", Schema(
                    """{"type":"object","required":["bbox"],"properties":{"bbox":{"type":"array","items":{"type":"number"}},"crs":{"type":"string","format":"uri"}}}""")),
                new("complexObjectOutput", "Object", "complexObjectInput.", Schema("""{"type":"object"}""")),
            ],
            run => Task.FromResult(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["stringOutput"] = run.Inputs["stringInput"]?.DeepClone(),
                ["doubleOutput"] = run.Inputs["doubleInput"]?.DeepClone(),
                ["booleanOutput"] = run.Inputs["booleanInput"]?.DeepClone(),
                ["boundingBoxOutput"] = run.Inputs["boundingBoxInput"]?.DeepClone(),
                ["complexObjectOutput"] = run.Inputs["complexObjectInput"]?.DeepClone(),
            })),

        new("buffer", "Buffer", "The area within a distance of a geometry, as the geometry service's buffer computes it.",
            [
                new("geometry", "Geometry", "The geometry to buffer, GeoJSON.", GeometrySchema),
                new("distance", "Distance", "How far, in the units of the reference.", Schema("""{"type":"number"}""")),
                CrsInput(),
            ],
            [GeometryOutput()],
            run => GeometryRunAsync(run, EngineOperation.Buffer, ["geometry"], [], distance: Number(run.Inputs, "distance"))),

        new("union", "Union", "The union of several geometries of one dimension.",
            [new("geometries", "Geometries", "The geometries, GeoJSON.", GeometrySchema, MaxOccurs: 1000), CrsInput()],
            [GeometryOutput()],
            run => GeometryRunAsync(run, EngineOperation.Union, ["geometries"], [])),

        new("intersection", "Intersection", "Each geometry intersected with one other.",
            [
                new("geometries", "Geometries", "The geometries to intersect, GeoJSON.", GeometrySchema, MaxOccurs: 1000),
                new("geometry", "With", "The geometry each is intersected with, GeoJSON.", GeometrySchema),
                CrsInput(),
            ],
            [new("result", "Results", "One geometry per input, in order; an empty intersection is null.", GeometrySchema, MaxOccurs: 1000)],
            run => GeometryRunAsync(run, EngineOperation.Intersect, ["geometries"], ["geometry"], many: true)),

        new("difference", "Difference", "Each geometry less one other.",
            [
                new("geometries", "Geometries", "The geometries to subtract from, GeoJSON.", GeometrySchema, MaxOccurs: 1000),
                new("geometry", "Less", "The geometry subtracted from each, GeoJSON.", GeometrySchema),
                CrsInput(),
            ],
            [new("result", "Results", "One geometry per input, in order; nothing left is null.", GeometrySchema, MaxOccurs: 1000)],
            run => GeometryRunAsync(run, EngineOperation.Difference, ["geometries"], ["geometry"], many: true)),

        new("simplify", "Simplify", "A geometry made topologically simple — valid rings, no self-intersection — as ArcGIS's simplify does.",
            [new("geometry", "Geometry", "The geometry, GeoJSON.", GeometrySchema), CrsInput()],
            [GeometryOutput()],
            run => GeometryRunAsync(run, EngineOperation.Simplify, ["geometry"], [])),

        new("generalize", "Generalize", "A geometry with vertices removed while it stays within a deviation of the original.",
            [
                new("geometry", "Geometry", "The geometry, GeoJSON.", GeometrySchema),
                new("maxDeviation", "Deviation", "The largest distance the result may stray, in the units of the reference.",
                    Schema("""{"type":"number","minimum":0}""")),
                CrsInput(),
            ],
            [GeometryOutput()],
            run => GeometryRunAsync(run, EngineOperation.Generalize, ["geometry"], [], distance: Number(run.Inputs, "maxDeviation"))),

        new("distance", "Distance", "The shortest distance between two geometries, in the units of the reference.",
            [
                new("geometry1", "First", "A geometry, GeoJSON.", GeometrySchema),
                new("geometry2", "Second", "A geometry, GeoJSON.", GeometrySchema),
                CrsInput(),
            ],
            [new("distance", "Distance", "The distance, in the units of the reference.", Schema("""{"type":"number"}"""))],
            run => DistanceRunAsync(run)),
    ];

    private static ProcessDefinition? Find(string id) => Catalogue.FirstOrDefault(p => p.Id == id);

    private static double Number(JsonObject inputs, string id) =>
        inputs[id] is JsonValue value && value.TryGetValue(out double number)
            ? number
            : inputs[id] is JsonObject { } qualified && qualified["value"] is JsonValue inner && inner.TryGetValue(out double wrapped)
                ? wrapped
                : throw new InputException($"'{id}' is a number, and the request gave none.");

    /// <summary>The reference a run is in: its <c>crs</c> input, or CRS84.</summary>
    private static int SridOf(JsonObject inputs)
    {
        string? named = inputs["crs"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        if (string.IsNullOrWhiteSpace(named) || named is Crs84 or "urn:ogc:def:crs:OGC:1.3:CRS84" or "CRS:84" or "OGC:CRS84")
        {
            return 4326;
        }

        string digits = named[(named.LastIndexOfAny(['/', ':']) + 1)..];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int srid) && srid > 0
            ? srid
            : throw new InputException($"'{named}' is not a reference this server reads. Name one as {Crs84} or http://www.opengis.net/def/crs/EPSG/0/<code>.");
    }

    /// <summary>One input's geometries — a single GeoJSON geometry or an array of them.</summary>
    private static List<Geometry> GeometriesOf(JsonObject inputs, string id)
    {
        JsonNode? given = inputs[id] ?? throw new InputException($"'{id}' is required.");
        List<Geometry> read = [];

        foreach (JsonNode? one in given is JsonArray many ? [.. many] : (JsonNode?[])[given])
        {
            JsonNode? geometry = one is JsonObject { } wrapper && wrapper["value"] is JsonObject inner ? inner : one;

            if (geometry is null)
            {
                throw new InputException($"'{id}' is not a GeoJSON geometry.");
            }

            if (!GeoJsonGeometry.TryRead(JsonSerializer.SerializeToElement(geometry), read.Count, keepZ: false,
                    geographic: SridOf(inputs) == 4326, out Geometry? parsed, out string? error))
            {
                throw new InputException($"'{id}' is not a GeoJSON geometry: {error}");
            }

            read.Add(parsed!);
        }

        return read;
    }

    private static async Task<Dictionary<string, JsonNode?>> GeometryRunAsync(
        RunContext run, EngineOperation operation, string[] left, string[] right, double distance = 0, bool many = false)
    {
        int srid = SridOf(run.Inputs);
        EngineRequest request = new(operation, [.. left.SelectMany(i => GeometriesOf(run.Inputs, i))], [.. right.SelectMany(i => GeometriesOf(run.Inputs, i))], srid)
        {
            Distance = distance,
            UnionResults = operation == EngineOperation.Buffer,
            Deadline = run.Service.DeadlineSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            PreflightPairs = run.Service.PreflightPairs,
            Wait = run.Service.WaitSeconds is { } waiting ? TimeSpan.FromSeconds(waiting) : null,
        };

        EngineResult result = await run.Engine.ComputeAsync(request, run.Cancellation).ConfigureAwait(false);

        if (result.Refusal is not EngineRefusal.None)
        {
            throw result.Refusal is EngineRefusal.TooLarge or EngineRefusal.Invalid
                ? new InputException(result.Message ?? result.Refusal.ToString())
                : new InvalidOperationException(result.Message ?? result.Refusal.ToString());
        }

        JsonNode? Json(Geometry? geometry) => geometry is null || geometry.IsEmpty ? null : GeoJson(geometry);

        return new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["result"] = many
                ? new JsonArray([.. result.Geometries.Select(Json)])
                : Json(result.Geometries.Count > 0 ? result.Geometries[0] : null),
        };
    }

    private static async Task<Dictionary<string, JsonNode?>> DistanceRunAsync(RunContext run)
    {
        int srid = SridOf(run.Inputs);
        EngineResult result = await run.Engine.ComputeAsync(
                new EngineRequest(EngineOperation.Distance, GeometriesOf(run.Inputs, "geometry1"), GeometriesOf(run.Inputs, "geometry2"), srid),
                run.Cancellation)
            .ConfigureAwait(false);

        return result.Refusal is EngineRefusal.None && result.Scalar is { } scalar
            ? new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["distance"] = JsonValue.Create(scalar) }
            : throw new InputException(result.Message ?? "The distance could not be measured.");
    }

    private static JsonNode GeoJson(Geometry geometry)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer))
        {
            GeoJsonWriter.WriteGeometry(json, geometry);
        }

        return JsonNode.Parse(buffer.ToArray())!;
    }

    // ---------------------------------------------------------------- jobs

    /// <summary>A job: one execution, synchronous or not, kept so its status and results can be asked for.</summary>
    private sealed class Job
    {
        public required string Id { get; init; }

        public required string ProcessId { get; init; }

        public required string? Owner { get; init; }

        public required DateTimeOffset Created { get; init; }

        public required bool Raw { get; init; }

        public required IReadOnlyList<string> Outputs { get; init; }

        public string Status { get; set; } = "accepted";

        public DateTimeOffset? Started { get; set; }

        public DateTimeOffset? Finished { get; set; }

        public DateTimeOffset Updated { get; set; }

        public string? Message { get; set; }

        public Dictionary<string, JsonNode?>? Results { get; set; }
    }

    /// <summary>The jobs this server remembers, newest last.</summary>
    /// <remarks>
    /// <b>Bounded: a thousand jobs and an hour each.</b> A job is an answer kept so that it can be fetched again, not a
    /// record; one that nobody has fetched in an hour is not going to be, and a server that kept every geometry it ever
    /// buffered would run out of memory for a feature nobody asked it to keep. The oldest finished job leaves first.
    /// Held in memory and per process, so a restart forgets them and a second server behind a balancer does not know
    /// them — which ADR-174 records as the shape of this, not an accident of it.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, Job> JobStore = new(StringComparer.Ordinal);

    private const int JobCapacity = 1000;

    private static readonly TimeSpan JobLifetime = TimeSpan.FromHours(1);

    private static void Keep(Job job)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (Job old in JobStore.Values.Where(j => now - j.Updated > JobLifetime).ToList())
        {
            JobStore.TryRemove(old.Id, out _);
        }

        while (JobStore.Count >= JobCapacity
            && JobStore.Values.OrderBy(j => j.Finished is null).ThenBy(j => j.Updated).FirstOrDefault() is { } oldest)
        {
            JobStore.TryRemove(oldest.Id, out _);
        }

        JobStore[job.Id] = job;
    }

    /// <summary>A caller's own job: another caller's is not found, as a job id is not a capability to share.</summary>
    private static Job? OwnJob(HttpContext context, string jobId) =>
        JobStore.TryGetValue(jobId, out Job? job) && job.Owner == OwnerOf(context) ? job : null;

    private static string? OwnerOf(HttpContext context) =>
        context.Features.Get<RequestPrincipal>()?.Principal is { } principal && principal.Kind != PrincipalKind.Anonymous
            ? principal.Name
            : null;

    // ---------------------------------------------------------------- handlers

    private static string Base(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{Root}";

    /// <summary>The geometry service, if this caller may run its operations; null otherwise.</summary>
    private static async Task<SystemService?> GeometryServiceAsync(HttpContext context)
    {
        PostgresSystemServices services = context.RequestServices.GetRequiredService<PostgresSystemServices>();
        SystemService? service = await services.FindAsync(GeometryServerEndpoints.ServiceName, context.RequestAborted).ConfigureAwait(false);
        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        return service is { Status: not ServiceStatus.Stopped } found && current is not null
            && LayerAccess.Evaluate(found.Sharing, null, current.Principal, current.Authorization).IsAllowed()
            ? found
            : null;
    }

    private static Task LandingAsync(HttpContext context)
    {
        string root = Base(context);
        return JsonAsync(context, new JsonObject
        {
            ["title"] = "Graticula — OGC API Processes",
            ["description"] = "The geometry service's operations as OGC API Processes, and OGC's echo process.",
            ["links"] = Links(
                (root, "self", "application/json", "This document"),
                (root + "/api", "service-desc", "application/vnd.oai.openapi+json;version=3.0", "The API definition"),
                (root + "/conformance", Rel + "conformance", "application/json", "Conformance classes"),
                (root + "/processes", Rel + "processes", "application/json", "The processes"),
                (root + "/jobs", Rel + "job-list", "application/json", "Your jobs")),
        });
    }

    private static Task ConformanceAsync(HttpContext context) =>
        JsonAsync(context, new JsonObject { ["conformsTo"] = new JsonArray([.. ConformanceClasses.Select(c => (JsonNode)c)]) });

    private static async Task ProcessesAsync(HttpContext context)
    {
        string root = Base(context);

        if (await PageAsync(context).ConfigureAwait(false) is not { } page)
        {
            return;
        }

        bool allowed = await GeometryServiceAsync(context).ConfigureAwait(false) is not null;
        ProcessDefinition[] all = allowed ? [.. Catalogue] : [];

        await JsonAsync(context, new JsonObject
        {
            ["processes"] = new JsonArray([.. all.Skip(page.Offset).Take(page.Limit).Select(p => (JsonNode)Summary(root, p))]),
            ["links"] = PageLinks(root + "/processes", "The processes", page, all.Length),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>limit</c> and <c>offset</c> as the process and job lists take them — 1.0's <c>/req/core/pl-limit-definition</c>
    /// and <c>/req/job-list/limit-definition</c>: 1 to 10,000, 10 when absent — or null when a refusal is written.
    /// </summary>
    private static async Task<(int Limit, int Offset)?> PageAsync(HttpContext context)
    {
        string? limitText = context.Request.Query["limit"].FirstOrDefault();
        string? offsetText = context.Request.Query["offset"].FirstOrDefault();
        int limit = 10, offset = 0;

        if ((limitText is not null
                && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 10_000))
            || (offsetText is not null && !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
        {
            await ProblemAsync(context, 400, "invalid-parameter", "limit is a whole number from 1 to 10000, and offset a whole number from 0.")
                .ConfigureAwait(false);
            return null;
        }

        return (limit, offset);
    }

    private static JsonArray PageLinks(string self, string title, (int Limit, int Offset) page, int total)
    {
        JsonArray links = Links((self, "self", "application/json", title));

        if (page.Offset + page.Limit < total)
        {
            links.Add(new JsonObject
            {
                ["href"] = string.Create(CultureInfo.InvariantCulture, $"{self}?limit={page.Limit}&offset={page.Offset + page.Limit}"),
                ["rel"] = "next",
                ["type"] = "application/json",
                ["title"] = "The next page",
            });
        }

        return links;
    }

    private static JsonObject Summary(string root, ProcessDefinition process) => new()
    {
        ["id"] = process.Id,
        ["title"] = process.Title,
        ["description"] = process.Description,
        ["version"] = "1.0.0",
        ["jobControlOptions"] = new JsonArray("sync-execute", "async-execute", "dismiss"),
        ["outputTransmission"] = new JsonArray("value"),
        ["links"] = Links(
            ($"{root}/processes/{process.Id}", "self", "application/json", process.Title),
            ($"{root}/processes/{process.Id}", Rel + "process-desc", "application/json", process.Title)),
    };

    private static async Task ProcessAsync(HttpContext context, string processId)
    {
        string root = Base(context);

        if (Find(processId) is not { } process || await GeometryServiceAsync(context).ConfigureAwait(false) is null)
        {
            await ProblemAsync(context, 404, "no-such-process", $"There is no process '{processId}' you may run.").ConfigureAwait(false);
            return;
        }

        JsonObject description = Summary(root, process);
        description["inputs"] = Parameters(process.Inputs, withOccurs: true);
        description["outputs"] = Parameters(process.Outputs, withOccurs: false);
        ((JsonArray)description["links"]!).Add(new JsonObject
        {
            ["href"] = $"{root}/processes/{process.Id}/execution",
            ["rel"] = Rel + "execute",
            ["type"] = "application/json",
            ["title"] = "Execute",
        });

        await JsonAsync(context, description).ConfigureAwait(false);
    }

    private static JsonObject Parameters(IReadOnlyList<Parameter> parameters, bool withOccurs)
    {
        JsonObject described = [];

        foreach (Parameter parameter in parameters)
        {
            JsonObject one = new()
            {
                ["title"] = parameter.Title,
                ["description"] = parameter.Description,
                ["schema"] = parameter.Schema.DeepClone(),
            };

            if (withOccurs)
            {
                one["minOccurs"] = parameter.MinOccurs;
                one["maxOccurs"] = parameter.MaxOccurs == 1 ? 1 : parameter.MaxOccurs;
            }

            described[parameter.Id] = one;
        }

        return described;
    }

    /// <summary>
    /// Runs a process: synchronously unless the caller prefers otherwise and says so — RFC 7240's
    /// <c>Prefer: respond-async</c> — which is 1.0's way of asking for a job.
    /// </summary>
    private static async Task ExecuteAsync(HttpContext context, string processId, IGeometryEngine engine, IHostApplicationLifetime lifetime)
    {
        if (Find(processId) is not { } process || await GeometryServiceAsync(context).ConfigureAwait(false) is not { } service)
        {
            await ProblemAsync(context, 404, "no-such-process", $"There is no process '{processId}' you may run.").ConfigureAwait(false);
            return;
        }

        JsonObject request;

        try
        {
            request = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException e)
        {
            await ProblemAsync(context, 400, "invalid-request", $"The execute request is not a JSON object: {e.Message}").ConfigureAwait(false);
            return;
        }

        JsonObject inputs = request["inputs"] as JsonObject ?? [];

        foreach (Parameter required in process.Inputs.Where(p => p.MinOccurs > 0))
        {
            if (inputs[required.Id] is null)
            {
                await ProblemAsync(context, 400, "invalid-request", $"'{required.Id}' is required by '{process.Id}'.").ConfigureAwait(false);
                return;
            }
        }

        if (inputs.Select(i => i.Key).FirstOrDefault(k => process.Inputs.All(p => p.Id != k)) is { } unknown)
        {
            await ProblemAsync(context, 400, "invalid-request", $"'{unknown}' is not an input of '{process.Id}'.").ConfigureAwait(false);
            return;
        }

        List<string> outputs = request["outputs"] is JsonObject { Count: > 0 } asked
            ? [.. asked.Select(o => o.Key)]
            : [.. process.Outputs.Select(o => o.Id)];

        if (outputs.FirstOrDefault(o => process.Outputs.All(p => p.Id != o)) is { } strange)
        {
            await ProblemAsync(context, 400, "invalid-request", $"'{strange}' is not an output of '{process.Id}'.").ConfigureAwait(false);
            return;
        }

        if (request["outputs"] is JsonObject chosen
            && chosen.Select(o => o.Value?["transmissionMode"]?.GetValue<string>()).FirstOrDefault(m => m is not null and not "value") is { } mode)
        {
            await ProblemAsync(context, 400, "invalid-request",
                $"'{mode}' is not a transmission mode this server offers; every output is sent by value.").ConfigureAwait(false);
            return;
        }

        // 1.0's default response is raw: the output itself, or the outputs as the parts of a multipart answer.
        bool raw = !string.Equals(request["response"]?.GetValue<string>(), "document", StringComparison.Ordinal);
        Job job = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ProcessId = process.Id,
            Owner = OwnerOf(context),
            Created = DateTimeOffset.UtcNow,
            Updated = DateTimeOffset.UtcNow,
            Raw = raw,
            Outputs = outputs,
        };

        bool async = context.Request.Headers["Prefer"].Any(p => p is not null && p.Contains("respond-async", StringComparison.OrdinalIgnoreCase));

        if (async)
        {
            Keep(job);
            CancellationToken stopping = lifetime.ApplicationStopping;
            _ = Task.Run(() => RunAsync(job, process, new RunContext(inputs, engine, service, stopping)), CancellationToken.None);

            context.Response.StatusCode = 201;
            context.Response.Headers.Location = $"{Base(context)}/jobs/{job.Id}";
            context.Response.Headers["Preference-Applied"] = "respond-async";
            await JsonAsync(context, StatusInfo(context, job), status: 201).ConfigureAwait(false);
            return;
        }

        await RunAsync(job, process, new RunContext(inputs, engine, service, context.RequestAborted)).ConfigureAwait(false);
        Keep(job);

        if (job.Status != "successful")
        {
            await ProblemAsync(context, job.Message?.StartsWith("400 ", StringComparison.Ordinal) == true ? 400 : 500,
                "invalid-request", job.Message?[4..] ?? "The process failed.").ConfigureAwait(false);
            return;
        }

        context.Response.Headers.Location = $"{Base(context)}/jobs/{job.Id}";
        await WriteResultsAsync(context, job).ConfigureAwait(false);
    }

    private static async Task RunAsync(Job job, ProcessDefinition process, RunContext run)
    {
        job.Status = "running";
        job.Started = job.Updated = DateTimeOffset.UtcNow;

        try
        {
            Dictionary<string, JsonNode?> results = await process.Run(run).ConfigureAwait(false);
            job.Results = job.Outputs.ToDictionary(o => o, o => results.GetValueOrDefault(o), StringComparer.Ordinal);
            job.Status = "successful";
            job.Message = "Done.";
        }
        catch (InputException e)
        {
            job.Status = "failed";
            job.Message = "400 " + e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            job.Status = "failed";
            job.Message = "500 " + e.Message;
        }
        catch (OperationCanceledException)
        {
            job.Status = "dismissed";
            job.Message = "500 The server stopped before the job finished.";
        }

        job.Finished = job.Updated = DateTimeOffset.UtcNow;
    }

    private static JsonObject StatusInfo(HttpContext context, Job job)
    {
        string root = Base(context);
        JsonArray links = Links(($"{root}/jobs/{job.Id}", "self", "application/json", "This job"));

        if (job.Status == "successful")
        {
            links.Add(new JsonObject
            {
                ["href"] = $"{root}/jobs/{job.Id}/results",
                ["rel"] = Rel + "results",
                ["type"] = "application/json",
                ["title"] = "Results",
            });
        }

        JsonObject info = new()
        {
            ["processID"] = job.ProcessId,
            ["type"] = "process",
            ["jobID"] = job.Id,
            ["status"] = job.Status,
            ["message"] = job.Message is { Length: > 4 } said && char.IsDigit(said[0]) ? said[4..] : job.Message,
            ["created"] = job.Created.ToString("O", CultureInfo.InvariantCulture),
            ["updated"] = job.Updated.ToString("O", CultureInfo.InvariantCulture),
            ["progress"] = job.Status is "successful" or "failed" ? 100 : job.Status == "running" ? 50 : 0,
            ["links"] = links,
        };

        if (job.Started is { } started)
        {
            info["started"] = started.ToString("O", CultureInfo.InvariantCulture);
        }

        if (job.Finished is { } finished)
        {
            info["finished"] = finished.ToString("O", CultureInfo.InvariantCulture);
        }

        return info;
    }

    private static async Task JobsAsync(HttpContext context)
    {
        if (await PageAsync(context).ConfigureAwait(false) is not { } page)
        {
            return;
        }

        string? owner = OwnerOf(context);
        Job[] own = [.. JobStore.Values.Where(j => j.Owner == owner).OrderBy(j => j.Created)];

        await JsonAsync(context, new JsonObject
        {
            ["jobs"] = new JsonArray([.. own.Skip(page.Offset).Take(page.Limit).Select(j => (JsonNode)StatusInfo(context, j))]),
            ["links"] = PageLinks(Base(context) + "/jobs", "Your jobs", page, own.Length),
        }).ConfigureAwait(false);
    }

    private static async Task JobAsync(HttpContext context, string jobId)
    {
        if (OwnJob(context, jobId) is not { } job)
        {
            await ProblemAsync(context, 404, "no-such-job", $"There is no job '{jobId}' of yours.").ConfigureAwait(false);
            return;
        }

        await JsonAsync(context, StatusInfo(context, job)).ConfigureAwait(false);
    }

    private static async Task ResultsAsync(HttpContext context, string jobId)
    {
        if (OwnJob(context, jobId) is not { } job)
        {
            await ProblemAsync(context, 404, "no-such-job", $"There is no job '{jobId}' of yours.").ConfigureAwait(false);
            return;
        }

        switch (job.Status)
        {
            case "successful":
                await WriteResultsAsync(context, job).ConfigureAwait(false);
                return;
            case "failed":
                await ProblemAsync(context, job.Message?.StartsWith("400 ", StringComparison.Ordinal) == true ? 400 : 500,
                    "job-results-failed", job.Message?[4..] ?? "The job failed.").ConfigureAwait(false);
                return;
            default:
                await ProblemAsync(context, 404, "result-not-ready", $"Job '{jobId}' is {job.Status}; its results are not ready.").ConfigureAwait(false);
                return;
        }
    }

    private static async Task DismissAsync(HttpContext context, string jobId)
    {
        if (OwnJob(context, jobId) is not { } job)
        {
            await ProblemAsync(context, 404, "no-such-job", $"There is no job '{jobId}' of yours.").ConfigureAwait(false);
            return;
        }

        // A run already under way is not interrupted — it is a geometry computation bounded by the service's deadline —
        // but its answer is dropped and the job is forgotten.
        job.Status = "dismissed";
        job.Message = "Dismissed.";
        job.Results = null;
        job.Updated = DateTimeOffset.UtcNow;
        JsonObject info = StatusInfo(context, job);
        JobStore.TryRemove(job.Id, out _);
        await JsonAsync(context, info).ConfigureAwait(false);
    }

    /// <summary>
    /// The results as 1.0 gives them: a document mapping each output to its value; or, raw, the one output itself, or
    /// several as the parts of a <c>multipart/related</c> answer, each named by its Content-ID.
    /// </summary>
    private static async Task WriteResultsAsync(HttpContext context, Job job)
    {
        Dictionary<string, JsonNode?> results = job.Results ?? [];

        if (!job.Raw)
        {
            JsonObject document = [];

            foreach ((string id, JsonNode? value) in results)
            {
                document[id] = value?.DeepClone();
            }

            await JsonAsync(context, document).ConfigureAwait(false);
            return;
        }

        if (results.Count == 1)
        {
            (string media, byte[] body) = RawOf(job.ProcessId, results.Keys.First(), results.Values.First());
            context.Response.ContentType = media;
            await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string boundary = "graticula-" + job.Id;
        using MemoryStream multipart = new();

        foreach ((string id, JsonNode? value) in results)
        {
            (string media, byte[] body) = RawOf(job.ProcessId, id, value);
            byte[] head = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: {media}\r\nContent-ID: <{id}>\r\n\r\n");
            multipart.Write(head);
            multipart.Write(body);
            multipart.Write("\r\n"u8);
        }

        multipart.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        context.Response.ContentType = $"multipart/related; boundary={boundary}";
        await context.Response.Body.WriteAsync(multipart.ToArray(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>One output as raw bytes: a string as text, a geometry as GeoJSON, anything else as JSON.</summary>
    private static (string Media, byte[] Body) RawOf(string processId, string outputId, JsonNode? value)
    {
        if (value is JsonValue text && text.TryGetValue(out string? s))
        {
            return ("text/plain; charset=utf-8", Encoding.UTF8.GetBytes(s));
        }

        bool geometry = Find(processId)?.Outputs.FirstOrDefault(o => o.Id == outputId)?.Schema["format"]?.GetValue<string>() == "geojson-geometry";
        return (geometry ? "application/geo+json" : "application/json", Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null"));
    }

    // ---------------------------------------------------------------- the API definition

    private static Task ApiAsync(HttpContext context)
    {
        string root = Base(context);
        static JsonObject Op(string summary, string id, JsonObject? body = null)
        {
            JsonObject op = new()
            {
                ["summary"] = summary,
                ["operationId"] = id,
                ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "Success" }, ["default"] = new JsonObject { ["description"] = "A problem (RFC 7807)" } },
            };

            if (body is not null)
            {
                op["requestBody"] = body;
            }

            return op;
        }

        JsonObject pathParam(string name) => new() { ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } };

        JsonObject executeBody = new()
        {
            ["required"] = true,
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["inputs"] = new JsonObject { ["type"] = "object" },
                            ["outputs"] = new JsonObject { ["type"] = "object" },
                            ["response"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("raw", "document"), ["default"] = "raw" },
                        },
                    },
                },
            },
        };

        JsonObject withParam(JsonObject op, string name)
        {
            op["parameters"] = new JsonArray(pathParam(name));
            return op;
        }

        return JsonAsync(context, new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "Graticula — OGC API Processes", ["version"] = "1.0.0" },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = root }),
            ["paths"] = new JsonObject
            {
                ["/"] = new JsonObject { ["get"] = Op("Landing page", "getLandingPage") },
                ["/conformance"] = new JsonObject { ["get"] = Op("Conformance classes", "getConformanceClasses") },
                ["/api"] = new JsonObject { ["get"] = Op("This definition", "getAPI") },
                ["/processes"] = new JsonObject { ["get"] = Op("The processes", "getProcesses") },
                ["/processes/{processId}"] = new JsonObject { ["get"] = withParam(Op("A process's description", "getProcessDescription"), "processId") },
                ["/processes/{processId}/execution"] = new JsonObject { ["post"] = withParam(Op("Execute a process", "execute", executeBody), "processId") },
                ["/jobs"] = new JsonObject { ["get"] = Op("Your jobs", "getJobs") },
                ["/jobs/{jobId}"] = new JsonObject
                {
                    ["get"] = withParam(Op("A job's status", "getStatus"), "jobId"),
                    ["delete"] = withParam(Op("Dismiss a job", "dismiss"), "jobId"),
                },
                ["/jobs/{jobId}/results"] = new JsonObject { ["get"] = withParam(Op("A job's results", "getResult"), "jobId") },
            },
        }, media: "application/vnd.oai.openapi+json;version=3.0");
    }

    // ---------------------------------------------------------------- answers

    private static JsonArray Links(params (string Href, string Rel, string Type, string Title)[] links) =>
        new([.. links.Select(l => (JsonNode)new JsonObject { ["href"] = l.Href, ["rel"] = l.Rel, ["type"] = l.Type, ["title"] = l.Title })]);

    private static async Task JsonAsync(HttpContext context, JsonNode document, int status = 200, string media = "application/json")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = media;
        await context.Response.WriteAsync(document.ToJsonString(Indented), context.RequestAborted).ConfigureAwait(false);
    }

    private static Task ProblemAsync(HttpContext context, int status, string type, string detail) =>
        JsonAsync(context, new JsonObject
        {
            ["type"] = Exceptions + type,
            ["title"] = type,
            ["status"] = status,
            ["detail"] = detail,
        }, status, "application/problem+json");
}
