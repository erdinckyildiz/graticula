using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Runs a user tool's jobs — ADR-223 §5.3, §5.5 and §5.8: the toolbox's source and each layer input written to the run's
/// directory, grapy's runner started fenced over it, and each feature class it answers published as a hosted layer of the
/// caller's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The job and its lease are an analysis's</b> (<see cref="AnalysisRunner"/>): claimed by kind, kept while the run
/// lasts, its parameters, messages and results in the same table; what differs is that the work is a process this side
/// waits on and can kill, rather than SQL.
/// </para>
/// <para>
/// <b>The directory is the run's own and is removed when the run ends</b>, however it ends; nothing in it is read by
/// anything but this run.
/// </para>
/// </remarks>
internal sealed partial class ToolRunner : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancelCheck = TimeSpan.FromSeconds(2);
    private static readonly string Who = "graticula/tools " + Environment.MachineName + "#" + Environment.ProcessId;

    /// <summary>One run at a time per server: a tool takes its memory and CPU limits whole.</summary>
    private const int Speaks = 1;

    private readonly IJobStore _jobs;
    private readonly IAnalysisStore _runs;
    private readonly JobSignal _signal;
    private readonly IToolboxStore _toolboxes;
    private readonly PostgresLayerCatalog _layers;
    private readonly IAdminCatalog _catalog;
    private readonly ServiceContexts _contexts;
    private readonly PostGisImporter _importer;
    private readonly ToolSettings _settings;
    private readonly HostSettings _host;
    private readonly TimeProvider _clock;
    private readonly ILogger<ToolRunner> _log;
    private readonly LeaseSweep _leases = new();

    public ToolRunner(
        IJobStore jobs,
        IAnalysisStore runs,
        JobSignal signal,
        IToolboxStore toolboxes,
        PostgresLayerCatalog layers,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        PostGisImporter importer,
        ToolSettings settings,
        HostSettings host,
        TimeProvider clock,
        ILogger<ToolRunner> log)
    {
        _jobs = jobs;
        _runs = runs;
        _signal = signal;
        _toolboxes = toolboxes;
        _layers = layers;
        _catalog = catalog;
        _contexts = contexts;
        _importer = importer;
        _settings = settings;
        _host = host;
        _clock = clock;
        _log = log;
    }

    [LoggerMessage(EventId = 2368, Level = LogLevel.Warning, Message = "A tool job could not be claimed: {Reason}")]
    private partial void ClaimFailed(string reason);

    [LoggerMessage(EventId = 2369, Level = LogLevel.Information, Message = "Tool {Task} ran in {Seconds:0.0} s, fenced: {Fence}.")]
    private partial void Ran(string task, double seconds, string fence);

    [LoggerMessage(EventId = 2370, Level = LogLevel.Warning, Message = "Tool {Task} failed: {Reason}")]
    private partial void RunFailed(string task, string reason);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        TimeSpan waiting = Idle;

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.Tool, Who, Speaks, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                ClaimFailed(unreachable.Message);
                await _signal.WaitAsync(JobKind.Tool, Patience, stopping).ConfigureAwait(false);
                continue;
            }

            if (job is null)
            {
                await _leases.SweepAsync(_jobs, _log, stopping).ConfigureAwait(false);
                await _signal.WaitAsync(JobKind.Tool, waiting, stopping).ConfigureAwait(false);
                waiting = waiting < Patience ? waiting * 2 : Patience;
                continue;
            }

            waiting = Idle;

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
        string task = job.Subject ?? "tool";
        string directory = Path.Combine(_host.StatePath, "tool-runs", job.Id.ToString("N"));

        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Task watcher = WatchForCancelAsync(job.Id, stop, watching.Token);

        try
        {
            AnalysisRun run = await _runs.FindAsync(job.Id, stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The run's parameters are gone.");
            JsonObject plan = (JsonObject)JsonNode.Parse(run.Parameters)!;

            string source = await _toolboxes.SourceAsync(plan["toolbox"]!.GetValue<string>(), stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The toolbox was removed after the run was submitted.");

            Directory.CreateDirectory(Path.Combine(directory, "inputs"));
            await File.WriteAllTextAsync(Path.Combine(directory, "toolbox.pyt"), source, stop.Token).ConfigureAwait(false);

            // Each layer input, as GeoJSON in WGS 84 — the only way data reaches the tool (ADR-223 §5.5).
            JsonObject inputs = [];

            foreach ((string name, JsonNode? sent) in plan["inputs"]!.AsObject())
            {
                PublishedLayer layer = await _layers.FindByIdAsync(Guid.Parse(sent!["layer"]!.GetValue<string>()), stop.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"The layer given as {name} was removed after the run was submitted.");
                (_, LayerDescription described) = await _contexts.GetAsync(layer, stop.Token).ConfigureAwait(false);

                if (!WhereClause.TryParse(sent["filter"]?.GetValue<string>(), [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote,
                        out ParsedWhere where, out string? bad, described.Fields.ToDictionary(f => f.Name, f => f.Type)))
                {
                    throw new InvalidOperationException($"The filter on {name} no longer reads against its fields: {bad}");
                }

                string written = Path.Combine("inputs", name + ".geojson");
                int count = await _importer.WriteGeoJsonAsync(
                    new AnalysisInput(layer.Definition, [], where), Path.Combine(directory, written), AnalysisCollections.Limit, stop.Token)
                    .ConfigureAwait(false);

                if (count > AnalysisCollections.Limit)
                {
                    throw new InvalidOperationException(
                        $"{name} holds more than the {AnalysisCollections.Limit:N0} features a tool is given; filter it.");
                }

                inputs[name] = written.Replace('\\', '/');
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "run.json"), new JsonObject
            {
                ["mode"] = "run",
                ["toolbox"] = "toolbox.pyt",
                ["tool"] = plan["tool"]!.GetValue<string>(),
                ["parameters"] = plan["values"]!.DeepClone(),
                ["inputs"] = inputs,
                ["limits"] = ToolSandbox.Limits(_settings),
            }.ToJsonString(), stop.Token).ConfigureAwait(false);

            ToolSandbox.Outcome outcome = await ToolSandbox.RunAsync(directory, _settings, stop.Token).ConfigureAwait(false);

            JsonObject? result = outcome.Result;
            string fence = result?["fence"] is JsonObject fenced
                ? $"Landlock ABI {fenced["landlock"]}, network {fenced["network"]}"
                : _settings.Unfenced ? "not fenced (Graticula:Tools:UnsafeUnfenced)" : "no fence";
            Say(messages, "esriJobMessageTypeInformative", $"Fence: {fence}.");

            foreach (JsonObject said in (result?["messages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                messages.Add((JsonObject)said.DeepClone());
            }

            if (outcome.TimedOut)
            {
                throw new InvalidOperationException($"The tool ran past its {_settings.WallClock.TotalMinutes:0} minutes and was stopped.");
            }

            if (result is null || result["ok"]?.GetValue<bool>() != true)
            {
                string why = result?["error"]?.GetValue<string>()
                    ?? (outcome.ErrorTail.Length > 0 ? outcome.ErrorTail[^Math.Min(400, outcome.ErrorTail.Length)..] : $"the run ended with code {outcome.ExitCode}");
                throw new InvalidOperationException(why);
            }

            // Each output: a layer published as the caller's, or the value the tool set.
            JsonObject results = [];
            JsonObject layers = result["layers"] as JsonObject ?? [];

            foreach (JsonObject output in plan["outputs"]!.AsArray().OfType<JsonObject>())
            {
                string name = output["name"]!.GetValue<string>();
                string dataType = output["dataType"]!.GetValue<string>();

                if (layers[name] is JsonObject layer)
                {
                    results[name] = await PublishAsync(job, name, Path.Combine(directory, layer["geojson"]!.GetValue<string>()), stop.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    results[name] = new JsonObject
                    {
                        ["paramName"] = name,
                        ["dataType"] = dataType,
                        ["value"] = ToolboxEndpoints.ValueOf(result["outputs"]?[name]?.GetValue<string>(), dataType),
                    };
                }
            }

            double seconds = (_clock.GetUtcNow() - started).TotalSeconds;
            Say(messages, "esriJobMessageTypeInformative",
                $"Succeeded at {_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)} (Elapsed Time: {seconds:0.00} seconds)");

            await _runs.RecordAsync(job.Id, results.ToJsonString(), new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Done, null, null, stopping, Who).ConfigureAwait(false);
            Ran(task, seconds, fence);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested && !lease.Lost)
        {
            Say(messages, "esriJobMessageTypeWarning", "Cancelled by the person who submitted it.");
            await _runs.RecordAsync(job.Id, null, new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Cancelled, null, null, stopping, Who).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping or the lease lost: the job stays the store's, for the sweep or the next claim.
        }
        catch (Exception failed)
        {
            string why = failed is Npgsql.PostgresException postgres ? $"The database refused the run: {postgres.MessageText}" : failed.Message;
            RunFailed(task, why);
            Say(messages, "esriJobMessageTypeError", why);
            Say(messages, "esriJobMessageTypeError", $"Failed at {_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)}");
            await _runs.RecordAsync(job.Id, null, new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, stopping, Who).ConfigureAwait(false);
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);

            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Its own end.
            }

            ToolboxEndpoints.TryDelete(directory);
        }
    }

    /// <summary>A feature class the tool answered, as a hosted layer of the caller's, and its result parameter.</summary>
    private async Task<JsonObject> PublishAsync(JobRecord job, string parameter, string geojson, CancellationToken cancellation)
    {
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(geojson, cancellation).ConfigureAwait(false));

        if (!GeoJsonFeatures.TryRead(document.RootElement, ImportLimits.Default, out ImportedDataset? dataset, out string? bad))
        {
            throw new InvalidOperationException($"The tool's {parameter} could not be read back: {bad}");
        }

        string name = ToolboxEndpoints.NameFor($"{parameter}_{job.Id.ToString("N")[..8]}").ToLowerInvariant();
        ImportResult imported = await _importer.ImportAsync(dataset!, name, cancellation).ConfigureAwait(false);
        Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(_catalog, cancellation).ConfigureAwait(false);

        await _catalog.PublishLayerAsync(
            new LayerPublication(
                name,
                datastore,
                imported.SchemaName,
                imported.TableName,
                "geom",
                "objectid",
                "objectid",
                imported.StoredSrid,
                dataset!.GeometryType,
                SharingScope.Private,
                ServiceName: name,
                Folder: "hosted"),
            job.Owner,
            cancellation).ConfigureAwait(false);

        PublishedService service = await _layers.FindServiceAsync("hosted", name, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{name}' was published and could not be found again.");

        return new JsonObject
        {
            ["paramName"] = parameter,
            ["dataType"] = "GPString",
            ["value"] = new JsonObject
            {
                ["url"] = $"/rest/services/{service.QualifiedName}/FeatureServer/0",
                ["itemId"] = PortalEndpoints.ItemIdOf(service, "FeatureServer"),
            },
        };
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

    private static void Say(List<JsonObject> messages, string type, string description) =>
        messages.Add(new JsonObject { ["type"] = type, ["description"] = description });
}
