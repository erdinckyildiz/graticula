using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The offline packaging service, <c>Utilities/OfflinePackaging/GPServer</c> — ADR-288 §11a, D-404: <c>CreateMapArea</c>,
/// <c>SetupMapArea</c> and <c>RefreshMapAreaPackage</c>, the tasks ArcGIS Pro's and Map Viewer's preplanned areas are made
/// through, over the code <c>POST /admin/maps/{id}/areas</c> runs (<see cref="MapAreaPackaging"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is read; the content of the values is not.</b> The public ArcGIS REST reference does not document these tasks
/// (every address tried on 2026-11-15 answered <i>Page not found</i>; ADR-288 §11a item 10). What this service answers for the
/// service and each task - names, <c>GPString</c> types, directions, required or optional, <c>areaType</c>'s choices and default,
/// derived outputs, and asynchronous execution - was read on 2026-11-15 from the task descriptions of an Enterprise 12.1
/// portal. <b>What a value holds is INFERRED</b>: the JSON inside <c>extent</c>, <c>area</c>, <c>bookmark</c>,
/// <c>tileServices</c>, <c>featureServices</c>, <c>mapLayersToIgnore</c> and <c>packages</c>, and what <c>outputName</c> means.
/// </para>
/// <para>
/// <b>Mapped onto the same code as the Graticula route, with one difference ArcGIS makes and this server does not.</b>
/// Enterprise splits making an area (<c>CreateMapArea</c>, an item) from writing its packages (<c>SetupMapArea</c>). Here an
/// area is never without its packages (ADR-288 §11a item 1): <c>CreateMapArea</c> makes the area and packages it, and
/// <c>SetupMapArea</c> on an area that exists writes what is missing and refreshes the rest, which is what
/// <see cref="MapAreaPackaging.RefreshAsync(PackagingCaller, MapArea, CancellationToken)"/> does. <c>RefreshMapAreaPackage</c>
/// refreshes the area a package belongs to (all of its packages: a package alone has no refresh here).
/// </para>
/// <para>
/// <b>Asynchronous in name, synchronous in fact</b>, as the print service is: the tasks declare
/// <c>esriExecutionTypeAsynchronous</c> because ArcGIS's clients submit and poll, <c>execute</c> answers at once too, and
/// <c>submitJob</c> does the work before it answers and keeps the finished job for ten minutes. A basemap's tile package is
/// the one thing that is a job (D-427); it is reported on the package item's status.
/// </para>
/// <para>
/// <b>The caller is the map's owner or an administrator</b>, exactly as for the route; an anonymous caller is asked for a
/// token. The service lists itself and its tasks to everyone and holds no data.
/// </para>
/// </remarks>
internal static class OfflinePackagingEndpoints
{
    /// <summary>Where the service is, as ArcGIS Enterprise's packaging service is (research note §5a).</summary>
    public const string ServicePath = "/rest/services/Utilities/OfflinePackaging/GPServer";

    /// <summary>Makes an area of a web map and packages it.</summary>
    public const string CreateTask = "CreateMapArea";

    /// <summary>Writes the packages an area is missing and refreshes the rest.</summary>
    public const string SetupTask = "SetupMapArea";

    /// <summary>Writes an area's packages again.</summary>
    public const string RefreshTask = "RefreshMapAreaPackage";

    /// <summary>The most finished jobs remembered for a client to poll.</summary>
    public const int MaximumJobs = 1024;

    private const string Kind = "packagingjobs";

    private static readonly string[] Tasks = [CreateTask, SetupTask, RefreshTask];

    private static readonly string[] AreaTypes = ["BOOKMARK", "ENVELOPE", "POLYGON"];

    private sealed record PackagingJob(string Status, IReadOnlyList<Output> Results, IReadOnlyList<string> Messages, DateTimeOffset Until);

    /// <summary>One output parameter of a task.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="Type">Its GP data type.</param>
    /// <param name="Value">Its value, as text.</param>
    internal sealed record Output(string Name, string Type, string Value);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<string, PackagingJob> Jobs = new(StringComparer.Ordinal);

    /// <summary>Maps the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        // As the print service's: the service and its tasks describe themselves and hold no data; a job is read by its id, a
        // capability; running a task asks for a signed-in caller who may change the map, and checks it itself.
        app.MapMethods(ServicePath, ["GET", "POST"], ServiceAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}", ["GET", "POST"], TaskAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}/execute", ["GET", "POST"], ExecuteAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/submitJob", ["GET", "POST"], SubmitAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}", ["GET", "POST"], JobAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}/results/{param}", ["GET", "POST"], ResultAsync)
            .Governed(SharingGovernedExtensions.Public);
    }

    /// <summary>The address <c>portals/self</c> names in <c>helperServices.packaging</c> (INFERRED: the key).</summary>
    /// <param name="origin">The organisation's origin.</param>
    /// <returns>The service's address.</returns>
    public static string Url(string origin) => $"{origin}{ServicePath}";

    // ------------------------------------------------------------------ the service, as it describes itself

    private static Task ServiceAsync(HttpContext context) =>
        AnalysisEndpoints.WriteAsync(context, new
        {
            currentVersion = 11.3,
            serviceDescription = "Makes preplanned map areas of web maps and keeps their packages: the mobile geodatabases and tile packages "
                + "an offline map takes (ADR-288 section 11a). The tasks and parameters are INFERRED, not read from documentation.",
            tasks = Tasks,
            executionType = "esriExecutionTypeAsynchronous",
            resultMapServerName = string.Empty,
            maximumRecords = 1000,
            capabilities = string.Empty,
        });

    private static async Task TaskAsync(HttpContext context, string task)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new
        {
            name,
            displayName = name,
            description = name switch
            {
                CreateTask => "Makes an area of a web map, by extent, bookmark or geometry, and packages it.",
                SetupTask => "Writes the packages a map area is missing and refreshes the rest.",
                _ => "Writes a map area's packages again from the data as it is now.",
            },
            category = string.Empty,
            helpUrl = string.Empty,
            executionType = "esriExecutionTypeAsynchronous",
            parameters = ParametersOf(name),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The tasks' parameters exactly as the task descriptions of an Enterprise 12.1 portal list them (read 2026-11-15): names, all
    /// <c>GPString</c>, directions, required or optional, <c>areaType</c>'s choices and default, and the derived outputs. What the
    /// <i>values</i> look like was not read and stays INFERRED.
    /// </summary>
    private static object[] ParametersOf(string task) => task switch
    {
        CreateTask =>
        [
            Parameter("mapItemId", "The web map's item id.", output: false, required: true),
            Parameter("bookmark", "The name of a bookmark of the web map, whose extent is the area (INFERRED content).", output: false, required: false),
            Parameter("extent", "An envelope {xmin, ymin, xmax, ymax, spatialReference} (INFERRED content).", output: false, required: false),
            Parameter("areaType", "How the area is given.", output: false, required: false, "BOOKMARK", AreaTypes),
            Parameter("area", "A polygon {rings, spatialReference}, whose envelope is the area (INFERRED content).", output: false, required: false),
            Parameter("outputName", "What the area is called (INFERRED).", output: false, required: false),
            Parameter("mapAreaItemId", "The new Map Area item.", output: true, required: false),
        ],
        SetupTask =>
        [
            Parameter("mapAreaItemId", "The Map Area item to write the missing packages of.", output: false, required: true),
            Parameter("mapLayersToIgnore", "Accepted and not used by this server (INFERRED content).", output: false, required: false),
            Parameter("tileServices", "Accepted and not used by this server (INFERRED content).", output: false, required: false),
            Parameter("featureServices", "Accepted and not used by this server (INFERRED content).", output: false, required: false),
            Parameter("packages", "The area's packages and their statuses, as JSON (INFERRED content).", output: true, required: false),
        ],
        _ =>
        [
            Parameter("packages", "The packages to write again: the JSON SetupMapArea answered, or package item ids (INFERRED content).", output: false, required: true),
            Parameter("result", "The area's packages and their statuses, as JSON (INFERRED content).", output: true, required: false),
        ],
    };

    /// <summary>The names of a task's input or output parameters, in order.</summary>
    /// <param name="task">The task.</param>
    /// <param name="output">Outputs when true, inputs otherwise.</param>
    /// <returns>The names.</returns>
    internal static IEnumerable<string> ParameterNames(string task, bool output) =>
        ParametersOf(task)
            .Select(p => JsonSerializer.SerializeToNode(p) as JsonObject)
            .Where(p => p!["direction"]!.GetValue<string>().EndsWith(output ? "Output" : "Input", StringComparison.Ordinal))
            .Select(p => p!["name"]!.GetValue<string>());

    private static object Parameter(
        string name, string description, bool output, bool required,
        object? defaultValue = null, string[]? choices = null) =>
        new
        {
            name,
            dataType = "GPString",
            displayName = name,
            description,
            direction = output ? "esriGPParameterDirectionOutput" : "esriGPParameterDirectionInput",
            defaultValue = defaultValue ?? string.Empty,
            parameterType = output ? "esriGPParameterTypeDerived"
                : required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
            category = string.Empty,
            choiceList = choices,
        };

    private static string? Named(string task) => Tasks.FirstOrDefault(t => string.Equals(t, task, StringComparison.OrdinalIgnoreCase));

    private static string NoSuchTask(string task) =>
        $"There is no task '{task}' in the offline packaging service. Its tasks are {string.Join(", ", Tasks.Select(t => $"'{t}'"))}.";

    // ------------------------------------------------------------------ running

    /// <summary>What one run came to: its outputs, or the status and sentence that refused it.</summary>
    internal sealed record Outcome(int Status, string? Refusal, IReadOnlyList<Output> Results);

    private static async Task ExecuteAsync(HttpContext context, string task, CancellationToken cancellation)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        Outcome outcome = await RunAsync(context, name, cancellation).ConfigureAwait(false);

        if (outcome.Refusal is { } refused)
        {
            await AnalysisEndpoints.RefuseAsync(context, outcome.Status, refused).ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new
        {
            results = outcome.Results.Select(r => new { paramName = r.Name, dataType = r.Type, value = (object)r.Value }),
            messages = Array.Empty<object>(),
        }).ConfigureAwait(false);
    }

    private static async Task SubmitAsync(HttpContext context, string task, CancellationToken cancellation)
    {
        if (Named(task) is not { } name)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, NoSuchTask(task)).ConfigureAwait(false);
            return;
        }

        Outcome outcome = await RunAsync(context, name, cancellation).ConfigureAwait(false);

        // A refusal for want of a token or a right is the HTTP answer a client expects; any other is a failed job it can read.
        if (outcome.Refusal is not null && outcome.Status is 499 or 401 or 403)
        {
            await AnalysisEndpoints.RefuseAsync(context, outcome.Status, outcome.Refusal).ConfigureAwait(false);
            return;
        }

        string id = "j" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string status = outcome.Refusal is null ? "esriJobSucceeded" : "esriJobFailed";
        Keep(id, new PackagingJob(status, outcome.Results, outcome.Refusal is null ? [] : [outcome.Refusal], DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10)));

        await AnalysisEndpoints.WriteAsync(context, new { jobId = id, jobStatus = status }).ConfigureAwait(false);
    }

    private static async Task JobAsync(HttpContext context, string task, string jobId)
    {
        if (Named(task) is null || Find(jobId) is not { } job)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "Job not found. A packaging job is kept for ten minutes.").ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new
        {
            jobId,
            jobStatus = job.Status,
            results = job.Status == "esriJobSucceeded"
                ? job.Results.ToDictionary(r => r.Name, r => (object)new { paramUrl = $"results/{r.Name}" })
                : [],
            inputs = new Dictionary<string, object>(),
            messages = job.Messages.Select(m => new { type = "esriJobMessageTypeError", description = m }),
        }).ConfigureAwait(false);
    }

    private static async Task ResultAsync(HttpContext context, string task, string jobId, string param)
    {
        if (Named(task) is null || Find(jobId) is not { Status: "esriJobSucceeded" } job
            || job.Results.FirstOrDefault(r => string.Equals(r.Name, param, StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "No such result. A packaging job's results are kept for ten minutes.").ConfigureAwait(false);
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new { paramName = found.Name, dataType = found.Type, value = (object)found.Value })
            .ConfigureAwait(false);
    }

    private static PackagingJob? Find(string jobId) =>
        (Jobs.TryGetValue(jobId, out PackagingJob? job) ? job : SiteHandoff.Load<PackagingJob>(Kind, jobId)) is { } found
        && found.Until > DateTimeOffset.UtcNow
            ? found
            : null;

    private static void Keep(string id, PackagingJob job)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SiteHandoff.Sweep(Kind, TimeSpan.FromMinutes(10));

        foreach ((string key, PackagingJob kept) in Jobs)
        {
            if (kept.Until <= now)
            {
                Jobs.TryRemove(key, out _);
            }
        }

        if (Jobs.Count >= MaximumJobs)
        {
            foreach ((string key, _) in Jobs.OrderBy(j => j.Value.Until).Take(Jobs.Count - MaximumJobs + 1).ToList())
            {
                Jobs.TryRemove(key, out _);
            }
        }

        Jobs[id] = job;
        SiteHandoff.Save(Kind, id, job);
    }

    private static async Task<Outcome> RunAsync(HttpContext context, string task, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return Refused(499, "A token is required: only a signed-in member who owns the map, or an administrator, makes map areas.");
        }

        IServiceProvider services = context.RequestServices;
        Func<string, string?> asked = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        IWebMapStore maps = services.GetRequiredService<IWebMapStore>();
        IMapAreaStore areas = services.GetRequiredService<IMapAreaStore>();
        IAuditLog audit = services.GetRequiredService<IAuditLog>();

        if (task == CreateTask)
        {
            return await CreateAsync(context, asked, maps, audit, current, cancellation).ConfigureAwait(false);
        }

        MapArea? area;
        string outputName = task == SetupTask ? "packages" : "result";
        string given;

        if (task == SetupTask)
        {
            given = asked("mapAreaItemId")?.Trim() ?? string.Empty;
            area = Guid.TryParse(given, out Guid item) ? await areas.FindAsync(item, cancellation).ConfigureAwait(false) : null;
        }
        else
        {
            // INFERRED: `packages` is the JSON SetupMapArea answered, a JSON array of package item ids or of objects naming one, or a single id.
            Guid[] ids = PackageIdsOf(asked("packages"));
            given = asked("packages")?.Trim() ?? string.Empty;

            if (ids.Length == 0)
            {
                return Refused(400, "packages is required: the JSON SetupMapArea answered, or the ids of package items of one Map Area.");
            }

            Guid? owner = null;

            foreach (Guid one in ids)
            {
                if (await areas.FindPackageAsync(one, cancellation).ConfigureAwait(false) is not { } held)
                {
                    return Refused(404, $"No package '{one:N}' that you may change.");
                }

                if (owner is not null && owner != held.AreaId)
                {
                    return Refused(400, "packages names packages of more than one Map Area; refresh one area at a time.");
                }

                owner = held.AreaId;
            }

            area = await areas.FindAsync(owner!.Value, cancellation).ConfigureAwait(false);
        }

        (WebMap? managed, _) = area is null ? (null, null) : await ManagedAsync(maps, current, area.MapId, cancellation).ConfigureAwait(false);

        if (area is null || managed is null)
        {
            return Refused(404, $"No map area for '{given}' that you may change.");
        }

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging
            .RefreshAsync(context, area, cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, audit, task == SetupTask ? "webmap.area.setup" : "webmap.area.refresh", area.MapId,
            AdminEndpoints.Detail(new { area = area.Id, via = "OfflinePackaging", refused = refusal?.Message }), packaged is not null, cancellation)
            .ConfigureAwait(false);

        return packaged is null
            ? Refused(refusal!.Status, refusal.Message)
            : new Outcome(200, null, [new Output(outputName, "GPString", PackagesJson(packaged))]);
    }

    private static async Task<Outcome> CreateAsync(
        HttpContext context, Func<string, string?> asked, IWebMapStore maps, IAuditLog audit, RequestPrincipal current,
        CancellationToken cancellation)
    {
        string mapId = (asked("mapItemId") ?? string.Empty).Trim().ToLowerInvariant();
        string title = (asked("outputName") ?? string.Empty).Trim();

        if (title.Length == 0)
        {
            // INFERRED: outputName is optional, so an area with none is named for its bookmark, or plainly.
            title = (asked("bookmark") ?? string.Empty).Trim() is { Length: > 0 } bookmarkName ? bookmarkName : "Map area";
        }

        if (!WebMaps.IsId(mapId))
        {
            return Refused(400, "mapItemId is required: the item id of a saved web map (32 hexadecimal characters).");
        }

        if (title.Length == 0 || title.Length > WebMaps.MaximumTitleLength)
        {
            return Refused(400, $"outputName is at most {WebMaps.MaximumTitleLength} characters.");
        }

        (WebMap? map, string? denied) = await ManagedAsync(maps, current, mapId, cancellation).ConfigureAwait(false);

        if (map is null)
        {
            return Refused(denied is null ? 404 : 403, denied ?? $"No web map '{mapId}' that you may change.");
        }

        (JsonObject? envelope, string? why) = AreaEnvelope(asked, map);

        Envelope extent = default;
        int wkid = 0;
        string? unreadable = null;

        if (envelope is null || !AdminEndpoints.TryReadAreaExtent(envelope, out extent, out wkid, out unreadable))
        {
            return Refused(400, why ?? unreadable!);
        }

        // The task takes no schedule or scale (read 2026-11-15); they are set afterwards, on the area (PUT .../settings).
        MapAreaSettings settings = new(null, null, false, null, null);

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging
            .CreateAsync(context, map, title, extent, wkid, cancellation, settings).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, audit, "webmap.area.create", mapId,
            AdminEndpoints.Detail(new { area = packaged?.Area.Id, title, via = "OfflinePackaging", refused = refusal?.Message }), packaged is not null, cancellation)
            .ConfigureAwait(false);

        return packaged is null
            ? Refused(refusal!.Status, refusal.Message)
            : new Outcome(200, null, [new Output("mapAreaItemId", "GPString", packaged.Area.Id.ToString("N"))]);
    }

    /// <summary>The web map, when the caller may change it: the map and null, or null and why not (null when they may not open it).</summary>
    private static async Task<(WebMap? Map, string? Denied)> ManagedAsync(
        IWebMapStore maps, RequestPrincipal current, string id, CancellationToken cancellation)
    {
        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map
            || !LayerAccess.Evaluate(map.Sharing, map.Owner, current.Principal, current.Authorization, map.SharedWith).IsAllowed())
        {
            return (null, null);
        }

        return LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization)
            ? (map, null)
            : (null, $"Only the map's owner, {map.OwnerName}, or an administrator can make areas of '{map.Title}'.");
    }

    private static Outcome Refused(int status, string why) => new(status, why, []);

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>
    /// The envelope the area is, from <c>areaType</c> and the three ways to give it (INFERRED): <c>extent</c> as an envelope,
    /// <c>area</c> as a polygon (whose bounds are taken) or an envelope, or <c>bookmark</c> by the name of one of the web map's
    /// <c>bookmarks</c>, whose <c>extent</c> the Web Map specification describes.
    /// </summary>
    /// <param name="asked">The parameters.</param>
    /// <param name="map">The map, for its bookmarks.</param>
    /// <returns>The envelope, or why not.</returns>
    internal static (JsonObject? Envelope, string? Why) AreaEnvelope(Func<string, string?> asked, WebMap map)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(map);

        string type = (asked("areaType") ?? string.Empty).Trim().ToUpperInvariant();

        if (type.Length == 0)
        {
            // The task's default is BOOKMARK; INFERRED: when nothing names the type, what was sent decides it.
            type = asked("extent") is { Length: > 0 } ? "ENVELOPE"
                : asked("area") is { Length: > 0 } ? "POLYGON"
                : "BOOKMARK";
        }

        switch (type)
        {
            case "ENVELOPE" when (asked("extent") is { Length: > 0 } ? asked("extent") : asked("area")) is { Length: > 0 } extent:
                return BoundsOf(TryParse(extent)) is { } box ? (box, null) : (null, "extent is an envelope {xmin, ymin, xmax, ymax, spatialReference}.");

            case "POLYGON" when asked("area") is { Length: > 0 } area:
                return BoundsOf(TryParse(area)) is { } bounds ? (bounds, null) : (null, "area is a polygon {rings, spatialReference} or an envelope.");

            case "BOOKMARK" when asked("bookmark") is { Length: > 0 } name:
                try
                {
                    JsonNode? found = (JsonNode.Parse(map.Document ?? "{}") as JsonObject)?["bookmarks"] is JsonArray marks
                        ? marks.FirstOrDefault(b => b is JsonObject o && o["name"] is JsonValue n && n.TryGetValue(out string? text)
                            && string.Equals(text, name.Trim(), StringComparison.OrdinalIgnoreCase))
                        : null;

                    return found is JsonObject bookmark && bookmark["extent"] is JsonObject bookExtent
                        ? (JsonNode.Parse(bookExtent.ToJsonString()) as JsonObject, null)
                        : (null, $"The map has no bookmark '{name.Trim()}' with an extent.");
                }
                catch (JsonException)
                {
                    return (null, "The map's document could not be read for its bookmarks.");
                }

            case "ENVELOPE" or "POLYGON" or "BOOKMARK":
                return (null, $"The area is not given: areaType {type} needs {(type == "ENVELOPE" ? "extent" : type == "POLYGON" ? "area" : "bookmark")}.");

            default:
                return (null, $"areaType '{type}' is not one of {string.Join(", ", AreaTypes)}.");
        }
    }

    /// <summary>The package item ids a <c>packages</c> value names (INFERRED shapes): an id, an array of ids or objects, or SetupMapArea's answer.</summary>
    internal static Guid[] PackageIdsOf(string? packages)
    {
        if (string.IsNullOrWhiteSpace(packages))
        {
            return [];
        }

        string text = packages.Trim();

        if (Guid.TryParse(text, out Guid single))
        {
            return [single];
        }

        try
        {
            JsonNode? node = JsonNode.Parse(text);

            if (node is JsonObject wrapper && wrapper["packages"] is JsonArray inner)
            {
                node = inner;
            }

            if (node is not JsonArray list)
            {
                return [];
            }

            List<Guid> ids = [];

            foreach (JsonNode? entry in list)
            {
                string? id = entry switch
                {
                    JsonValue v when v.TryGetValue(out string? t) => t,
                    JsonObject o => (o["id"] ?? o["itemId"]) is JsonValue iv && iv.TryGetValue(out string? t2) ? t2 : null,
                    _ => null,
                };

                if (!Guid.TryParse(id, out Guid parsed))
                {
                    return [];
                }

                ids.Add(parsed);
            }

            return [.. ids];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>An envelope from a polygon's rings (or an envelope as sent), keeping its spatial reference.</summary>
    private static JsonObject? BoundsOf(JsonNode? shape)
    {
        if (shape is not JsonObject geometry)
        {
            return null;
        }

        if (geometry["xmin"] is not null)
        {
            return geometry;
        }

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        if (geometry["rings"] is not JsonArray rings)
        {
            return null;
        }

        foreach (JsonNode? ring in rings)
        {
            foreach (JsonNode? point in ring as JsonArray ?? [])
            {
                if (point is JsonArray { Count: >= 2 } xy && xy[0] is JsonValue x && x.TryGetValue(out double px) && xy[1] is JsonValue y && y.TryGetValue(out double py))
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, px), Math.Min(minY, py), Math.Max(maxX, px), Math.Max(maxY, py));
                }
            }
        }

        return double.IsFinite(minX) && double.IsFinite(maxX)
            ? new JsonObject
            {
                ["xmin"] = minX, ["ymin"] = minY, ["xmax"] = maxX, ["ymax"] = maxY,
                ["spatialReference"] = geometry["spatialReference"]?.DeepClone(),
            }
            : null;
    }

    private static string PackagesJson(PackagedArea packaged) =>
        JsonSerializer.Serialize(new
        {
            mapAreaItemId = packaged.Area.Id.ToString("N"),
            status = packaged.Area.Status,
            packages = packaged.Packages.Select(p => new { id = p.Id.ToString("N"), format = p.Format, status = p.Status, error = p.Error }),
            skipped = packaged.Skipped.Select(s => new { service = s.Service, reason = s.Reason }),
        }, Web);
}
