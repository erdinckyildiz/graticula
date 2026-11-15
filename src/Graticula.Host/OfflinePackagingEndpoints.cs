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
/// <b>Nothing here is read from documentation, and that is said first.</b> The public ArcGIS REST reference for the
/// Enterprise services was fetched on 2026-11-15 for these three tasks, the service and its parameters, and answered
/// <i>Page not found</i> for every address tried (see ADR-288 §11a item 10 for the addresses): the tasks are not documented in
/// the public reference. What is known is what research note §5a measured on an Enterprise 12.1 portal: the service's path,
/// its three task names, that a Map Area item has the properties it has, and that the area was described to
/// <c>CreateMapArea</c> by some of <c>areaType</c>, <c>area</c>, <c>bookmark</c> and <c>extent</c>. <b>Every parameter name,
/// type, value and output below is <c>INFERRED</c></b> from those names and from what the GP service, task, execute and
/// job resources document in general (the print service, <see cref="PrintEndpoints"/>, is written from the same
/// resources); none was read from Enterprise's own task description, and a client that sends what Enterprise's task
/// takes may be refused with the parameter named.
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
/// the one thing that is a job (D-410); it is reported on the package item's status.
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

    private static readonly string[] AreaTypes = ["extent", "bookmark", "geometry"];

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

    private static object[] ParametersOf(string task) => task switch
    {
        CreateTask =>
        [
            Parameter("webmap", "GPString", "Web Map", "The web map's item id.", output: false, required: true),
            Parameter("name", "GPString", "Name", "What the area is called.", output: false, required: true),
            Parameter("areaType", "GPString", "Area Type", "How the area is given: its extent, a bookmark of the map, or a geometry. "
                + "Taken from whichever of the next three is sent when it is not.", output: false, required: false, null, AreaTypes),
            Parameter("extent", "GPString", "Extent", "An envelope {xmin, ymin, xmax, ymax, spatialReference}.", output: false, required: false),
            Parameter("area", "GPString", "Area", "A polygon {rings, spatialReference}, whose envelope is the area, or an envelope.", output: false, required: false),
            Parameter("bookmark", "GPString", "Bookmark", "The name of a bookmark of the web map; its extent is the area.", output: false, required: false),
            Parameter("mapAreaTileScale", "GPString", "Tile Scale", "{\"minScale\": n, \"maxScale\": n}: the scales the tile packages cover.", output: false, required: false),
            Parameter("packageRefreshSchedule", "GPString", "Package Refresh Schedule", "A weekly Quartz cron expression, for example 0 30 2 ? * MON.", output: false, required: false),
            Parameter("mapAreaItemId", "GPString", "Map Area Item Id", "The new Map Area item.", output: true, required: false),
        ],
        SetupTask =>
        [
            Parameter("mapAreaItemId", "GPString", "Map Area Item Id", "The Map Area item to write the missing packages of.", output: false, required: true),
            Parameter("packages", "GPString", "Packages", "The area's packages and their statuses, as JSON.", output: true, required: false),
        ],
        _ =>
        [
            Parameter("itemId", "GPString", "Item Id", "The Map Area item, or one of its package items; the area's packages are written again.", output: false, required: true),
            Parameter("packages", "GPString", "Packages", "The area's packages and their statuses, as JSON.", output: true, required: false),
        ],
    };

    private static object Parameter(
        string name, string type, string display, string description, bool output, bool required,
        object? defaultValue = null, string[]? choices = null) =>
        new
        {
            name,
            dataType = type,
            displayName = display,
            description,
            direction = output ? "esriGPParameterDirectionOutput" : "esriGPParameterDirectionInput",
            defaultValue,
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

        string key = task == SetupTask ? "mapAreaItemId" : "itemId";

        if (Guid.TryParse(asked(key)?.Trim(), out Guid item) is false)
        {
            return Refused(400, $"{key} is required: the id of a Map Area item{(task == RefreshTask ? " or of one of its package items" : string.Empty)}.");
        }

        MapArea? area = await areas.FindAsync(item, cancellation).ConfigureAwait(false);

        if (area is null && task == RefreshTask && await areas.FindPackageAsync(item, cancellation).ConfigureAwait(false) is { } package)
        {
            area = await areas.FindAsync(package.AreaId, cancellation).ConfigureAwait(false);
        }

        (WebMap? managed, _) = area is null ? (null, null) : await ManagedAsync(maps, current, area.MapId, cancellation).ConfigureAwait(false);

        if (area is null || managed is null)
        {
            return Refused(404, $"No map area '{asked(key)}' that you may change.");
        }

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging
            .RefreshAsync(context, area, cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, audit, task == SetupTask ? "webmap.area.setup" : "webmap.area.refresh", area.MapId,
            AdminEndpoints.Detail(new { area = area.Id, via = "OfflinePackaging", refused = refusal?.Message }), packaged is not null, cancellation)
            .ConfigureAwait(false);

        return packaged is null
            ? Refused(refusal!.Status, refusal.Message)
            : new Outcome(200, null, [new Output("packages", "GPString", PackagesJson(packaged))]);
    }

    private static async Task<Outcome> CreateAsync(
        HttpContext context, Func<string, string?> asked, IWebMapStore maps, IAuditLog audit, RequestPrincipal current,
        CancellationToken cancellation)
    {
        string mapId = (asked("webmap") ?? string.Empty).Trim().ToLowerInvariant();
        string title = (asked("name") ?? string.Empty).Trim();

        if (!WebMaps.IsId(mapId))
        {
            return Refused(400, "webmap is required: the item id of a saved web map (32 hexadecimal characters).");
        }

        if (title.Length == 0 || title.Length > WebMaps.MaximumTitleLength)
        {
            return Refused(400, $"name is required, and at most {WebMaps.MaximumTitleLength} characters.");
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

        JsonNode? scale = asked("mapAreaTileScale") is { Length: > 0 } scaleText ? TryParse(scaleText) : null;
        string? schedule = asked("packageRefreshSchedule") is { Length: > 0 } cron ? cron : null;

        if (!AdminEndpoints.TryReadAreaSettings(
                schedule, schedulePresent: schedule is not null, enabled: null, scale, scalePresent: scale is not null, current: null,
                DateTimeOffset.UtcNow, out MapAreaSettings settings, out string? badSettings))
        {
            return Refused(400, badSettings!);
        }

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

        string type = (asked("areaType") ?? string.Empty).Trim().ToLowerInvariant();

        if (type.Length == 0)
        {
            type = asked("extent") is { Length: > 0 } ? "extent"
                : asked("bookmark") is { Length: > 0 } ? "bookmark"
                : asked("area") is { Length: > 0 } ? "geometry"
                : string.Empty;
        }

        switch (type)
        {
            case "extent" when asked("extent") is { Length: > 0 } extent:
                return TryParse(extent) is JsonObject box ? (box, null) : (null, "extent is an envelope {xmin, ymin, xmax, ymax, spatialReference}.");

            case "geometry" or "area" when asked("area") is { Length: > 0 } area:
                return BoundsOf(TryParse(area)) is { } bounds ? (bounds, null) : (null, "area is a polygon {rings, spatialReference} or an envelope.");

            case "bookmark" when asked("bookmark") is { Length: > 0 } name:
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

            case "":
                return (null, "The area is not given: send extent, area or bookmark (and, if you like, areaType).");

            default:
                return (null, $"areaType '{type}' is not one this server takes with the parameter it needs. Its values are {string.Join(", ", AreaTypes)}: "
                    + "extent needs extent, bookmark needs bookmark, geometry needs area.");
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
