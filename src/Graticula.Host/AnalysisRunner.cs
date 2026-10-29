using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
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
        ILogger<AnalysisRunner> log)
    {
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
            string output = plan["output"]!.GetValue<string>();

            PublishedLayer layer = await _layers.FindByIdAsync(Guid.Parse(plan["layer"]!.GetValue<string>()), stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The input layer was removed after the analysis was submitted.");

            (_, LayerDescription described) = await _contexts.GetAsync(layer, stop.Token).ConfigureAwait(false);
            AnalysisInput input = InputOf(plan, layer, described);

            ImportResult made = run.Task switch
            {
                "CreateBuffers" => await _importer.BufferAsync(input, BufferOf(plan), table, stop.Token).ConfigureAwait(false),
                "DissolveBoundaries" => await _importer.DissolveAsync(input, DissolveOf(plan), table, stop.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"'{run.Task}' is not a task this server runs."),
            };

            Say(messages, "esriJobMessageTypeInformative", $"{made.Rows} feature(s) written.");

            Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(_catalog, stop.Token).ConfigureAwait(false);

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
                    GeometryKind.MultiPolygon,
                    SharingScope.Private,
                    ServiceName: output,
                    Folder: "hosted"),
                job.Owner,
                stop.Token).ConfigureAwait(false);

            PublishedService service = await _layers.FindServiceAsync("hosted", output, stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"'{output}' was published and could not be found again.");

            string result = run.Task == "CreateBuffers" ? "bufferLayer" : "dissolvedLayer";
            JsonObject results = new()
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

            double seconds = (_clock.GetUtcNow() - started).TotalSeconds;
            Say(messages, "esriJobMessageTypeInformative",
                $"Succeeded at {_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)} (Elapsed Time: {seconds:0.00} seconds)");

            await _runs.RecordAsync(job.Id, results.ToJsonString(), new JsonArray([.. messages]).ToJsonString(), stopping).ConfigureAwait(false);
            await _jobs.FinishAsync(job.Id, JobStatus.Done, null, null, stopping, Who).ConfigureAwait(false);
            Made(run.Task, output, made.Rows, seconds);
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
    private static AnalysisInput InputOf(JsonObject plan, PublishedLayer layer, LayerDescription described)
    {
        string identity = layer.Definition.IntegerIdentityColumn ?? layer.Definition.IdentityColumn;
        string[] reserved = ["buff_dist", "orig_fid", "analysisarea", "geom", "objectid", "count"];

        // Every attribute but the row's own id and its GlobalID, which the result has afresh.
        string[] columns = [.. described.Fields
            .Where(f => f.Type != FieldType.Guid
                && !string.Equals(f.Name, identity, StringComparison.OrdinalIgnoreCase)
                && !reserved.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Name)];

        if (!WhereClause.TryParse(
                plan["filter"]?.GetValue<string>(), [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote,
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
            imperial ? 2_589_988.110336 : 1_000_000);
    }

    private static DissolvePlan DissolveOf(JsonObject plan) => new(
        [.. plan["dissolveFields"]!.AsArray().Select(f => f!.GetValue<string>())],
        [.. plan["summaries"]!.AsArray().Select(s => (
            s!["field"]!.GetValue<string>(), s["statistic"]!.GetValue<string>(), s["output"]!.GetValue<string>()))],
        plan["multiPart"]!.GetValue<bool>());

    private static void Say(List<JsonObject> messages, string type, string description) =>
        messages.Add(new JsonObject { ["type"] = type, ["description"] = description });

    /// <summary>What went wrong, in a sentence for the person who asked rather than a stack.</summary>
    private static string Plain(Exception failed) => failed switch
    {
        Npgsql.PostgresException postgres => $"The database refused the analysis: {postgres.MessageText}",
        _ => failed.Message,
    };
}
