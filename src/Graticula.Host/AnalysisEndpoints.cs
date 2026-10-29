using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// The Spatial Analysis Service — ADR-222: a GPServer at the address ArcGIS Enterprise's portal names as its
/// <c>helperServices.analysis</c>, whose tasks run as jobs over hosted layers and write a new hosted layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The documented shape, copied as documented</b> (docs/research/gp-research.md): the service and task resources, the
/// asynchronous <c>submitJob</c>, the job with its status words and messages, the result and input parameters, and
/// <c>cancel</c>. Every task is asynchronous, as the analysis service's are; <c>execute</c> says so.
/// </para>
/// <para>
/// <b>What is checked here and what is run there.</b> A submission is refused at once for anything the caller can be
/// told now — a layer that is not this server's or not readable, a field the layer does not have, an output name that is
/// taken — and the parameters, with the layer each input resolved to, are written to the job. The runner
/// (<see cref="AnalysisRunner"/>) reads only that.
/// </para>
/// </remarks>
internal static partial class AnalysisEndpoints
{
    /// <summary>Where the service is, under <c>/rest/services</c>.</summary>
    public const string ServicePath = "/rest/services/System/SpatialAnalysisTools/GPServer";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>The units a distance may be given in, and a metre's worth of each.</summary>
    internal static readonly IReadOnlyDictionary<string, double> Units = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["Meters"] = 1,
        ["Kilometers"] = 1000,
        ["Feet"] = 0.3048,
        ["Miles"] = 1609.344,
        ["NauticalMiles"] = 1852,
        ["Yards"] = 0.9144,
    };

    /// <summary>The statistics a summary field may ask for, and PostgreSQL's aggregate for each.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Statistics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sum"] = "sum",
        ["Mean"] = "avg",
        ["Min"] = "min",
        ["Max"] = "max",
        ["Stddev"] = "stddev_samp",
    };

    /// <summary>The spatial relationships <c>JoinFeatures</c> takes, as ArcGIS documents them.</summary>
    internal static readonly string[] Relationships =
        ["Intersects", "WithinDistance", "Contains", "Within", "CompletelyContains", "CompletelyWithin", "IdenticalTo"];

    /// <summary>The units an area may be summed in, and a square metre's worth of each.</summary>
    internal static readonly IReadOnlyDictionary<string, double> AreaUnits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["SquareMeters"] = 1,
        ["SquareKilometers"] = 1_000_000,
        ["Hectares"] = 10_000,
        ["Acres"] = 4046.8564224,
        ["SquareMiles"] = 2_589_988.110336,
        ["SquareYards"] = 0.83612736,
        ["SquareFeet"] = 0.09290304,
    };

    /// <summary>Maps the routes.</summary>
    public static void Map(WebApplication app)
    {
        // ADR-018 condition 5: each route says what decides who reaches it. The service and its tasks describe tools and
        // hold no data; a submission reads its input only where that layer's service is shared with the caller; a job,
        // its results and its inputs are its owner's and an administrator's.
        app.MapMethods(ServicePath, ["GET", "POST"], ServiceAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}", ["GET", "POST"], TaskAsync).Governed(SharingGovernedExtensions.Public);
        app.MapMethods(ServicePath + "/{task}/submitJob", ["GET", "POST"], SubmitAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/execute", ["GET", "POST"], ExecuteAsync)
            .Governed(SharingGovernedExtensions.Public).DisableAntiforgery();
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}", ["GET", "POST"], JobAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}/results/{param}", ["GET", "POST"], ResultAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}/inputs/{param}", ["GET", "POST"], InputAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapMethods(ServicePath + "/{task}/jobs/{jobId}/cancel", ["GET", "POST"], CancelAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    // ------------------------------------------------------------------ the tasks, as the service describes them

    /// <summary>One parameter of a task, as its resource lists it.</summary>
    internal sealed record Parameter(
        string Name, string DataType, string DisplayName, string Description, bool Output, bool Required,
        object? DefaultValue = null, IReadOnlyList<string>? ChoiceList = null);

    /// <summary>One task: its name, what it does, its parameters, and the result parameter it writes.</summary>
    internal sealed record TaskInfo(
        string Name, string DisplayName, string Description, string Result, IReadOnlyList<Parameter> Parameters,
        string Primary = "inputLayer", string? Second = null);

    private static readonly Parameter OutputName = new(
        "outputName", "GPString", "Output Name",
        "{\"serviceProperties\": {\"name\": \"<name>\"}} writes the result as a new hosted feature layer; without it, the result "
        + "is returned as a feature collection.", false, false);

    private static readonly Parameter ContextParameter = new(
        "context", "GPString", "Context",
        "{\"extent\": {…}, \"outSR\": {\"wkid\": …}}: only features overlapping the extent are used, and the result is stored in outSR.",
        false, false);

    /// <summary>The tasks this server runs — ADR-222 §5.3.</summary>
    internal static readonly IReadOnlyList<TaskInfo> Tasks =
    [
        new(
            "CreateBuffers", "Create Buffers",
            "Creates polygons that cover a given distance from each feature, measured on the ground.",
            "bufferLayer",
            [
                new("inputLayer", "GPFeatureRecordSetLayer", "Input Layer", "The features to buffer: {\"url\": \"<layer url>\", \"filter\": \"<where>\"}.", false, true),
                new("distances", "GPMultiValue:GPDouble", "Distances", "One or more distances, in units. Either distances or field.", false, false),
                new("field", "GPString", "Field", "A field holding each feature's own distance, in units. Either distances or field.", false, false),
                new("units", "GPString", "Units", "The unit of the distances.", false, false, "Meters", [.. Units.Keys]),
                new("dissolveType", "GPString", "Dissolve Type", "None keeps a buffer per feature; Dissolve makes one per distance.", false, false, "None", ["None", "Dissolve"]),
                new("ringType", "GPString", "Ring Type", "Disks overlap; Rings leave out each smaller distance's area.", false, false, "Disks", ["Disks", "Rings"]),
                new("sideType", "GPString", "Side Type", "Full; Left or Right of a line; Outside a polygon — for a single distance.", false, false, "Full", ["Full", "Left", "Right", "Outside"]),
                new("endType", "GPString", "End Type", "Round or Flat line ends — for a single distance.", false, false, "Round", ["Round", "Flat"]),
                OutputName,
                ContextParameter,
                new("bufferLayer", "GPString", "Buffer Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
            ]),
        new(
            "DissolveBoundaries", "Dissolve Boundaries",
            "Merges polygons that share values in the fields named, or every polygon that overlaps or touches another when none are named.",
            "dissolvedLayer",
            [
                new("inputLayer", "GPFeatureRecordSetLayer", "Input Layer", "The polygons: {\"url\": \"<layer url>\", \"filter\": \"<where>\"}.", false, true),
                new("dissolveFields", "GPMultiValue:GPString", "Dissolve Fields", "Fields whose equal values are merged.", false, false),
                new("summaryFields", "GPMultiValue:GPString", "Summary Fields", "\"<field> <Sum|Mean|Min|Max|Stddev>\" for each statistic.", false, false),
                new("multiPartFeatures", "GPBoolean", "Multipart Features", "True keeps a group as one feature; false makes one per separate part.", false, false, true),
                OutputName,
                ContextParameter,
                new("dissolvedLayer", "GPString", "Dissolved Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
            ]),
        new(
            "OverlayLayers", "Overlay Layers",
            "Combines two layers: their intersection, their union, or the first less the second.",
            "outputLayer",
            [
                new("inputLayer", "GPFeatureRecordSetLayer", "Input Layer", "The features overlaid: {\"url\": \"<layer url>\"}.", false, true),
                new("overlayLayer", "GPFeatureRecordSetLayer", "Overlay Layer", "The features they are overlaid with: {\"url\": \"<layer url>\"}.", false, true),
                new("overlayType", "GPString", "Overlay Type", "Intersect, Union (polygons with polygons) or Erase.", false, false, "Intersect", ["Intersect", "Union", "Erase"]),
                new("outputType", "GPString", "Output Type", "Intersect only: Input keeps the lower dimension of the two; Line or Point the meeting lines or points.", false, false, "Input", ["Input", "Line", "Point"]),
                new("snapToInput", "GPBoolean", "Snap To Input", "True moves only the overlay's vertices to the input's; false moves both.", false, false, false),
                new("tolerance", "GPDouble", "Tolerance", "How near vertices snap, in the input layer's units; none when empty or 0.", false, false),
                OutputName,
                ContextParameter,
                new("outputLayer", "GPString", "Output Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
            ],
            Second: "overlayLayer"),
        new(
            "MergeLayers", "Merge Layers",
            "Copies the features of two layers of the same kind of geometry into one.",
            "mergedLayer",
            [
                new("inputLayer", "GPFeatureRecordSetLayer", "Input Layer", "The first layer: {\"url\": \"<layer url>\"}.", false, true),
                new("mergeLayer", "GPFeatureRecordSetLayer", "Merge Layer", "The second layer, of the same kind of geometry.", false, true),
                new("mergingAttributes", "GPMultiValue:GPString", "Merging Attributes",
                    "\"<field> Remove\", \"<field> Rename <name>\" or \"<merge field> Match <input field>\"; fields of the same name are matched anyway.", false, false),
                OutputName,
                ContextParameter,
                new("mergedLayer", "GPString", "Merged Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
            ],
            Second: "mergeLayer"),
        new(
            "AggregatePoints", "Aggregate Points",
            "Counts the points that fall in each polygon, and summarises their fields.",
            "aggregatedLayer",
            [
                new("pointLayer", "GPFeatureRecordSetLayer", "Point Layer", "The points: {\"url\": \"<layer url>\"}.", false, true),
                new("polygonLayer", "GPFeatureRecordSetLayer", "Polygon Layer", "The polygons they are counted in; required unless binType is given.", false, false),
                new("keepBoundariesWithNoPoints", "GPBoolean", "Keep Boundaries With No Points", "False leaves out a polygon with no point in it.", false, false, true),
                new("summaryFields", "GPMultiValue:GPString", "Summary Fields", "\"<field> <Sum|Mean|Min|Max|Stddev>\" over the points in each polygon.", false, false),
                new("groupByField", "GPString", "Group By Field", "A field of the points: its values are counted apart, in the groupSummary table.", false, false),
                new("minorityMajority", "GPBoolean", "Minority Majority", "With groupByField: the value with the most and the fewest points in each polygon.", false, false, false),
                new("percentPoints", "GPBoolean", "Percent Points", "With groupByField: each value's share of its polygon's points.", false, false, false),
                .. BinParameters,
                OutputName,
                ContextParameter,
                new("aggregatedLayer", "GPString", "Aggregated Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
                new("groupSummary", "GPString", "Group Summary", "With groupByField: the table of each value's statistics in each polygon.", true, false),
            ],
            Primary: "pointLayer",
            Second: "polygonLayer"),
        new(
            "FindCentroids", "Find Centroids",
            "A point at the centre of each line or polygon, or on it.",
            "outputLayer",
            [
                new("inputLayer", "GPFeatureRecordSetLayer", "Input Layer", "Lines or polygons: {\"url\": \"<layer url>\"}.", false, true),
                new("pointLocation", "GPBoolean", "Point Location", "True puts each point on its feature, where a centre may fall outside it.", false, false, false),
                OutputName,
                ContextParameter,
                new("outputLayer", "GPString", "Output Layer", "The new hosted layer: {\"url\": …, \"itemId\": …}.", true, false),
            ]),
        new(
            "SummarizeWithin", "Summarize Within",
            "Counts the points, sums the length of the lines or the area of the polygons, and summarises the fields of the "
            + "features inside each polygon or bin; lines and polygons in proportion to their part inside.",
            "resultLayer",
            [
                new("sumWithinLayer", "GPFeatureRecordSetLayer", "Sum Within Layer", "The polygons summarised in; required unless binType is given.", false, false),
                new("summaryLayer", "GPFeatureRecordSetLayer", "Summary Layer", "The points, lines or polygons summarised.", false, true),
                new("sumShape", "GPBoolean", "Sum Shape", "Count the points, sum the lines' length or the polygons' area.", false, false, true),
                new("shapeUnits", "GPString", "Shape Units", "Lines: a length unit; polygons: an area unit.", false, false, null,
                    [.. Units.Keys, .. AreaUnits.Keys]),
                new("summaryFields", "GPMultiValue:GPString", "Summary Fields", "\"<field> <Sum|Mean|Min|Max|Stddev>\"; required when sumShape is false.", false, false),
                new("groupByField", "GPString", "Group By Field", "A field of the summarised features: its values are summarised apart, in the groupBySummary table.", false, false),
                new("minorityMajority", "GPBoolean", "Minority Majority", "With groupByField: the value with the most and the least in each polygon.", false, false, false),
                new("percentShape", "GPBoolean", "Percent Shape", "With groupByField: each value's share of its polygon.", false, false, false),
                .. BinParameters,
                OutputName,
                ContextParameter,
                new("resultLayer", "GPString", "Result Layer", "The new hosted layer, or the feature collection.", true, false),
                new("groupBySummary", "GPString", "Group By Summary", "With groupByField: the table of each value's statistics in each polygon.", true, false),
            ],
            Primary: "summaryLayer",
            Second: "sumWithinLayer"),
        new(
            "JoinFeatures", "Join Features",
            "Adds the join layer's attributes to the target's features where they meet, are near, or hold equal values.",
            "outputLayer",
            [
                new("targetLayer", "GPFeatureRecordSetLayer", "Target Layer", "The features that receive the attributes.", false, true),
                new("joinLayer", "GPFeatureRecordSetLayer", "Join Layer", "The features whose attributes are joined.", false, true),
                new("spatialRelationship", "GPString", "Spatial Relationship", "How the features must meet; empty for an attribute join only.", false, false, null,
                    [.. Relationships]),
                new("spatialRelationshipDistance", "GPDouble", "Spatial Relationship Distance", "The distance for WithinDistance.", false, false),
                new("spatialRelationshipDistanceUnits", "GPString", "Spatial Relationship Distance Units", "The unit of that distance.", false, false, "Meters", [.. Units.Keys]),
                new("attributeRelationship", "GPString", "Attribute Relationship",
                    "[{\"targetField\": …, \"operator\": \"equal\", \"joinField\": …}].", false, false),
                new("joinOperation", "GPString", "Join Operation", "One output feature per target, or one per matching pair.", false, false,
                    "JoinOneToOne", ["JoinOneToOne", "JoinOneToMany"]),
                new("summaryFields", "GPString", "Summary Fields",
                    "[{\"statisticType\": \"SUM|MEAN|MIN|MAX|STDDEV\", \"onStatisticField\": …}], in a one-to-one join.", false, false),
                new("recordsToMatch", "GPString", "Records To Match",
                    "{\"groupByFields\": \"\", \"orderByFields\": \"<field> ASC|DESC\", \"topCount\": 1}: which match a one-to-one join keeps.", false, false),
                new("joinType", "GPString", "Join Type", "Inner keeps the targets that match; Left keeps them all.", false, false,
                    "Inner", ["Inner", "Left"]),
                OutputName,
                ContextParameter,
                new("outputLayer", "GPString", "Output Layer", "The new hosted layer, or the feature collection.", true, false),
            ],
            Primary: "targetLayer",
            Second: "joinLayer"),
    ];

    /// <summary>The bin parameters <c>AggregatePoints</c> and <c>SummarizeWithin</c> share.</summary>
    private static Parameter[] BinParameters =>
    [
        new("binType", "GPString", "Bin Type", "Square or Hexagon bins over the features, in place of a polygon layer.", false, false, null, ["Square", "Hexagon"]),
        new("binSize", "GPDouble", "Bin Size", "A square's side, or a hexagon's distance between parallel sides; required with binType.", false, false),
        new("binSizeUnit", "GPString", "Bin Size Unit", "The unit of the bin size.", false, false, "Meters", [.. Units.Keys]),
    ];

    /// <summary>The name a grouped summary's table is published under, beside its layer.</summary>
    internal static string GroupLayerName(string output) => output + "_groups";

    /// <summary>The result parameter a grouped summary's table is answered by, as each task documents it.</summary>
    internal static string? GroupResultOf(string task) => task switch
    {
        "AggregatePoints" => "groupSummary",
        "SummarizeWithin" => "groupBySummary",
        _ => null,
    };

    private static TaskInfo? TaskNamed(string name) =>
        Tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static async Task ServiceAsync(HttpContext context)
    {
        await WriteAsync(context, new
        {
            currentVersion = 11.3,
            serviceDescription = "Analysis tools of this server, run over its hosted feature layers (ADR-222).",
            tasks = Tasks.Select(t => t.Name).ToArray(),
            taskNames = Tasks.Select(t => t.Name).ToArray(),
            executionType = "esriExecutionTypeAsynchronous",
            resultMapServerName = string.Empty,
            maximumRecords = 1000,
            capabilities = string.Empty,
        }).ConfigureAwait(false);
    }

    private static async Task TaskAsync(HttpContext context, string task)
    {
        if (TaskNamed(task) is not { } info)
        {
            await RefuseAsync(context, 404, $"There is no task '{task}' here. The tasks are {string.Join(", ", Tasks.Select(t => t.Name))}.").ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, new
        {
            name = info.Name,
            displayName = info.DisplayName,
            description = info.Description,
            category = string.Empty,
            helpUrl = string.Empty,
            executionType = "esriExecutionTypeAsynchronous",
            parameters = info.Parameters.Select(p => new
            {
                name = p.Name,
                dataType = p.DataType,
                displayName = p.DisplayName,
                description = p.Description,
                direction = p.Output ? "esriGPParameterDirectionOutput" : "esriGPParameterDirectionInput",
                defaultValue = p.DefaultValue,
                parameterType = p.Output ? "esriGPParameterTypeDerived"
                    : p.Required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
                category = string.Empty,
                choiceList = p.ChoiceList,
            }).ToArray(),
        }).ConfigureAwait(false);
    }

    private static Task ExecuteAsync(HttpContext context, string task) =>
        RefuseAsync(context, 400, $"'{task}' is an asynchronous task. Submit it with submitJob and read the job's results.");

    // ------------------------------------------------------------------ submitting

    private static async Task SubmitAsync(
        HttpContext context,
        string task,
        PostgresLayerCatalog layers,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        IAnalysisStore runs,
        JobSignal signal,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (TaskNamed(task) is not { } info)
        {
            await RefuseAsync(context, 404, $"There is no task '{task}' here.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await RefuseAsync(context, 499, "Sign in to run an analysis: its result is a hosted layer of your own.").ConfigureAwait(false);
            return;
        }

        // ADR-222 §5.8b (INFERRED): its result is a hosted feature layer, so it takes what publishing one takes.
        if (!current.Authorization.Allows(Privilege.ContentPublishFeatures))
        {
            await RefuseAsync(context, 403, "Running an analysis makes a hosted feature layer, and your role may not publish one.").ConfigureAwait(false);
            return;
        }

        Func<string, string?> asked = await ParametersOfAsync(context, cancellation).ConfigureAwait(false);

        (JsonObject? plan, string? refusal) = await PlanAsync(info, asked, current, layers, catalog, contexts, cancellation).ConfigureAwait(false);

        if (plan is null)
        {
            await RefuseAsync(context, 400, refusal!).ConfigureAwait(false);
            return;
        }

        AnalysisRun run = await runs.CreateAsync(current.Principal.Id, info.Name, plan.ToJsonString(Compact), cancellation).ConfigureAwait(false);
        signal.Wake(JobKind.Analysis);

        await AdminEndpoints.AuditAsync(
            context, audit, "analysis.submit", plan["output"]?.GetValue<string>() ?? "(feature collection)",
            AdminEndpoints.Detail(new { task = info.Name, job = run.Job.Id, layer = plan["layer"]?.GetValue<string>() ?? "(feature collection)" }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await WriteAsync(context, new { jobId = JobIdOf(run.Job.Id), jobStatus = "esriJobSubmitted" }).ConfigureAwait(false);
    }

    /// <summary>
    /// What a submission asks, checked and resolved: the input layer by id, the fields it names, the output name — or the
    /// sentence that refuses it.
    /// </summary>
    internal static async Task<(JsonObject? Plan, string? Refusal)> PlanAsync(
        TaskInfo info,
        Func<string, string?> asked,
        RequestPrincipal current,
        PostgresLayerCatalog layers,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        JsonObject sent = [];

        foreach (Parameter parameter in info.Parameters.Where(p => !p.Output))
        {
            if (asked(parameter.Name) is { } value)
            {
                sent[parameter.Name] = value;
            }
        }

        // ---- the input layer, and the second one where the task takes two
        (PublishedLayer? layer, string? filter, string? why, JsonObject? collection, LayerDescription? collected) =
            await InputLayerAsync(info.Primary, asked(info.Primary), current, layers, cancellation).ConfigureAwait(false);

        if (layer is null)
        {
            return (null, why);
        }

        LayerDescription described = collected
            ?? (await contexts.GetAsync(layer, cancellation).ConfigureAwait(false)).Item2;
        Dictionary<string, FieldDescription> fields = described.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        if (!WhereClause.TryParse(filter, [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote, out _, out string? badFilter,
                described.Fields.ToDictionary(f => f.Name, f => f.Type)))
        {
            return (null, $"The {info.Primary}'s filter could not be read: {badFilter}");
        }

        PublishedLayer? second = null;
        string? secondFilter = null;
        LayerDescription? secondDescribed = null;
        JsonObject? secondCollection = null;

        // The areas of AggregatePoints and SummarizeWithin may be bins instead, and then no second layer is read.
        bool binned = !string.IsNullOrWhiteSpace(asked("binType")) && string.IsNullOrWhiteSpace(asked(info.Second ?? string.Empty));

        if (info.Second is { } secondName && !(binned && info.Name is "AggregatePoints" or "SummarizeWithin"))
        {
            LayerDescription? secondCollected;
            (second, secondFilter, why, secondCollection, secondCollected) =
                await InputLayerAsync(secondName, asked(secondName), current, layers, cancellation).ConfigureAwait(false);

            if (second is null)
            {
                return (null, info.Name is "AggregatePoints" or "SummarizeWithin" && string.IsNullOrWhiteSpace(asked(secondName))
                    ? $"{secondName} is required unless binType is given."
                    : why);
            }

            secondDescribed = secondCollected ?? (await contexts.GetAsync(second, cancellation).ConfigureAwait(false)).Item2;

            if (!WhereClause.TryParse(secondFilter, [.. secondDescribed.Fields.Select(f => f.Name)], LayerDefinition.Quote, out _, out badFilter,
                    secondDescribed.Fields.ToDictionary(f => f.Name, f => f.Type)))
            {
                return (null, $"The {secondName}'s filter could not be read: {badFilter}");
            }
        }

        // ---- the output: a new hosted layer when named, a feature collection returned when not (ADR-222 condition 4), or
        // a layer of the caller's overwritten when itemProperties says so
        string? output = null;
        PublishedService? overwritten = null;

        if (Overwrites(asked("outputName")) is { } named)
        {
            (overwritten, string? badTarget) = await OverwrittenAsync(named, current, layers, cancellation).ConfigureAwait(false);

            if (overwritten is null)
            {
                return (null, badTarget);
            }

            output = overwritten.Name;
        }
        else if (!string.IsNullOrWhiteSpace(asked("outputName")))
        {
            (output, string? badName) = await OutputNameAsync(asked("outputName"), catalog, layers, cancellation).ConfigureAwait(false);

            if (output is null)
            {
                return (null, badName);
            }
        }

        // ---- the context
        bool legacy = asked("context") is null && asked("env:outSR") is not null;
        (JsonObject? extent, int? outSrid, string? badContext) = ContextOf(asked("context") ?? asked("env:outSR"), legacy);

        if (badContext is not null)
        {
            return (null, badContext);
        }

        JsonObject plan = new()
        {
            ["sent"] = sent,
            ["layer"] = collection is null ? layer.Id.ToString() : null,
            ["collection"] = collection,
            ["filter"] = filter,
            ["output"] = output,
            ["overwrite"] = overwritten?.Id.ToString(),
            ["extent"] = extent,
            ["outSrid"] = outSrid,
            ["layer2"] = second is null || secondCollection is not null ? null : second.Id.ToString(),
            ["collection2"] = secondCollection,
            ["filter2"] = secondFilter,
            ["shape"] = (int)AnalysisShape.Polygons,
        };

        string? refusal = info.Name switch
        {
            "CreateBuffers" => PlanBuffers(asked, layer, fields, plan),
            "DissolveBoundaries" => PlanDissolve(asked, layer, fields, plan),
            "OverlayLayers" => PlanOverlay(asked, layer, described, second!, secondDescribed!, plan),
            "MergeLayers" => PlanMerge(asked, layer, described, second!, secondDescribed!, plan),
            "AggregatePoints" => PlanAggregate(asked, layer, described, second, secondDescribed, plan),
            "FindCentroids" => PlanCentroids(asked, layer, plan),
            "SummarizeWithin" => PlanSummarize(asked, layer, described, second, secondDescribed, plan),
            "JoinFeatures" => PlanJoin(asked, layer, described, second!, secondDescribed!, plan),
            _ => $"'{info.Name}' is listed and not built.",
        };

        // An overwritten layer keeps its kind: a point layer is not overwritten with polygons.
        if (refusal is null && overwritten is not null)
        {
            refusal = plan["group"] is not null
                ? "A summary grouped by a field writes a table beside its layer, and is not written over an existing layer; "
                  + "write it under a new name."
                : overwritten.Layers[0].GeometryType is { } kind && ShapeOf(kind) != (AnalysisShape)plan["shape"]!.GetValue<int>()
                    ? $"'{overwritten.Name}' holds {ShapeOf(kind).ToString().ToLowerInvariant()}, and this result is "
                      + $"{((AnalysisShape)plan["shape"]!.GetValue<int>()).ToString().ToLowerInvariant()}; a layer is overwritten "
                      + "only with its own kind."
                    : null;
        }

        // A grouped summary publishes its table beside the layer, under a name of its own that must be free as well.
        if (refusal is null && output is not null && overwritten is null && plan["group"] is not null
            && await HostedDataEndpoints.NameTakenAsync(catalog, GroupLayerName(output), cancellation).ConfigureAwait(false))
        {
            refusal = $"A layer named '{GroupLayerName(output)}' already exists, and a grouped summary writes its table under that name. "
                + "Choose another output name.";
        }

        return refusal is null ? (plan, null) : (null, refusal);
    }

    private static string? PlanBuffers(Func<string, string?> asked, PublishedLayer layer, Dictionary<string, FieldDescription> fields, JsonObject plan)
    {
        string? distances = asked("distances");
        string? field = asked("field");

        if (string.IsNullOrWhiteSpace(distances) == string.IsNullOrWhiteSpace(field))
        {
            return "Give the buffer distances, or the field holding each feature's distance — one of the two.";
        }

        if (!string.IsNullOrWhiteSpace(distances))
        {
            double[]? values = Doubles(distances);

            if (values is null || values.Length == 0 || values.Any(v => !double.IsFinite(v) || v <= 0))
            {
                return "distances is a list of numbers above zero, such as [1, 2.5, 5].";
            }

            plan["distances"] = new JsonArray([.. values.Distinct().Order().Select(v => (JsonNode)v)]);
        }
        else
        {
            if (!fields.TryGetValue(field!.Trim(), out FieldDescription found) || !IsNumeric(found.Type))
            {
                return $"The input layer has no numeric field '{field}' to read each feature's distance from.";
            }

            plan["field"] = found.Name;
        }

        string units = asked("units") ?? "Meters";

        if (!Units.ContainsKey(units))
        {
            return $"'{units}' is not a unit here; the units are {string.Join(", ", Units.Keys)}.";
        }

        plan["units"] = Units.Keys.First(k => string.Equals(k, units, StringComparison.OrdinalIgnoreCase));

        if (Choice(asked("dissolveType"), "None", ["None", "Dissolve"]) is not { } dissolve)
        {
            return "dissolveType is None or Dissolve.";
        }

        if (Choice(asked("ringType"), "Disks", ["Disks", "Rings"]) is not { } rings)
        {
            return "ringType is Disks or Rings.";
        }

        // sideType and endType, as documented: Left or Right of a line, Outside a polygon, flat line ends — each for a
        // single distance only.
        AnalysisShape shape = ShapeOf(layer.GeometryType);
        string side = asked("sideType")?.Trim() is { Length: > 0 } sent ? sent : "Full";
        string end = asked("endType")?.Trim() is { Length: > 0 } ended ? ended : "Round";
        string[] sides = shape switch
        {
            AnalysisShape.Lines => ["Full", "Left", "Right"],
            AnalysisShape.Polygons => ["Full", "Outside"],
            _ => ["Full"],
        };

        if (!sides.Contains(side, StringComparer.OrdinalIgnoreCase))
        {
            return $"sideType for {shape.ToString().ToLowerInvariant()} is {string.Join(" or ", sides)}.";
        }

        if (Choice(end, "Round", ["Round", "Flat"]) is not { } endType)
        {
            return "endType is Round or Flat.";
        }

        if (endType == "Flat" && shape != AnalysisShape.Lines)
        {
            return "endType Flat is for lines; a point's or a polygon's buffer has no ends.";
        }

        bool shaped = !string.Equals(side, "Full", StringComparison.OrdinalIgnoreCase) || endType == "Flat";

        if (shaped && plan["distances"] is JsonArray several && several.Count > 1)
        {
            return "sideType and endType are for a single distance; give one distance, or buffer both sides with round ends.";
        }

        plan["side"] = sides.First(s => string.Equals(s, side, StringComparison.OrdinalIgnoreCase));
        plan["flat"] = endType == "Flat";
        plan["dissolve"] = dissolve == "Dissolve";
        plan["rings"] = rings == "Rings";
        return null;
    }

    private static string? PlanDissolve(Func<string, string?> asked, PublishedLayer layer, Dictionary<string, FieldDescription> fields, JsonObject plan)
    {
        if (layer.GeometryType is not (GeometryKind.Polygon or GeometryKind.MultiPolygon))
        {
            return "Dissolve Boundaries takes a polygon layer.";
        }

        JsonArray groups = [];

        foreach (string name in Strings(asked("dissolveFields")) ?? [])
        {
            if (!fields.TryGetValue(name, out FieldDescription found))
            {
                return $"The input layer has no field '{name}' to dissolve by.";
            }

            groups.Add(found.Name);
        }

        JsonArray summaries = [];

        foreach (string entry in Strings(asked("summaryFields")) ?? [])
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || !fields.TryGetValue(parts[0], out FieldDescription found)
                || !Statistics.TryGetValue(parts[1], out string? aggregate))
            {
                return $"'{entry}' is not a summary: write a field and one of Sum, Mean, Min, Max or Stddev, such as \"Population Sum\".";
            }

            if (!IsNumeric(found.Type) && !(found.Type == FieldType.Date && aggregate is "min" or "max"))
            {
                return $"{parts[1]} of '{found.Name}' cannot be taken: it is not a number{(found.Type == FieldType.Date ? " (a date takes Min and Max)" : string.Empty)}.";
            }

            summaries.Add(new JsonObject
            {
                ["field"] = found.Name,
                ["statistic"] = aggregate,
                ["output"] = PostGisImporter.ColumnNameFor($"{parts[1]}_{found.Name}"),
            });
        }

        string? multi = asked("multiPartFeatures");

        plan["dissolveFields"] = groups;
        plan["summaries"] = summaries;
        plan["multiPart"] = multi is null || !string.Equals(multi.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    /// <summary>Names every result writes for itself.</summary>
    private static readonly string[] Always = ["objectid", "geom"];

    /// <summary>The names a task's result writes beside the columns it carries, so a source column of the same name is left out.</summary>
    internal static IReadOnlyList<string> WrittenBy(string task) => task switch
    {
        "CreateBuffers" => ["buff_dist", "orig_fid", "analysisarea"],
        "AggregatePoints" => ["point_count"],
        _ => [],
    };

    /// <summary>
    /// The source columns a result keeps: every attribute but the row's own id and its GlobalID, which the result has
    /// afresh, and a name the task writes itself. One rule, read by the plan and by the runner.
    /// </summary>
    internal static IReadOnlyList<string> CarriedColumns(PublishedLayer layer, LayerDescription described, string task)
    {
        string identity = layer.Definition.IntegerIdentityColumn ?? layer.Definition.IdentityColumn;
        string[] written = [.. Always, .. WrittenBy(task)];

        return [.. described.Fields
            .Where(f => f.Type != FieldType.Guid
                && !string.Equals(f.Name, identity, StringComparison.OrdinalIgnoreCase)
                && !written.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Name)];
    }

    /// <summary>Points, lines or polygons, from a layer's geometry type.</summary>
    internal static AnalysisShape ShapeOf(GeometryKind? kind) => kind switch
    {
        GeometryKind.Point or GeometryKind.MultiPoint => AnalysisShape.Points,
        GeometryKind.LineString or GeometryKind.MultiLineString => AnalysisShape.Lines,
        _ => AnalysisShape.Polygons,
    };

    /// <summary>A name not yet taken: the name itself, or with <c>_1</c>, <c>_2</c> … after it, as ArcGIS renames a clash.</summary>
    private static string Unclaimed(string name, ICollection<string> taken)
    {
        string candidate = name;

        for (int i = 1; taken.Contains(candidate) || Always.Contains(candidate, StringComparer.OrdinalIgnoreCase); i++)
        {
            candidate = PostGisImporter.ColumnNameFor($"{name}_{i.ToString(CultureInfo.InvariantCulture)}");
        }

        taken.Add(candidate);
        return candidate;
    }

    private static string? PlanOverlay(
        Func<string, string?> asked, PublishedLayer layer, LayerDescription described,
        PublishedLayer overlay, LayerDescription overlayDescribed, JsonObject plan)
    {
        if (Choice(asked("overlayType"), "Intersect", ["Intersect", "Union", "Erase"]) is not { } operation)
        {
            return "overlayType is Intersect, Union or Erase.";
        }

        AnalysisShape first = ShapeOf(layer.GeometryType);
        AnalysisShape other = ShapeOf(overlay.GeometryType);

        // outputType, for an intersection only: Line where neither layer is points, Point anywhere.
        if (Choice(asked("outputType"), "Input", ["Input", "Line", "Point"]) is not { } outputType)
        {
            return "outputType is Input, Line or Point.";
        }

        if (outputType != "Input" && operation != "Intersect")
        {
            return $"outputType {outputType} is for Intersect; {operation} keeps the input's kind.";
        }

        if (outputType == "Line" && (first == AnalysisShape.Points || other == AnalysisShape.Points))
        {
            return "outputType Line takes lines or polygons; a point meets nothing along a line.";
        }

        // Snapping: within the tolerance, in the input layer's own units, vertices move to each other — or, with
        // snapToInput, only the overlay's move to the input's.
        double tolerance = 0;

        if (!string.IsNullOrWhiteSpace(asked("tolerance"))
            && (!double.TryParse(asked("tolerance"), NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance) || tolerance < 0))
        {
            return "tolerance is a distance of zero or more, in the input layer's units.";
        }

        plan["tolerance"] = tolerance;
        plan["snapToInput"] = string.Equals(asked("snapToInput")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        plan["outputType"] = outputType;

        if (operation == "Union" && (first != AnalysisShape.Polygons || other != AnalysisShape.Polygons))
        {
            return "Union overlays polygons with polygons.";
        }

        if (operation == "Erase" && other != AnalysisShape.Polygons)
        {
            return "Erase takes away the area of a polygon layer; the overlay layer holds no area.";
        }

        HashSet<string> taken = new(CarriedColumns(layer, described, "OverlayLayers"), StringComparer.OrdinalIgnoreCase);
        JsonArray columns = [];

        foreach (string column in CarriedColumns(overlay, overlayDescribed, "OverlayLayers"))
        {
            columns.Add(new JsonObject { ["source"] = column, ["output"] = Unclaimed(column, taken) });
        }

        plan["overlayType"] = operation;
        plan["overlayColumns"] = columns;
        plan["shape"] = (int)(operation switch
        {
            "Intersect" when outputType == "Line" => AnalysisShape.Lines,
            "Intersect" when outputType == "Point" => AnalysisShape.Points,
            "Intersect" => (AnalysisShape)Math.Min((int)first, (int)other),
            "Erase" => first,
            _ => AnalysisShape.Polygons,
        });
        return null;
    }

    private static string? PlanMerge(
        Func<string, string?> asked, PublishedLayer layer, LayerDescription described,
        PublishedLayer merge, LayerDescription mergeDescribed, JsonObject plan)
    {
        AnalysisShape shape = ShapeOf(layer.GeometryType);

        if (ShapeOf(merge.GeometryType) != shape)
        {
            return "The two layers hold different kinds of geometry; merging takes points with points, lines with lines, polygons with polygons.";
        }

        Dictionary<string, FieldDescription> inputFields = described.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FieldDescription> mergeFields = mergeDescribed.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> renamed = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> matched = new(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in Strings(asked("mergingAttributes")) ?? [])
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (parts.Length >= 2 ? parts[1].ToLowerInvariant() : string.Empty)
            {
                case "remove" when parts.Length == 2 && (inputFields.ContainsKey(parts[0]) || mergeFields.ContainsKey(parts[0])):
                    removed.Add(parts[0]);
                    break;
                case "rename" when parts.Length == 3 && mergeFields.ContainsKey(parts[0]):
                    renamed[parts[0]] = PostGisImporter.ColumnNameFor(parts[2]);
                    break;
                case "match" when parts.Length == 3 && mergeFields.TryGetValue(parts[0], out FieldDescription from)
                    && inputFields.TryGetValue(parts[2], out FieldDescription to):
                    if (from.Type == FieldType.Text && IsNumeric(to.Type))
                    {
                        return $"'{from.Name}' holds text and '{to.Name}' numbers; text is not matched into a number.";
                    }

                    matched[parts[0]] = to.Name;
                    break;
                default:
                    return $"'{entry}' is not a merging attribute: write \"<field> Remove\", \"<field> Rename <name>\" or "
                        + "\"<merge field> Match <input field>\", with fields the layers have.";
            }
        }

        string[] inputColumns = [.. CarriedColumns(layer, described, "MergeLayers").Where(c => !removed.Contains(c))];
        HashSet<string> taken = new(inputColumns, StringComparer.OrdinalIgnoreCase);
        JsonArray mergeColumns = [];
        JsonArray mergeOnly = [];

        foreach (string column in CarriedColumns(merge, mergeDescribed, "MergeLayers").Where(c => !removed.Contains(c)))
        {
            // Matched by name or by the request, the input's column takes it; otherwise it is a column of its own.
            string? into = matched.TryGetValue(column, out string? target) ? target
                : !renamed.ContainsKey(column) && inputColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
                    ? inputColumns.First(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase))
                    : null;

            if (into is not null && mergeFields[column].Type == FieldType.Text && IsNumeric(inputFields[into].Type))
            {
                return $"'{column}' holds text in the merge layer and numbers in the input layer; rename it to keep both.";
            }

            string output = into ?? Unclaimed(renamed.TryGetValue(column, out string? name) ? name : column, taken);
            mergeColumns.Add(new JsonObject { ["source"] = column, ["output"] = output });

            if (into is null)
            {
                mergeOnly.Add(new JsonObject { ["source"] = column, ["output"] = output });
            }
        }

        plan["inputColumns"] = new JsonArray([.. inputColumns.Select(c => (JsonNode)new JsonObject { ["source"] = c, ["output"] = c })]);
        plan["mergeColumns"] = mergeColumns;
        plan["mergeOnly"] = mergeOnly;
        plan["shape"] = (int)shape;
        return null;
    }

    private static string? PlanAggregate(
        Func<string, string?> asked, PublishedLayer points, LayerDescription pointsDescribed,
        PublishedLayer? polygons, LayerDescription? polygonsDescribed, JsonObject plan)
    {
        if (ShapeOf(points.GeometryType) != AnalysisShape.Points)
        {
            return "pointLayer holds no points.";
        }

        if (polygons is not null && ShapeOf(polygons.GeometryType) != AnalysisShape.Polygons)
        {
            return "polygonLayer holds no polygons.";
        }

        if (polygons is null && PlanBins(asked, plan) is { } badBins)
        {
            return badBins;
        }


        Dictionary<string, FieldDescription> fields = pointsDescribed.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        HashSet<string> taken = new([.. polygons is null ? [] : CarriedColumns(polygons, polygonsDescribed!, "AggregatePoints"), "point_count"],
            StringComparer.OrdinalIgnoreCase);
        JsonArray summaries = [];

        foreach (string entry in Strings(asked("summaryFields")) ?? [])
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || !fields.TryGetValue(parts[0], out FieldDescription found)
                || !Statistics.TryGetValue(parts[1], out string? aggregate))
            {
                return $"'{entry}' is not a summary: write a field of the points and one of Sum, Mean, Min, Max or Stddev.";
            }

            if (!IsNumeric(found.Type) && !(found.Type == FieldType.Date && aggregate is "min" or "max"))
            {
                return $"{parts[1]} of '{found.Name}' cannot be taken: it is not a number.";
            }

            summaries.Add(new JsonObject
            {
                ["field"] = found.Name,
                ["statistic"] = aggregate,
                ["output"] = Unclaimed(PostGisImporter.ColumnNameFor($"{parts[1]}_{found.Name}"), taken),
            });
        }

        if (PlanGroup(asked, pointsDescribed, taken, "percentPoints", "percent_points", plan) is { } badGroup)
        {
            return badGroup;
        }

        string? keep = asked("keepBoundariesWithNoPoints");
        plan["summaries"] = summaries;
        plan["keepEmpty"] = keep is null || !string.Equals(keep.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        plan["shape"] = (int)AnalysisShape.Polygons;
        return null;
    }

    /// <summary>Bins in place of a polygon layer: their kind and their size in metres — or why not.</summary>
    private static string? PlanBins(Func<string, string?> asked, JsonObject plan)
    {
        if (Choice(asked("binType"), "Square", ["Square", "Hexagon"]) is not { } kind)
        {
            return "binType is Square or Hexagon.";
        }

        if (!double.TryParse(asked("binSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size) || !(size > 0))
        {
            return "binSize is required with binType, and is a distance above zero.";
        }

        if (Choice(asked("binSizeUnit"), "Meters", [.. Units.Keys]) is not { } unit)
        {
            return $"binSizeUnit is one of {string.Join(", ", Units.Keys)}.";
        }

        double metres = size * Units[unit];

        // A bin smaller than a metre over a city is millions of rows nobody asked for; ArcGIS caps its bins in count.
        if (metres < 1)
        {
            return "A bin is at least a metre across.";
        }

        plan["bins"] = new JsonObject { ["hexagon"] = kind == "Hexagon", ["metres"] = metres };
        return null;
    }

    /// <summary>
    /// A summary by a field's values — its column, the areas' majority and minority columns, the group table's share
    /// column — or why not. Nothing is planned when no field is named, and the two booleans then say nothing.
    /// </summary>
    private static string? PlanGroup(
        Func<string, string?> asked, LayerDescription summarized, ICollection<string> taken, string percentParameter,
        string percentName, JsonObject plan)
    {
        string? sent = asked("groupByField")?.Trim();

        if (string.IsNullOrEmpty(sent) || sent == "\"\"")
        {
            return null;
        }

        // A struct: FirstOrDefault of none is an empty field, not null, so the match is asked for by index.
        int at = summarized.Fields.ToList().FindIndex(f => string.Equals(f.Name, sent, StringComparison.OrdinalIgnoreCase));

        if (at < 0 || summarized.Fields[at].Type == FieldType.Binary)
        {
            return $"groupByField is a field of the summarised features; there is no '{sent}' to group by.";
        }

        FieldDescription field = summarized.Fields[at];

        bool True(string parameter) => string.Equals(asked(parameter)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        bool majority = True("minorityMajority");

        plan["group"] = new JsonObject
        {
            ["column"] = field.Name,
            ["majority"] = majority ? Unclaimed(PostGisImporter.ColumnNameFor($"majority_{field.Name}"), taken) : null,
            ["minority"] = majority ? Unclaimed(PostGisImporter.ColumnNameFor($"minority_{field.Name}"), taken) : null,
            ["percent"] = True(percentParameter) ? percentName : null,
        };
        return null;
    }

    /// <summary>The summary fields of a summarised layer — or why one cannot be taken.</summary>
    private static (JsonArray? Summaries, string? Why) SummariesOf(
        IReadOnlyList<string>? entries, LayerDescription described, ICollection<string> taken, bool weighted)
    {
        Dictionary<string, FieldDescription> fields = described.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        JsonArray summaries = [];

        foreach (string entry in entries ?? [])
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || !fields.TryGetValue(parts[0], out FieldDescription found)
                || !Statistics.TryGetValue(parts[1], out string? aggregate))
            {
                return (null, $"'{entry}' is not a summary: write a field and one of Sum, Mean, Min, Max or Stddev.");
            }

            if (!IsNumeric(found.Type) && !(found.Type == FieldType.Date && aggregate is "min" or "max"))
            {
                return (null, $"{parts[1]} of '{found.Name}' cannot be taken: it is not a number.");
            }

            // A weighted minimum of a date is still a date; a weighted sum of one would not be.
            _ = weighted;
            summaries.Add(new JsonObject
            {
                ["field"] = found.Name,
                ["statistic"] = aggregate,
                ["output"] = Unclaimed(PostGisImporter.ColumnNameFor($"{parts[1]}_{found.Name}"), taken),
            });
        }

        return (summaries, null);
    }

    private static string? PlanSummarize(
        Func<string, string?> asked, PublishedLayer summarized, LayerDescription summarizedDescribed,
        PublishedLayer? areas, LayerDescription? areasDescribed, JsonObject plan)
    {
        if (areas is not null && ShapeOf(areas.GeometryType) != AnalysisShape.Polygons)
        {
            return "sumWithinLayer holds no polygons.";
        }

        if (areas is null && PlanBins(asked, plan) is { } badBins)
        {
            return badBins;
        }

        AnalysisShape shape = ShapeOf(summarized.GeometryType);
        bool sumShape = asked("sumShape") is not { } s || !string.Equals(s.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string>? entries = Strings(asked("summaryFields"));

        if (!sumShape && (entries is null || entries.Count == 0))
        {
            return "summaryFields is required when sumShape is false: there would be nothing to summarise.";
        }

        HashSet<string> taken = new(areas is null ? [] : CarriedColumns(areas, areasDescribed!, "SummarizeWithin"), StringComparer.OrdinalIgnoreCase);
        string? countName = null;
        string? measureName = null;
        double perUnit = 1;

        if (sumShape)
        {
            countName = Unclaimed(shape == AnalysisShape.Points ? "point_count" : "count", taken);

            if (shape != AnalysisShape.Points)
            {
                bool lines = shape == AnalysisShape.Lines;
                IReadOnlyDictionary<string, double> units = lines ? Units : AreaUnits;
                string fallback = lines ? "Meters" : "SquareMeters";

                if (Choice(asked("shapeUnits"), fallback, [.. units.Keys]) is not { } unit)
                {
                    return $"shapeUnits for {(lines ? "lines" : "polygons")} is one of {string.Join(", ", units.Keys)}.";
                }

                perUnit = units[unit];
                measureName = Unclaimed(PostGisImporter.ColumnNameFor($"sum_{(lines ? "length" : "area")}_{unit}"), taken);
            }
        }

        (JsonArray? summaries, string? why) = SummariesOf(entries, summarizedDescribed, taken, shape != AnalysisShape.Points);

        if (summaries is null)
        {
            return why;
        }

        if (PlanGroup(asked, summarizedDescribed, taken, "percentShape", "percent_shape", plan) is { } badGroup)
        {
            return badGroup;
        }

        plan["summary"] = new JsonObject
        {
            ["count"] = countName,
            ["measure"] = shape == AnalysisShape.Points || !sumShape ? 0 : (int)shape - 1,
            ["measureName"] = measureName,
            ["perUnit"] = perUnit,
            ["keepEmpty"] = true,
            ["weighted"] = shape != AnalysisShape.Points,
        };
        plan["summaries"] = summaries;
        plan["summarizedShape"] = (int)shape;
        plan["shape"] = (int)AnalysisShape.Polygons;
        return null;
    }

    private static string? PlanJoin(
        Func<string, string?> asked, PublishedLayer target, LayerDescription targetDescribed,
        PublishedLayer join, LayerDescription joinDescribed, JsonObject plan)
    {
        string? relationship = null;

        if (!string.IsNullOrWhiteSpace(asked("spatialRelationship")))
        {
            relationship = Relationships.FirstOrDefault(r => string.Equals(r, asked("spatialRelationship")!.Trim(), StringComparison.OrdinalIgnoreCase));

            if (relationship is null)
            {
                return $"spatialRelationship is one of {string.Join(", ", Relationships)}.";
            }
        }

        double metres = 0;

        if (relationship == "WithinDistance")
        {
            if (!double.TryParse(asked("spatialRelationshipDistance"), NumberStyles.Float, CultureInfo.InvariantCulture, out double distance)
                || distance < 0)
            {
                return "WithinDistance needs spatialRelationshipDistance, a distance of zero or more.";
            }

            if (Choice(asked("spatialRelationshipDistanceUnits"), "Meters", [.. Units.Keys]) is not { } unit)
            {
                return $"spatialRelationshipDistanceUnits is one of {string.Join(", ", Units.Keys)}.";
            }

            metres = distance * Units[unit];
        }

        Dictionary<string, FieldDescription> targetFields = targetDescribed.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FieldDescription> joinFields = joinDescribed.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        JsonArray attributes = [];

        if (!string.IsNullOrWhiteSpace(asked("attributeRelationship")))
        {
            try
            {
                foreach (JsonObject pair in (JsonNode.Parse(asked("attributeRelationship")!) as JsonArray ?? []).OfType<JsonObject>())
                {
                    string? targetField = pair["targetField"]?.GetValue<string>();
                    string? joinField = pair["joinField"]?.GetValue<string>();
                    string operation = pair["operator"]?.GetValue<string>() ?? "equal";

                    if (!string.Equals(operation, "equal", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"An attribute relationship's operator is equal; '{operation}' is not one ArcGIS documents.";
                    }

                    if (targetField is null || !targetFields.TryGetValue(targetField, out FieldDescription tf))
                    {
                        return $"The target layer has no field '{targetField}'.";
                    }

                    if (joinField is null || !joinFields.TryGetValue(joinField, out FieldDescription jf))
                    {
                        return $"The join layer has no field '{joinField}'.";
                    }

                    if (IsNumeric(tf.Type) != IsNumeric(jf.Type))
                    {
                        return $"'{tf.Name}' and '{jf.Name}' cannot be equal: one is a number and the other is not.";
                    }

                    attributes.Add(new JsonObject { ["target"] = tf.Name, ["join"] = jf.Name });
                }
            }
            catch (JsonException)
            {
                return "attributeRelationship is [{\"targetField\": …, \"operator\": \"equal\", \"joinField\": …}].";
            }
        }

        if (relationship is null && attributes.Count == 0)
        {
            return "Join Features needs a spatialRelationship, an attributeRelationship, or both.";
        }

        if (Choice(asked("joinOperation"), "JoinOneToOne", ["JoinOneToOne", "JoinOneToMany"]) is not { } operationName)
        {
            return "joinOperation is JoinOneToOne or JoinOneToMany.";
        }

        if (Choice(asked("joinType"), "Inner", ["Inner", "Left"]) is not { } joinType)
        {
            return "joinType is Inner or Left.";
        }

        bool oneToMany = operationName == "JoinOneToMany";
        HashSet<string> taken = new(CarriedColumns(target, targetDescribed, "JoinFeatures"), StringComparer.OrdinalIgnoreCase);
        string countName = oneToMany ? "count" : Unclaimed("count", taken);

        // Summaries: ArcGIS's [{statisticType, onStatisticField}], or the "<field> <Statistic>" every other task takes.
        JsonArray summaries = [];
        string? sentSummaries = asked("summaryFields");

        if (!string.IsNullOrWhiteSpace(sentSummaries))
        {
            List<string> entries = [];

            try
            {
                foreach (JsonNode? one in JsonNode.Parse(sentSummaries) as JsonArray ?? [])
                {
                    if (one is JsonObject o)
                    {
                        string statistic = o["statisticType"]?.GetValue<string>() ?? string.Empty;
                        string? field = o["onStatisticField"]?.GetValue<string>();

                        // COUNT is always returned, so asking for it adds nothing.
                        if (!string.Equals(statistic, "COUNT", StringComparison.OrdinalIgnoreCase) && field is not null)
                        {
                            entries.Add($"{field} {statistic}");
                        }
                    }
                    else if (one is JsonValue v)
                    {
                        entries.Add(v.GetValue<string>());
                    }
                }
            }
            catch (JsonException)
            {
                entries.AddRange(Strings(sentSummaries) ?? []);
            }

            if (oneToMany && entries.Count > 0)
            {
                return "Summary statistics are taken in a one-to-one join; a one-to-many join keeps each match whole.";
            }

            (JsonArray? read, string? why) = SummariesOf(entries, joinDescribed, taken, weighted: false);

            if (read is null)
            {
                return why;
            }

            summaries = read;
        }

        JsonArray joinColumns = [];

        if (oneToMany || summaries.Count == 0)
        {
            foreach (string column in CarriedColumns(join, joinDescribed, "JoinFeatures"))
            {
                joinColumns.Add(new JsonObject { ["source"] = column, ["output"] = Unclaimed(column, taken) });
            }
        }

        // recordsToMatch: which match a one-to-one join keeps, when no spatial relationship decides.
        JsonArray orderBy = [];

        if (!string.IsNullOrWhiteSpace(asked("recordsToMatch")))
        {
            try
            {
                JsonObject records = JsonNode.Parse(asked("recordsToMatch")!) as JsonObject ?? [];

                if (records["groupByFields"]?.GetValue<string>() is { Length: > 0 } grouped && grouped.Trim().Length > 0)
                {
                    return "recordsToMatch's groupByFields is not built here; order the matches with orderByFields.";
                }

                foreach (string part in (records["orderByFields"]?.GetValue<string>() ?? string.Empty)
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (!joinFields.TryGetValue(words[0], out FieldDescription by))
                    {
                        return $"recordsToMatch orders by '{words[0]}', which the join layer does not have.";
                    }

                    orderBy.Add(new JsonObject
                    {
                        ["field"] = by.Name,
                        ["descending"] = words.Length > 1 && string.Equals(words[1], "DESC", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
            catch (JsonException)
            {
                return "recordsToMatch is {\"groupByFields\": \"\", \"orderByFields\": \"<field> ASC|DESC\", \"topCount\": 1}.";
            }
        }

        AnalysisShape shape = ShapeOf(target.GeometryType);
        plan["join"] = new JsonObject
        {
            ["relationship"] = relationship,
            ["metres"] = metres,
            ["attributes"] = attributes,
            ["oneToMany"] = oneToMany,
            ["keepAll"] = joinType == "Left",
            ["columns"] = joinColumns,
            ["count"] = countName,
            ["orderBy"] = orderBy,
        };
        plan["summaries"] = summaries;
        plan["shape"] = (int)shape;
        plan["single"] = target.GeometryType == GeometryKind.Point;
        return null;
    }

    private static string? PlanCentroids(Func<string, string?> asked, PublishedLayer layer, JsonObject plan)
    {
        if (ShapeOf(layer.GeometryType) == AnalysisShape.Points)
        {
            return "Find Centroids takes lines or polygons; a point is its own centre.";
        }

        plan["inside"] = string.Equals(asked("pointLocation")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        plan["shape"] = (int)AnalysisShape.Points;

        // One point each, stored as a point, as ArcGIS's centroids are — not the multipoint an overlay of points makes.
        plan["single"] = true;
        return null;
    }

    /// <summary>The input layer a request names, if it is this server's, hosted, and readable by the caller.</summary>
    private static async Task<(PublishedLayer? Layer, string? Filter, string? Why, JsonObject? Collection, LayerDescription? Described)> InputLayerAsync(
        string parameter, string? sent, RequestPrincipal current, PostgresLayerCatalog layers, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(sent))
        {
            return (null, null, $"{parameter} is required: {{\"url\": \"<a hosted feature layer of this server>\"}} or a feature collection.", null, null);
        }

        string? url;
        string? filter = null;

        try
        {
            JsonNode? node = JsonNode.Parse(sent);

            if (node is JsonObject named && named["url"] is JsonValue address)
            {
                url = address.GetValue<string>();
                filter = named["filter"]?.GetValue<string>();
            }
            else if (node is JsonObject collection && AnalysisCollections.IsCollection(collection))
            {
                // ADR-222 condition 4: read now, so a malformed one is refused before it is queued; written by the runner.
                if (!AnalysisCollections.TryRead(collection, parameter, out AnalysisCollections.Collection? read, out string? bad)
                    || !AnalysisCollections.TryRows(read!, parameter, out _, out bad))
                {
                    return (null, null, bad, null, null);
                }

                return (AnalysisCollections.LayerOf(read!, "collection"), collection["filter"]?.GetValue<string>(), null, collection, read!.Described);
            }
            else
            {
                return (null, null, $"{parameter} is {{\"url\": \"<layer url>\"}}, optionally with a \"filter\", or a feature collection.", null, null);
            }
        }
        catch (JsonException)
        {
            // A bare url is what some clients send.
            url = sent.Trim();
        }

        int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
        string[] path = at < 0 ? [] : url[(at + "/rest/services/".Length)..].Split('?')[0].Trim('/').Split('/');
        int face = Array.FindIndex(path, p => p is "FeatureServer" or "MapServer");

        if (face < 1 || face != path.Length - 2 || !int.TryParse(path[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return (null, null, $"'{url}' is not a layer of this server: a hosted feature layer's url ends in /FeatureServer/<layer>.", null, null);
        }

        string? folder = face == 2 ? path[0] : null;
        string name = path[face - 1];

        if (await layers.FindServiceAsync(folder, name, cancellation).ConfigureAwait(false) is not { } service
            || !LayerAccess.Evaluate(service.Sharing, service.Owner, current.Principal, current.Authorization, service.SharedWith).IsAllowed()
            || service.LayerOrTable(index) is not { } layer)
        {
            return (null, null, $"There is no layer at '{url}' that you may read.", null, null);
        }

        if (!layer.Definition.IsHosted || !string.Equals(layer.Definition.SchemaName, PostGisImporter.HostedSchema, StringComparison.Ordinal))
        {
            return (null, null, "Analysis reads hosted layers for now; this layer is read from a registered source.", null, null);
        }

        if (layer.GeometryType is null)
        {
            return (null, null, "This is a table, which has no shapes to analyse.", null, null);
        }

        return (layer, filter, null, null, null);
    }

    /// <summary>
    /// The layer an <c>outputName</c> asks to overwrite — <c>{"itemProperties": {"itemId" | "serviceURL" | "name": …,
    /// "overwrite": true}}</c>, as ArcGIS documents it, or <c>serviceProperties.name</c> beside an <c>overwrite</c> —
    /// or null when it asks for a new layer.
    /// </summary>
    private static JsonObject? Overwrites(string? sent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sent) || JsonNode.Parse(sent) is not JsonObject output)
            {
                return null;
            }

            JsonObject item = output["itemProperties"] as JsonObject ?? [];
            bool overwrite = item["overwrite"]?.GetValueKind() == JsonValueKind.True || output["overwrite"]?.GetValueKind() == JsonValueKind.True;

            if (!overwrite)
            {
                return null;
            }

            return new JsonObject
            {
                ["itemId"] = item["itemId"]?.GetValue<string>(),
                ["serviceURL"] = item["serviceURL"]?.GetValue<string>() ?? item["serviceUrl"]?.GetValue<string>(),
                ["name"] = item["name"]?.GetValue<string>() ?? output["serviceProperties"]?["name"]?.GetValue<string>(),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The hosted service an overwrite names, found by item id, address or name — and refused unless it is one layer,
    /// the caller's or theirs to administer, and not protected.
    /// </summary>
    private static async Task<(PublishedService? Service, string? Why)> OverwrittenAsync(
        JsonObject named, RequestPrincipal current, PostgresLayerCatalog layers, CancellationToken cancellation)
    {
        PublishedService? service = null;

        if (named["itemId"]?.GetValue<string>() is { Length: > 0 } itemId)
        {
            service = (await layers.ListServicesAsync(cancellation).ConfigureAwait(false))
                .FirstOrDefault(s => string.Equals(s.Id.ToString("N"), itemId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        else if (named["serviceURL"]?.GetValue<string>() is { Length: > 0 } url)
        {
            int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
            string[] path = at < 0 ? [] : url[(at + "/rest/services/".Length)..].Split('?')[0].Trim('/').Split('/');
            int face = Array.IndexOf(path, "FeatureServer");

            if (face >= 1)
            {
                service = await layers.FindServiceAsync(face == 2 ? path[0] : null, path[face - 1], cancellation).ConfigureAwait(false);
            }
        }
        else if (named["name"]?.GetValue<string>() is { Length: > 0 } name)
        {
            service = await layers.FindServiceAsync("hosted", name.Trim(), cancellation).ConfigureAwait(false);
        }

        if (service is null || !string.Equals(service.Folder, "hosted", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "outputName asks to overwrite a layer that is not a hosted layer here: name it by itemId, serviceURL or name.");
        }

        if (service.Owner != current.Principal.Id && !current.Authorization.Allows(Privilege.AdminManageAllContent))
        {
            return (null, $"'{service.Name}' is not yours, so a result is not written over it.");
        }

        if (service.Layers.Count != 1 || service.Tables.Count != 0 || service.ViewOf is not null || service.HasViews)
        {
            return (null, $"'{service.Name}' is not a single layer of its own, so a result is not written over it.");
        }

        return (service, null);
    }

    /// <summary>The service name the result is written under, free and valid — or why not.</summary>
    private static async Task<(string? Name, string? Why)> OutputNameAsync(
        string? sent, IAdminCatalog catalog, PostgresLayerCatalog layers, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(sent))
        {
            return (null, "outputName is required here: the result is written as a new hosted feature layer, "
                + "{\"serviceProperties\": {\"name\": \"<name>\"}}. A result returned as a feature collection is not built yet.");
        }

        string? name;

        try
        {
            name = JsonNode.Parse(sent) switch
            {
                JsonObject o when o["serviceProperties"]?["name"] is JsonValue v => v.GetValue<string>(),
                JsonValue v => v.GetValue<string>(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            name = sent;
        }
        catch (NotSupportedException e)
        {
            return (null, e.Message);
        }

        // ADR-222 §5.8 (INFERRED): Map Viewer writes names with spaces; a service name takes none, so they become underscores.
        name = Regex.Replace((name ?? string.Empty).Trim(), @"\s+", "_");

        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$"))
        {
            return (null, $"'{name}' cannot name a layer: letters, digits, _ and -, up to 40, starting with a letter or digit.");
        }

        if (await HostedDataEndpoints.NameTakenAsync(catalog, name, cancellation).ConfigureAwait(false)
            || await layers.FindServiceAsync("hosted", name, cancellation).ConfigureAwait(false) is not null)
        {
            return (null, $"A layer named '{name}' already exists. Choose another output name.");
        }

        return (name, null);
    }

    /// <summary>The extent and output reference a context names — the 10.6.1 object, or the older <c>env:outSR</c>.</summary>
    private static (JsonObject? Extent, int? OutSrid, string? Why) ContextOf(string? sent, bool legacy)
    {
        if (string.IsNullOrWhiteSpace(sent))
        {
            return (null, null, null);
        }

        try
        {
            if (legacy)
            {
                JsonNode? sr = int.TryParse(sent, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wkid)
                    ? JsonValue.Create(wkid)
                    : JsonNode.Parse(sent);
                return (null, Srid(sr), null);
            }

            if (JsonNode.Parse(sent) is not JsonObject context)
            {
                return (null, null, "context is a JSON object: {\"extent\": …, \"outSR\": …}.");
            }

            int? outSrid = context["outSR"] is { } outSr ? Srid(outSr) : null;
            JsonObject? extent = null;

            if (context["extent"] is JsonObject box)
            {
                double[] edges = [.. Edges.Select(k => box[k]?.GetValue<double>() ?? double.NaN)];

                if (edges.Any(e => !double.IsFinite(e)) || edges[0] > edges[2] || edges[1] > edges[3])
                {
                    return (null, null, "context's extent is {\"xmin\", \"ymin\", \"xmax\", \"ymax\", \"spatialReference\"}.");
                }

                extent = new JsonObject
                {
                    ["xmin"] = edges[0], ["ymin"] = edges[1], ["xmax"] = edges[2], ["ymax"] = edges[3],
                    ["srid"] = Srid(box["spatialReference"]) ?? 4326,
                };
            }

            return (extent, outSrid, null);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return (null, null, "context could not be read as JSON.");
        }
    }

    private static readonly string[] Edges = ["xmin", "ymin", "xmax", "ymax"];

    private static int? Srid(JsonNode? sr)
    {
        int? wkid = sr switch
        {
            JsonValue v => v.GetValue<int>(),
            JsonObject o => (o["latestWkid"] ?? o["wkid"])?.GetValue<int>(),
            _ => null,
        };

        // Esri's names for web mercator are EPSG:3857 here, as everywhere this server reads a reference.
        return wkid is 102100 or 102113 or 900913 ? 3857 : wkid;
    }

    // ------------------------------------------------------------------ the job and what it answered

    private static async Task JobAsync(HttpContext context, string task, string jobId, IAnalysisStore runs, CancellationToken cancellation)
    {
        if (await RunOfAsync(context, task, jobId, runs, cancellation).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        JsonObject answer = new()
        {
            ["jobId"] = JobIdOf(run.Job.Id),
            ["jobStatus"] = StatusOf(run),
        };

        if (run.Results is { } results && JsonNode.Parse(results) is JsonObject held)
        {
            answer["results"] = new JsonObject([.. held.Select(r => new KeyValuePair<string, JsonNode?>(
                r.Key, new JsonObject { ["paramUrl"] = $"results/{r.Key}" }))]);
        }

        if (JsonNode.Parse(run.Parameters)?["sent"] is JsonObject sent)
        {
            answer["inputs"] = new JsonObject([.. sent.Select(p => new KeyValuePair<string, JsonNode?>(
                p.Key, new JsonObject { ["paramUrl"] = $"inputs/{p.Key}" }))]);
        }

        bool messages = !string.Equals(Asked(context, "returnMessages"), "false", StringComparison.OrdinalIgnoreCase);
        answer["messages"] = messages ? JsonNode.Parse(run.Messages) : new JsonArray();

        if (run.Job.Status == JobStatus.Running)
        {
            answer["progress"] = new JsonObject { ["type"] = "default", ["message"] = "Executing..." };
        }

        await WriteAsync(context, answer).ConfigureAwait(false);
    }

    private static async Task ResultAsync(HttpContext context, string task, string jobId, string param, IAnalysisStore runs, CancellationToken cancellation)
    {
        if (await RunOfAsync(context, task, jobId, runs, cancellation).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        if (run.Results is null || JsonNode.Parse(run.Results)?[param] is not JsonObject result)
        {
            await RefuseAsync(context, 400, $"The job has no result '{param}'{(run.Job.Status == JobStatus.Done ? string.Empty : " yet: it has not succeeded")}.").ConfigureAwait(false);
            return;
        }

        // The layer's address is stored as a path, and given back on the origin the caller reached this server by.
        if (result["value"]?["url"] is JsonValue path && path.GetValue<string>().StartsWith('/'))
        {
            result["value"]!["url"] = TileFaces.Origin(context) + path.GetValue<string>();
        }

        await WriteAsync(context, result).ConfigureAwait(false);
    }

    private static async Task InputAsync(HttpContext context, string task, string jobId, string param, IAnalysisStore runs, CancellationToken cancellation)
    {
        if (await RunOfAsync(context, task, jobId, runs, cancellation).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        if (JsonNode.Parse(run.Parameters)?["sent"]?[param] is not JsonValue value)
        {
            await RefuseAsync(context, 400, $"The job was not given '{param}'.").ConfigureAwait(false);
            return;
        }

        string? dataType = TaskNamed(task)?.Parameters.FirstOrDefault(p => p.Name == param)?.DataType;
        await WriteAsync(context, new JsonObject { ["paramName"] = param, ["dataType"] = dataType ?? "GPString", ["value"] = value.DeepClone() }).ConfigureAwait(false);
    }

    private static async Task CancelAsync(HttpContext context, string task, string jobId, IAnalysisStore runs, IJobStore jobs, CancellationToken cancellation)
    {
        if (await RunOfAsync(context, task, jobId, runs, cancellation).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        if (run.Job.Status is not (JobStatus.Queued or JobStatus.Running))
        {
            await RefuseAsync(context, 400, $"The job has ended ({StatusOf(run)}); there is nothing to cancel.").ConfigureAwait(false);
            return;
        }

        await runs.AskCancelAsync(run.Job.Id, cancellation).ConfigureAwait(false);

        // A queued run is stopped here; a running one by its runner, which reads the mark.
        if (run.Job.Status == JobStatus.Queued)
        {
            await jobs.FinishAsync(run.Job.Id, JobStatus.Cancelled, null, null, cancellation).ConfigureAwait(false);
        }

        await WriteAsync(context, new { jobId = JobIdOf(run.Job.Id), jobStatus = "esriJobCancelling" }).ConfigureAwait(false);
    }

    /// <summary>The caller's run named by a job id, or a refusal written — another member's is no job (ADR-037).</summary>
    private static async Task<AnalysisRun?> RunOfAsync(HttpContext context, string task, string jobId, IAnalysisStore runs, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (TryJob(jobId, out Guid id)
            && await runs.FindAsync(id, cancellation).ConfigureAwait(false) is { } run
            && string.Equals(run.Task, TaskNamed(task)?.Name, StringComparison.Ordinal)
            && !current.Principal.IsAnonymous
            && (run.Job.Owner == current.Principal.Id || LayerAccess.MayManage(null, current.Principal, current.Authorization)))
        {
            return run;
        }

        await RefuseAsync(context, 400, "Invalid or missing input parameters: there is no such job.").ConfigureAwait(false);
        return null;
    }

    /// <summary>A job's id as the documents write one: <c>j</c> and 32 hex digits.</summary>
    internal static string JobIdOf(Guid id) => "j" + id.ToString("N");

    private static bool TryJob(string jobId, out Guid id) =>
        Guid.TryParseExact(jobId.StartsWith('j') ? jobId[1..] : jobId, "N", out id);

    /// <summary>The documented status word for where a run has got to.</summary>
    internal static string StatusOf(AnalysisRun run) => run.Job.Status switch
    {
        JobStatus.Queued => "esriJobSubmitted",
        JobStatus.Running when run.CancelAsked => "esriJobCancelling",
        JobStatus.Running => "esriJobExecuting",
        JobStatus.Done => "esriJobSucceeded",
        JobStatus.Cancelled => "esriJobCancelled",
        _ => "esriJobFailed",
    };

    // ------------------------------------------------------------------ reading a request, writing an answer

    private static async Task<Func<string, string?>> ParametersOfAsync(HttpContext context, CancellationToken cancellation)
    {
        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : null;

        return name =>
        {
            if (form is not null && form.TryGetValue(name, out StringValues posted) && !StringValues.IsNullOrEmpty(posted))
            {
                return posted.ToString();
            }

            return context.Request.Query.TryGetValue(name, out StringValues query) && !StringValues.IsNullOrEmpty(query)
                ? query.ToString()
                : null;
        };
    }

    private static string? Asked(HttpContext context, string name) =>
        context.Request.Query.TryGetValue(name, out StringValues value) ? value.ToString() : null;

    private static async Task WriteAsync(HttpContext context, object answer)
    {
        bool pretty = string.Equals(Asked(context, "f"), "pjson", StringComparison.OrdinalIgnoreCase);
        await Results.Json(answer, pretty ? Pretty : Compact).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A refusal in the GP error shape: <c>{"error": {"code", "message", "details"}}</c>.</summary>
    private static async Task RefuseAsync(HttpContext context, int code, string message)
    {
        // An ArcGIS client reads the code from the body; the HTTP status says the same where HTTP has the number.
        int status = code is 499 ? 401 : code;
        await Results.Json(new { error = new { code, message, details = new[] { message } } }, statusCode: status)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static double[]? Doubles(string text)
    {
        try
        {
            return JsonNode.Parse(text) switch
            {
                JsonArray a => [.. a.Select(n => n!.GetValue<double>())],
                JsonValue v => [v.GetValue<double>()],
                _ => null,
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException)
        {
            string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            double[] read = new double[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out read[i]))
                {
                    return null;
                }
            }

            return read;
        }
    }

    private static IReadOnlyList<string>? Strings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) is JsonArray a ? [.. a.Select(n => n!.GetValue<string>().Trim()).Where(s => s.Length > 0)] : [text.Trim()];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
    }

    private static string? Choice(string? sent, string fallback, string[] allowed) =>
        string.IsNullOrWhiteSpace(sent)
            ? fallback
            : allowed.FirstOrDefault(a => string.Equals(a, sent.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsNumeric(FieldType type) =>
        type is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Single or FieldType.Double;
}
