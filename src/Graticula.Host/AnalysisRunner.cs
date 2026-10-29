using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Runs the Spatial Analysis Service's jobs — ADR-222: one at a time per server, in the datastore, as the tenant that
/// asked, and publishes each result as a new hosted layer owned by the member who asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only what the submission wrote.</b> The input was resolved to a layer id, its fields checked and its output
/// name found free when the job was submitted (<see cref="AnalysisEndpoints.PlanAsync"/>); what can have changed since —
/// the layer removed, the name taken by somebody faster — fails the job with that sentence.
/// </para>
/// <para>
/// <b>A second run replaces its own table and is refused at the publish</b> (<see cref="JobRerun.RefusedByTheStore"/>):
/// the table is named for the job, and the service for the caller's output name, which the first run's publish holds if
/// it got that far.
/// </para>
/// </remarks>
internal sealed partial class AnalysisRunner : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancelCheck = TimeSpan.FromSeconds(2);

    private static readonly string Who = "graticula/analysis " + Environment.MachineName + "#" + Environment.ProcessId;

    private const int Speaks = 1;

    private readonly IJobStore _jobs;
    private readonly IAnalysisStore _runs;
    private readonly JobSignal _signal;
    private readonly PostgresLayerCatalog _layers;
    private readonly IAdminCatalog _catalog;
    private readonly ServiceContexts _contexts;
    private readonly PostGisImporter _importer;
    private readonly ITileCache _tiles;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnalysisRunner> _log;
    private readonly LeaseSweep _leases = new();

    /// <summary>Creates the runner.</summary>
    public AnalysisRunner(
        IJobStore jobs,
        IAnalysisStore runs,
        JobSignal signal,
        PostgresLayerCatalog layers,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        PostGisImporter importer,
        TimeProvider clock,
        ILogger<AnalysisRunner> log,
        ITileCache tiles)
    {
        _tiles = tiles;
        _jobs = jobs;
        _runs = runs;
        _signal = signal;
        _layers = layers;
        _catalog = catalog;
        _contexts = contexts;
        _importer = importer;
        _clock = clock;
        _log = log;
    }

    [LoggerMessage(EventId = 2363, Level = LogLevel.Warning, Message = "An analysis job could not be claimed: {Reason}")]
    private partial void ClaimFailed(string reason);

    [LoggerMessage(EventId = 2364, Level = LogLevel.Information, Message = "Analysis {Task} made '{Output}' with {Rows} feature(s) in {Seconds:0.0} s.")]
    private partial void Made(string task, string output, int rows, double seconds);

    [LoggerMessage(EventId = 2365, Level = LogLevel.Warning, Message = "Analysis {Task} failed: {Reason}")]
    private partial void RunFailed(string task, string reason);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        TimeSpan waiting = Idle;

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.Analysis, Who, Speaks, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                ClaimFailed(unreachable.Message);
                await _signal.WaitAsync(JobKind.Analysis, Patience, stopping).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                await _leases.SweepAsync(_jobs, _log, stopping).ConfigureAwait(false);
                await _signal.WaitAsync(JobKind.Analysis, waiting, stopping).ConfigureAwait(false);
                waiting = waiting < Patience ? waiting * 2 : Patience;
                continue;
            }

            waiting = Idle;

            // ADR-204: a job runs as the tenant it was started in, so what it reads and writes is that tenant's.
            using (Graticula.Platform.Tenancy.TenantScope.EnterJob(job.Tenant))
            {
                await RunAsync(job, stopping).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(_jobs, job.Id, Who, _log, stopping);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Working);

        List<JsonObject> messages = [];
        DateTimeOffset started = _clock.GetUtcNow();
        Say(messages, "esriJobMessageTypeInformative", $"Start Time: {started.ToString("R", CultureInfo.InvariantCulture)}");

        string table = PostGisImporter.AnalysisTableFor(job.Id);
        string task = job.Subject ?? "analysis";

        // The caller's Cancel is a mark in the row; this reads it while the statement runs, and stops the statement.
        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Task watcher = WatchForCancelAsync(job.Id, stop, watching.Token);

        try
        {
            AnalysisRun run = await _runs.FindAsync(job.Id, stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The run's parameters are gone.");

            if (run.CancelAsked)
            {
                throw new OperationCanceledException();
            }

            JsonObject plan = (JsonObject)JsonNode.Parse(run.Parameters)!;
            string? output = plan["output"]?.GetValue<string>();

            AnalysisInput input = await InputAsync(plan, job.Id, 1, run.Task, stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The submission named no input.");
            AnalysisInput? second = await InputAsync(plan, job.Id, 2, run.Task, stop.Token).ConfigureAwait(false);

            ImportResult made = run.Task switch
            {
                "CreateBuffers" => await _importer.BufferAsync(input, BufferOf(plan), table, stop.Token).ConfigureAwait(false),
                "DissolveBoundaries" => await _importer.DissolveAsync(input, DissolveOf(plan), table, stop.Token).ConfigureAwait(false),
                "OverlayLayers" => await _importer.OverlayAsync(input, second!, OverlayOf(plan), table, stop.Token).ConfigureAwait(false),
                "MergeLayers" => await _importer.MergeAsync(input, second!, MergeOf(plan), table, stop.Token).ConfigureAwait(false),

                // The polygons are what the result is made of, so theirs is the reference and the context's outSR applies to them.
                "AggregatePoints" => await _importer.SummarizeAsync(
                    second is null ? null : second with { OutputSrid = input.OutputSrid }, input, AnalysisShape.Points,
                    AggregateOf(plan), table, stop.Token).ConfigureAwait(false),
                "SummarizeWithin" => await _importer.SummarizeAsync(
                    second is null ? null : second with { OutputSrid = input.OutputSrid }, input,
                    (AnalysisShape)plan["summarizedShape"]!.GetValue<int>(), SummarizeOf(plan), table, stop.Token).ConfigureAwait(false),
                "JoinFeatures" => await _importer.JoinAsync(input, second!, JoinOf(plan), table, stop.Token).ConfigureAwait(false),
                "FindCentroids" => await _importer.CentroidsAsync(input, plan["inside"]!.GetValue<bool>(), table, stop.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"'{run.Task}' is not a task this server runs."),
            };

            Say(messages, "esriJobMessageTypeInformative", $"{made.Rows} feature(s) written.");

            GeometryKind kind = (AnalysisShape)plan["shape"]!.GetValue<int>() switch
            {
                AnalysisShape.Points when plan["single"]?.GetValue<bool>() == true => GeometryKind.Point,
                AnalysisShape.Points => GeometryKind.MultiPoint,
                AnalysisShape.Lines => GeometryKind.MultiLineString,
                _ => GeometryKind.MultiPolygon,
            };
            string result = AnalysisEndpoints.Tasks.First(t => t.Name == run.Task).Result;
            JsonObject results;

            if (output is null)
            {
                // ADR-222 condition 4: no name, so the result is returned as a feature collection and nothing is kept.
                if (made.Rows > AnalysisCollections.Limit)
                {
                    throw new InvalidOperationException(
                        $"The result holds {made.Rows:N0} features, more than the {AnalysisCollections.Limit:N0} returned without "
                        + "being saved. Give an outputName to keep it as a hosted layer.");
                }

                AnalysisRows read = await _importer.ReadAnalysisAsync(table, AnalysisCollections.Limit, stop.Token).ConfigureAwait(false);
                AnalysisRows? groups = plan["group"] is null
                    ? null
                    : await _importer.ReadAnalysisAsync(PostGisImporter.GroupTableFor(table), AnalysisCollections.Limit, stop.Token).ConfigureAwait(false);
                await _importer.DropAnalysisAsync(table, CancellationToken.None).ConfigureAwait(false);

                results = new JsonObject
                {
                    [result] = new JsonObject
                    {
                        ["paramName"] = result,
                        ["dataType"] = "GPString",
                        ["value"] = AnalysisCollections.Write(read, kind),
                    },
                };

                if (groups is not null && AnalysisEndpoints.GroupResultOf(run.Task) is { } groupResult)
                {
                    results[groupResult] = new JsonObject
                    {
                        ["paramName"] = groupResult,
                        ["dataType"] = "GPString",
                        ["value"] = AnalysisCollections.Write(groups, null),
                    };
                }
            }
            else
            {
                // A summary is drawn by its count, so the result says something the moment it is opened (design review
                // 2026-10-29: every hexagon one flat colour until somebody restyled it).
                string? counted = run.Task switch
                {
                    "AggregatePoints" => "point_count",
                    "SummarizeWithin" => plan["summary"]?["count"]?.GetValue<string>() ?? plan["summary"]?["measureName"]?.GetValue<string>(),
                    _ => null,
                };
                string? drawn = counted is null
                    ? null
                    : DrawnBy(counted, await _importer.LargestAsync(table, counted, stop.Token).ConfigureAwait(false), kind);

                results = plan["overwrite"]?.GetValue<string>() is { } replaced
                    ? await OverwriteAsync(output, Guid.Parse(replaced), made, result, drawn, stop.Token).ConfigureAwait(false)
                    : await PublishAsync(job, output, made, kind, result, stop.Token, drawn).ConfigureAwait(false);

                // The groups' table, published into the same service as its layer, as ArcGIS's result service holds both.
                if (plan["group"] is not null && AnalysisEndpoints.GroupResultOf(run.Task) is { } groupResult)
                {
                    results[groupResult] = await PublishGroupsAsync(job, output, made, groupResult, stop.Token).ConfigureAwait(false);
                }
            }

            double seconds = (_clock.GetUtcNow() - started).TotalSeconds;
            Say(messages, "esriJobMessageTypeInformative",
                $"Succeeded at {_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)} (Elapsed Time: {seconds:0.00} seconds)");

            await _runs.RecordAsync(job.Id, results.ToJsonString(), new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Done, null, null, stopping, Who).ConfigureAwait(false);
            Made(run.Task, output ?? "(returned)", made.Rows, seconds);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested && !lease.Lost)
        {
            await _importer.DropAnalysisAsync(table, CancellationToken.None).ConfigureAwait(false);
            Say(messages, "esriJobMessageTypeWarning", "Cancelled by the person who submitted it.");
            await _runs.RecordAsync(job.Id, null, new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Cancelled, null, null, stopping, Who).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping, or the lease went to another node: the job is queued again and finished there.
        }
        catch (Exception failed)
        {
            try
            {
                await _importer.DropAnalysisAsync(table, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The table is named for the job; a later run of it replaces whatever is left.
            }

            string why = Plain(failed);
            RunFailed(task, why);
            Say(messages, "esriJobMessageTypeError", why);
            Say(messages, "esriJobMessageTypeError", $"Failed at {_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)}");
            await _runs.RecordAsync(job.Id, null, new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, stopping, Who).ConfigureAwait(false);
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);

            // A collection's table lives as long as its run, however the run ended.
            foreach (int index in (int[])[1, 2])
            {
                try
                {
                    await _importer.DropAnalysisAsync(PostGisImporter.CollectionTableFor(job.Id, index), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception left)
                {
                    RunFailed(task, "a collection's table was left behind: " + left.Message);
                }
            }

            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Its own end.
            }
        }
    }

    /// <summary>
    /// <c>outputName</c>'s overwrite: the run's table in place of the layer's, the layer's drawing the result's, and every
    /// cache of it emptied — the service, its layer and its item stay, as ArcGIS's overwrite keeps the item.
    /// </summary>
    private async Task<JsonObject> OverwriteAsync(
        string output, Guid serviceId, ImportResult made, string result, string? drawn, CancellationToken cancellation)
    {
        PublishedService service = await _layers.FindServiceAsync("hosted", output, cancellation).ConfigureAwait(false) is { } found
            && found.Id == serviceId
                ? found
                : throw new InvalidOperationException($"'{output}' was removed or replaced after the analysis was submitted.");
        PublishedLayer layer = service.Layers[0];

        await _importer.ReplaceWithAnalysisAsync(made.TableName, layer.Definition.TableName, cancellation).ConfigureAwait(false);

        if (drawn is not null)
        {
            await _catalog.SetSymbologyAsync(layer.Id, drawn, cancellation).ConfigureAwait(false);
        }

        _contexts.Forget(layer);
        _tiles.Purge(layer.Id);
        await _catalog.TouchServiceAsync(service.Id, cancellation).ConfigureAwait(false);

        return new JsonObject
        {
            [result] = new JsonObject
            {
                ["paramName"] = result,
                ["dataType"] = "GPString",
                ["value"] = new JsonObject
                {
                    ["url"] = $"/rest/services/{service.QualifiedName}/FeatureServer/0",
                    ["itemId"] = PortalEndpoints.ItemIdOf(service, "FeatureServer"),
                },
            },
        };
    }

    /// <summary>
    /// A light-to-dark ramp, five classes, yellow through orange to red — strong from its first class, since the palest
    /// yellow was invisible on OpenStreetMap's own pale ground (second design review, 2026-10-29).
    /// </summary>
    private static readonly int[][] Ramp =
    [
        [254, 217, 118, 215], [254, 178, 76, 220], [253, 141, 60, 225], [240, 59, 32, 230], [189, 0, 38, 235],
    ];

    /// <summary>
    /// A summary's drawing: up to five equal classes of its count from 1 to the largest, as an Esri class-breaks renderer
    /// read into the canonical document — or null, for the generated appearance, when there is nothing to class.
    /// </summary>
    internal static string? DrawnBy(string column, double? largest, GeometryKind kind)
    {
        if (largest is not { } top || top <= 0)
        {
            return null;
        }

        int classes = (int)Math.Min(5, Math.Ceiling(top));
        JsonArray infos = [];
        double from = 0;

        for (int i = 0; i < classes; i++)
        {
            double to = i == classes - 1 ? top : Math.Ceiling(top * (i + 1) / classes);
            int[] colour = Ramp[i * (Ramp.Length - 1) / Math.Max(1, classes - 1)];

            infos.Add(new JsonObject
            {
                ["classMinValue"] = from,
                ["classMaxValue"] = to,
                ["label"] = from == 0 ? $"up to {to.ToString("0.##", CultureInfo.InvariantCulture)}"
                    : $"{from.ToString("0.##", CultureInfo.InvariantCulture)} to {to.ToString("0.##", CultureInfo.InvariantCulture)}",
                ["symbol"] = new JsonObject
                {
                    ["type"] = "esriSFS",
                    ["style"] = "esriSFSSolid",
                    ["color"] = new JsonArray([.. colour.Select(c => (JsonNode)c)]),
                    ["outline"] = new JsonObject
                    {
                        ["type"] = "esriSLS", ["style"] = "esriSLSSolid", ["color"] = new JsonArray(90, 40, 20, 220), ["width"] = 0.75,
                    },
                },
            });
            from = to;
        }

        JsonObject drawing = new()
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "classBreaks",
                ["field"] = column,
                ["minValue"] = 0,
                ["classBreakInfos"] = infos,
            },
        };

        try
        {
            return SymbologyConversion.Read(drawing.ToJsonString(), kind).Canonical;
        }
        catch (SymbologyException)
        {
            // A drawing this server cannot read back is not worth a failed analysis; the generated one stands in.
            return null;
        }
    }

    /// <summary>A grouped summary's table, published as the result service's table, and its result parameter.</summary>
    private async Task<JsonObject> PublishGroupsAsync(
        JobRecord job, string output, ImportResult made, string groupResult, CancellationToken cancellation)
    {
        Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(_catalog, cancellation).ConfigureAwait(false);
        PublishedLayerAddress address = await _catalog.PublishLayerAsync(
            new LayerPublication(
                AnalysisEndpoints.GroupLayerName(output),
                datastore,
                made.SchemaName,
                PostGisImporter.GroupTableFor(made.TableName),
                null,
                "objectid",
                "objectid",
                made.StoredSrid,
                null,
                SharingScope.Private,
                ServiceName: output,
                Folder: "hosted"),
            job.Owner,
            cancellation).ConfigureAwait(false);

        PublishedService service = await _layers.FindServiceAsync("hosted", output, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{output}' was published and could not be found again.");

        return new JsonObject
        {
            ["paramName"] = groupResult,
            ["dataType"] = "GPString",
            ["value"] = new JsonObject
            {
                ["url"] = $"/rest/services/{service.QualifiedName}/FeatureServer/{address.LayerIndex.ToString(CultureInfo.InvariantCulture)}",
                ["itemId"] = PortalEndpoints.ItemIdOf(service, "FeatureServer"),
            },
        };
    }

    /// <summary>ADR-222 §5.6: the result as a new service of the caller's, private, under the name they gave.</summary>
    private async Task<JsonObject> PublishAsync(
        JobRecord job, string output, ImportResult made, GeometryKind kind, string result, CancellationToken cancellation,
        string? symbology = null)
    {
        Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(_catalog, cancellation).ConfigureAwait(false);

        // ADR-222 §5.6: a new service of the caller's, private, under the name they gave.
        await _catalog.PublishLayerAsync(
            new LayerPublication(
                output,
                datastore,
                made.SchemaName,
                made.TableName,
                "geom",
                "objectid",
                "objectid",
                made.StoredSrid,
                kind,
                SharingScope.Private,
                ServiceName: output,
                Folder: "hosted",
                Symbology: symbology),
            job.Owner,
            cancellation).ConfigureAwait(false);

        PublishedService service = await _layers.FindServiceAsync("hosted", output, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{output}' was published and could not be found again.");

        return new JsonObject
        {
            [result] = new JsonObject
            {
                ["paramName"] = result,
                ["dataType"] = "GPString",
                ["value"] = new JsonObject
                {
                    // A path, given its origin when it is read (AnalysisEndpoints.ResultAsync).
                    ["url"] = $"/rest/services/{service.QualifiedName}/FeatureServer/0",
                    ["itemId"] = PortalEndpoints.ItemIdOf(service, "FeatureServer"),
                },
            },
        };
    }

    /// <summary>
    /// The run's <paramref name="index"/>th input: a hosted layer found again by id, or a feature collection written to
    /// the run's own table — ADR-222 condition 4 — or null when the plan has none.
    /// </summary>
    private async Task<AnalysisInput?> InputAsync(JsonObject plan, Guid job, int index, string task, CancellationToken cancellation)
    {
        string suffix = index == 1 ? string.Empty : "2";
        string filter = "filter" + suffix;

        if (plan["collection" + suffix] is JsonObject sent)
        {
            if (!AnalysisCollections.TryRead(sent, "the collection", out AnalysisCollections.Collection? collection, out string? bad)
                || !AnalysisCollections.TryRows(collection!, "the collection", out IReadOnlyList<CollectionRow> rows, out bad))
            {
                throw new InvalidOperationException(bad);
            }

            string table = PostGisImporter.CollectionTableFor(job, index);
            await _importer.WriteCollectionAsync(table, [.. collection!.Columns.Select(c => c.Column)], collection.Srid, rows, cancellation)
                .ConfigureAwait(false);

            return InputOf(plan, AnalysisCollections.LayerOf(collection, table), collection.Described, filter, task);
        }

        if (plan["layer" + suffix]?.GetValue<string>() is not { } id)
        {
            return null;
        }

        PublishedLayer layer = await _layers.FindByIdAsync(Guid.Parse(id), cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The {(index == 1 ? "input" : "second input")} layer was removed after the analysis was submitted.");

        (_, LayerDescription described) = await _contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
        return InputOf(plan, layer, described, filter, task);
    }

    private async Task WatchForCancelAsync(Guid job, CancellationTokenSource stop, CancellationToken watching)
    {
        while (!watching.IsCancellationRequested)
        {
            await Task.Delay(CancelCheck, _clock, watching).ConfigureAwait(false);

            if (await _runs.FindAsync(job, watching).ConfigureAwait(false) is { CancelAsked: true })
            {
                await stop.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>The layer, the columns it keeps, its filter parsed again, and the context.</summary>
    private static AnalysisInput InputOf(JsonObject plan, PublishedLayer layer, LayerDescription described, string filter, string task)
    {
        IReadOnlyList<string> columns = AnalysisEndpoints.CarriedColumns(layer, described, task);

        if (!WhereClause.TryParse(
                plan[filter]?.GetValue<string>(), [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote,
                out ParsedWhere where, out string? bad, described.Fields.ToDictionary(f => f.Name, f => f.Type)))
        {
            throw new InvalidOperationException($"The input layer's filter no longer reads against its fields: {bad}");
        }

        AnalysisExtent? extent = plan["extent"] is JsonObject box
            ? new AnalysisExtent(
                box["xmin"]!.GetValue<double>(), box["ymin"]!.GetValue<double>(),
                box["xmax"]!.GetValue<double>(), box["ymax"]!.GetValue<double>(), box["srid"]!.GetValue<int>())
            : null;

        return new AnalysisInput(layer.Definition, columns, where, extent, plan["outSrid"]?.GetValue<int>());
    }

    private static BufferPlan BufferOf(JsonObject plan)
    {
        string units = plan["units"]!.GetValue<string>();

        // AnalysisArea follows the unit system, as the task documents: square miles for imperial units, square kilometres otherwise.
        bool imperial = units is "Miles" or "Feet" or "Yards" or "NauticalMiles";

        return new BufferPlan(
            plan["distances"] is JsonArray distances ? [.. distances.Select(d => d!.GetValue<double>())] : null,
            plan["field"]?.GetValue<string>(),
            AnalysisEndpoints.Units[units],
            plan["dissolve"]!.GetValue<bool>(),
            plan["rings"]!.GetValue<bool>(),
            imperial ? 2_589_988.110336 : 1_000_000)
        {
            Side = plan["side"]?.GetValue<string>() ?? "Full",
            FlatEnds = plan["flat"]?.GetValue<bool>() == true,
        };
    }

    private static DissolvePlan DissolveOf(JsonObject plan) => new(
        [.. plan["dissolveFields"]!.AsArray().Select(f => f!.GetValue<string>())],
        [.. plan["summaries"]!.AsArray().Select(s => (
            s!["field"]!.GetValue<string>(), s["statistic"]!.GetValue<string>(), s["output"]!.GetValue<string>()))],
        plan["multiPart"]!.GetValue<bool>());

    private static IReadOnlyList<(string Source, string Output)> Pairs(JsonNode? node) =>
        [.. (node?.AsArray() ?? []).Select(c => (c!["source"]!.GetValue<string>(), c["output"]!.GetValue<string>()))];

    private static OverlayPlan OverlayOf(JsonObject plan) => new(
        plan["overlayType"]!.GetValue<string>(),
        (AnalysisShape)plan["shape"]!.GetValue<int>(),
        Pairs(plan["overlayColumns"]))
    {
        Linework = (plan["outputType"]?.GetValue<string>() ?? "Input") != "Input",
        Tolerance = plan["tolerance"]?.GetValue<double>() ?? 0,
        SnapToInput = plan["snapToInput"]?.GetValue<bool>() == true,
    };

    private static MergePlan MergeOf(JsonObject plan) => new(
        Pairs(plan["inputColumns"]),
        Pairs(plan["mergeColumns"]),
        Pairs(plan["mergeOnly"]),
        (AnalysisShape)plan["shape"]!.GetValue<int>());

    private static IReadOnlyList<(string Column, string Statistic, string Output)> SummariesOf(JsonObject plan) =>
        [.. (plan["summaries"]?.AsArray() ?? []).Select(s => (
            s!["field"]!.GetValue<string>(), s["statistic"]!.GetValue<string>(), s["output"]!.GetValue<string>()))];

    private static BinPlan? BinsOf(JsonObject plan) =>
        plan["bins"] is JsonObject bins ? new BinPlan(bins["hexagon"]!.GetValue<bool>(), bins["metres"]!.GetValue<double>()) : null;

    /// <summary>AggregatePoints as SummarizeWithin's case of points: a count named as ArcGIS names it, nothing weighted.</summary>
    private static SummaryPlan AggregateOf(JsonObject plan) => new(
        "point_count", SummaryMeasure.None, null, 1, SummariesOf(plan), plan["keepEmpty"]!.GetValue<bool>(), BinsOf(plan), Weighted: false,
        GroupOf(plan));

    private static GroupPlan? GroupOf(JsonObject plan) =>
        plan["group"] is JsonObject group
            ? new GroupPlan(
                group["column"]!.GetValue<string>(),
                group["majority"]?.GetValue<string>(),
                group["minority"]?.GetValue<string>(),
                group["percent"]?.GetValue<string>())
            : null;

    private static SummaryPlan SummarizeOf(JsonObject plan)
    {
        JsonObject summary = plan["summary"]!.AsObject();

        return new SummaryPlan(
            summary["count"]?.GetValue<string>(),
            (SummaryMeasure)summary["measure"]!.GetValue<int>(),
            summary["measureName"]?.GetValue<string>(),
            summary["perUnit"]!.GetValue<double>(),
            SummariesOf(plan),
            summary["keepEmpty"]!.GetValue<bool>(),
            BinsOf(plan),
            summary["weighted"]!.GetValue<bool>(),
            GroupOf(plan));
    }

    private static JoinPlan JoinOf(JsonObject plan)
    {
        JsonObject join = plan["join"]!.AsObject();

        return new JoinPlan(
            join["relationship"]?.GetValue<string>(),
            join["metres"]!.GetValue<double>(),
            [.. join["attributes"]!.AsArray().Select(a => (a!["target"]!.GetValue<string>(), a["join"]!.GetValue<string>()))],
            join["oneToMany"]!.GetValue<bool>(),
            join["keepAll"]!.GetValue<bool>(),
            Pairs(join["columns"]),
            join["count"]!.GetValue<string>(),
            SummariesOf(plan),
            [.. join["orderBy"]!.AsArray().Select(o => (o!["field"]!.GetValue<string>(), o["descending"]!.GetValue<bool>()))],
            (AnalysisShape)plan["shape"]!.GetValue<int>(),
            plan["single"]?.GetValue<bool>() == true);
    }

    private static void Say(List<JsonObject> messages, string type, string description) =>
        messages.Add(new JsonObject { ["type"] = type, ["description"] = description });

    /// <summary>What went wrong, in a sentence for the person who asked rather than a stack.</summary>
    private static string Plain(Exception failed) => failed switch
    {
        Npgsql.PostgresException postgres => $"The database refused the analysis: {postgres.MessageText}",
        _ => failed.Message,
    };
}
