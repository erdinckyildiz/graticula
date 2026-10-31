using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Python toolboxes: published by a member who holds <c>content:publishTools</c>, served each as a GPServer at
/// <c>/rest/services/Tools/{name}/GPServer</c> with a task per tool — ADR-223 §5.7 and §5.8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publishing reads the toolbox by running it</b> — fenced, in describe mode — because a Python toolbox says what its
/// tools take only when <c>getParameterInfo</c> runs. A toolbox that cannot be described is refused with what the run
/// said, and nothing is stored.
/// </para>
/// <para>
/// <b>Who may run a tool is who may read its item</b>, as with every item here (ADR-018); a run is the caller's, and its
/// job is answered by <see cref="AnalysisEndpoints"/>'s job resources, so the GP shapes are one implementation.
/// </para>
/// </remarks>
internal static partial class ToolboxEndpoints
{
    /// <summary>The services directory folder toolboxes are listed under.</summary>
    public const string Folder = "Tools";

    private const int LargestSource = 512 * 1024;

    /// <summary>Maps the routes.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/content/toolboxes", PublishAsync).DisableAntiforgery();
        app.MapGet("/content/toolboxes", ListAsync);
        app.MapPut("/content/toolboxes/{name}/sharing", ShareAsync);
        app.MapDelete("/content/toolboxes/{name}", DeleteAsync);

        const string root = "/rest/services/" + Folder + "/{name}/GPServer";

        // ADR-018 condition 5: a toolbox and its tasks are its item's to read; a run is its owner's and an administrator's.
        app.MapMethods(root, ["GET", "POST"], ServiceAsync).Governed(SharingGovernedExtensions.ByService);
        app.MapMethods(root + "/{task}", ["GET", "POST"], TaskAsync).Governed(SharingGovernedExtensions.ByService);
        app.MapMethods(root + "/{task}/submitJob", ["GET", "POST"], SubmitAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods(root + "/{task}/execute", ["GET", "POST"], ExecuteAsync)
            .Governed(SharingGovernedExtensions.Public).DisableAntiforgery();
        app.MapMethods(root + "/{task}/jobs/{jobId}", ["GET", "POST"], JobAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapMethods(root + "/{task}/jobs/{jobId}/results/{param}", ["GET", "POST"], ResultAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapMethods(root + "/{task}/jobs/{jobId}/cancel", ["GET", "POST"], CancelAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    // ------------------------------------------------------------------ publishing

    private static async Task PublishAsync(
        HttpContext context, IToolboxStore toolboxes, ToolSettings settings, HostSettings host, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTools).ConfigureAwait(false))
        {
            return;
        }

        if (settings.Refusal is { } cannot)
        {
            await AnalysisEndpoints.RefuseAsync(context, 409, cannot).ConfigureAwait(false);
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (form.Files.GetFile("file") is not { } file || file.Length == 0)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "Send the toolbox as the file 'file': one .pyt or .py.").ConfigureAwait(false);
            return;
        }

        string extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (extension is not (".pyt" or ".py") || file.Length > LargestSource)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400,
                $"A toolbox is one .pyt or .py file of at most {LargestSource / 1024} KB; '{file.FileName}' is not.").ConfigureAwait(false);
            return;
        }

        string source;

        using (StreamReader reader = new(file.OpenReadStream(), new UTF8Encoding(false, throwOnInvalidBytes: true)))
        {
            try
            {
                source = await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
            }
            catch (DecoderFallbackException)
            {
                await AnalysisEndpoints.RefuseAsync(context, 400, "A toolbox is Python source in UTF-8; the file is not.").ConfigureAwait(false);
                return;
            }
        }

        // A name typed is used as typed or refused; only one taken from the file's name is made into a name (design review:
        // "bad name!" was published as bad_name_ without a word).
        string typedName = form["name"].ToString().Trim();

        if (typedName.Length > 0 && !ServiceName().IsMatch(typedName))
        {
            await AnalysisEndpoints.RefuseAsync(context, 400,
                $"'{typedName}' cannot name a toolbox: letters, digits, _ and -, up to 40, starting with a letter or digit.").ConfigureAwait(false);
            return;
        }

        string name = typedName.Length > 0 ? typedName : NameFor(Path.GetFileNameWithoutExtension(file.FileName));
        string title = form["title"].ToString() is { Length: > 0 } titled ? titled.Trim() : name;

        if (await toolboxes.FindByNameAsync(name, cancellation).ConfigureAwait(false) is not null)
        {
            await AnalysisEndpoints.RefuseAsync(context, 409, $"A toolbox named '{name}' already exists. Choose another name.").ConfigureAwait(false);
            return;
        }

        // Described by running it, fenced, in a directory of its own that is removed afterwards.
        string directory = Path.Combine(host.StatePath, "tool-runs", "describe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ToolSandbox.Outcome described;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "toolbox.pyt"), source, cancellation).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "run.json"), new JsonObject
            {
                ["mode"] = "describe",
                ["toolbox"] = "toolbox.pyt",
                ["limits"] = ToolSandbox.Limits(settings),
            }.ToJsonString(), cancellation).ConfigureAwait(false);

            described = await ToolSandbox.RunAsync(directory, settings with { WallClock = TimeSpan.FromMinutes(1) }, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(directory);
        }

        if (described.Result is not { } result || result["ok"]?.GetValue<bool>() != true || result["tools"] is not JsonArray tools)
        {
            string why = described.Result?["error"]?.GetValue<string>()
                ?? (described.TimedOut ? "describing it took longer than a minute" : $"the run ended with code {described.ExitCode}");
            await AnalysisEndpoints.RefuseAsync(context, 400, $"The toolbox could not be read: {why}").ConfigureAwait(false);
            return;
        }

        if (tools.Count == 0)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "The toolbox names no tools in Toolbox.tools.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Toolbox made = await toolboxes.CreateAsync(name, title, current.Principal.Id, file.FileName, source, tools.ToJsonString(), cancellation)
            .ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(
            context, audit, "toolbox.publish", name, AdminEndpoints.Detail(new { id = made.Id, tools = tools.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(context, made, current), statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListAsync(HttpContext context, IToolboxStore toolboxes, ToolSettings settings, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        IReadOnlyList<Toolbox> all = await toolboxes.ListAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            toolboxes = all.Where(t => MayRun(t, current)).Select(t => Describe(context, t, current)),
            mayPublish = current.Authorization.Allows(Privilege.ContentPublishTools),

            // Whether a run here is fenced, so a page promises a sandbox only where there is one (design review).
            fenced = settings.Refusal is null && !settings.Unfenced,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ShareAsync(
        HttpContext context, string name, IToolboxStore toolboxes, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedAsync(context, name, toolboxes, cancellation).ConfigureAwait(false) is not { } toolbox)
        {
            return;
        }

        JsonObject? sent = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellation).ConfigureAwait(false) as JsonObject;
        // Every scope an item takes, as a document's sharing reads them; the groups of a group-shared toolbox are the
        // item's own, set where every item's groups are set.
        SharingScope? sharing = sent?["sharing"]?.GetValue<string>()?.Trim().ToLowerInvariant() switch
        {
            "private" => SharingScope.Private,
            "group" or "shared" => SharingScope.Group,
            "organization" or "org" => SharingScope.Organization,
            "allorganizations" => SharingScope.AllOrganizations,
            "public" => SharingScope.Public,
            _ => null,
        };

        if (sharing is null)
        {
            await AnalysisEndpoints.RefuseAsync(context, 400, "sharing is private, group, organization, allorganizations or public.")
                .ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Privilege? needed = sharing switch
        {
            SharingScope.Organization => Privilege.SharingShareToOrganization,
            SharingScope.AllOrganizations => Privilege.SharingShareToAllOrganizations,
            SharingScope.Public => Privilege.SharingShareToPublic,
            _ => null,
        };

        if (needed is { } privilege && !current.Authorization.Allows(privilege))
        {
            await AnalysisEndpoints.RefuseAsync(context, 403, $"Your role may not share this widely ({Roles.NameOf(privilege)}).")
                .ConfigureAwait(false);
            return;
        }

        await toolboxes.ShareAsync(toolbox.Id, sharing.Value, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "toolbox.share", name, AdminEndpoints.Detail(new { sharing = sent!["sharing"]!.ToString() }),
            succeeded: true, cancellation).ConfigureAwait(false);
        await Results.Json(new { name, sharing = sent!["sharing"]!.ToString() }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a toolbox into the recycle bin — ADR-203 (Q-180), D-346: its item and, by the walk of the store's own
    /// foreign keys, the <c>toolbox</c> row that holds its source, so a restore brings the GPServer back under the
    /// same item id and name.
    /// </summary>
    private static async Task DeleteAsync(
        HttpContext context, string name, IToolboxStore toolboxes, IAuditLog audit, RecycleBinService recycle,
        CancellationToken cancellation)
    {
        if (await ManagedAsync(context, name, toolboxes, cancellation).ConfigureAwait(false) is not { } toolbox)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Guid id = Guid.Parse(toolbox.Id);
        (Guid? entry, string? refused) = await recycle.RecycleRowsAsync(
            "item", id, id, "toolbox", toolbox.Title, toolbox.Owner, current.Principal.Id, cancellation).ConfigureAwait(false);
        bool gone = entry is not null;

        await AdminEndpoints.AuditAsync(context, audit, "toolbox.delete", name,
            AdminEndpoints.Detail(new { id = toolbox.Id, recycled = entry }), succeeded: gone, cancellation).ConfigureAwait(false);

        if (!gone)
        {
            await AnalysisEndpoints.RefuseAsync(context, 409, refused ?? "It could not be set aside, and nothing was deleted.")
                .ConfigureAwait(false);
            return;
        }

        await Results.Json(new { name, deleted = true, recycled = entry, until = DateTimeOffset.UtcNow + recycle.Retention })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the GPServer

    private static async Task ServiceAsync(HttpContext context, string name, IToolboxStore toolboxes, CancellationToken cancellation)
    {
        if (await ReadableAsync(context, name, toolboxes, cancellation).ConfigureAwait(false) is not { } toolbox)
        {
            return;
        }

        await AnalysisEndpoints.WriteAsync(context, new
        {
            currentVersion = 11.3,
            serviceDescription = toolbox.Title,
            tasks = ToolsOf(toolbox).Select(t => t["name"]!.GetValue<string>()),
            executionType = "esriExecutionTypeAsynchronous",
            resultMapServerName = string.Empty,
            maximumRecords = AnalysisCollections.Limit,
        }).ConfigureAwait(false);
    }

    private static async Task TaskAsync(HttpContext context, string name, string task, IToolboxStore toolboxes, CancellationToken cancellation)
    {
        if (await ToolAsync(context, name, task, toolboxes, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        JsonObject tool = found.Tool;
        await AnalysisEndpoints.WriteAsync(context, new JsonObject
        {
            ["name"] = tool["name"]!.GetValue<string>(),
            ["displayName"] = tool["label"]?.GetValue<string>() ?? tool["name"]!.GetValue<string>(),
            ["description"] = tool["description"]?.GetValue<string>() ?? string.Empty,
            ["category"] = string.Empty,
            ["helpUrl"] = string.Empty,
            ["executionType"] = "esriExecutionTypeAsynchronous",
            ["parameters"] = new JsonArray([.. ParametersOf(tool).Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p["name"]!.GetValue<string>(),
                ["dataType"] = DataTypeOf(p),
                ["displayName"] = p["displayName"]?.GetValue<string>() ?? p["name"]!.GetValue<string>(),
                ["description"] = string.Empty,
                ["direction"] = IsOutput(p) ? "esriGPParameterDirectionOutput" : "esriGPParameterDirectionInput",
                ["defaultValue"] = p["defaultValue"]?.DeepClone(),
                ["parameterType"] = p["parameterType"]?.GetValue<string>() switch
                {
                    "Optional" => "esriGPParameterTypeOptional",
                    "Derived" => "esriGPParameterTypeDerived",
                    _ => "esriGPParameterTypeRequired",
                },
                ["category"] = string.Empty,
                ["choiceList"] = p["choiceList"]?.DeepClone(),
            })]),
        }).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(HttpContext context, string name, string task) =>
        await AnalysisEndpoints.RefuseAsync(context, 400,
            $"The task '{task}' is asynchronous: run it with submitJob and read its results from the job.").ConfigureAwait(false);

    private static async Task SubmitAsync(
        HttpContext context,
        string name,
        string task,
        IToolboxStore toolboxes,
        PostgresLayerCatalog layers,
        IAnalysisStore runs,
        JobSignal signal,
        ToolSettings settings,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ToolAsync(context, name, task, toolboxes, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await AnalysisEndpoints.RefuseAsync(context, 499, "Sign in to run a tool: its run and its results are yours.").ConfigureAwait(false);
            return;
        }

        if (settings.Refusal is { } cannot)
        {
            await AnalysisEndpoints.RefuseAsync(context, 409, cannot).ConfigureAwait(false);
            return;
        }

        Func<string, string?> asked = await AnalysisEndpoints.ParametersOfAsync(context, cancellation).ConfigureAwait(false);
        JsonObject values = [];
        JsonObject inputs = [];
        JsonObject sent = [];
        JsonArray outputs = [];

        foreach (JsonObject parameter in ParametersOf(found.Tool))
        {
            string parameterName = parameter["name"]!.GetValue<string>();
            string dataType = DataTypeOf(parameter);

            if (IsOutput(parameter))
            {
                // A raster output becomes an image service and a feature output a hosted layer, which the upload doors ask
                // this privilege for; the tool's door asks the same, before the run (security review 2026-10-30, and for
                // a feature output by owner decision 2026-10-31, D-325 (b)).
                if (dataType is "GPRasterDataLayer" or "GPFeatureRecordSetLayer" && !current.Authorization.Allows(Privilege.ContentPublishFeatures))
                {
                    string becomes = dataType == "GPRasterDataLayer" ? "an image service" : "a hosted layer";
                    await AnalysisEndpoints.RefuseAsync(context, 403,
                        $"{parameterName} is published as {becomes}, which takes the privilege content:publishFeatures; ask an administrator for it.")
                        .ConfigureAwait(false);
                    return;
                }

                // A feature output's value, when sent, is the name its layer is published under — what a web tool's
                // output name is in ArcGIS (design review); checked now so a run does not fail at its end on it. A raster
                // output's names its image service the same way (D-324).
                string? layerName = dataType is "GPFeatureRecordSetLayer" or "GPRasterDataLayer" ? asked(parameterName)?.Trim() : null;

                if (!string.IsNullOrEmpty(layerName))
                {
                    if (!ServiceName().IsMatch(layerName))
                    {
                        await AnalysisEndpoints.RefuseAsync(context, 400,
                            $"'{layerName}' cannot name {parameterName}'s layer: letters, digits, _ and -, up to 40, starting with a letter or digit.")
                            .ConfigureAwait(false);
                        return;
                    }

                    if (await layers.FindServiceAsync("hosted", layerName, cancellation).ConfigureAwait(false) is not null
                        || (dataType == "GPRasterDataLayer"
                            && context.RequestServices.GetService(typeof(ICoverageCatalog)) is ICoverageCatalog coverages
                            && await coverages.FindAsync("hosted", layerName, cancellation).ConfigureAwait(false) is not null))
                    {
                        await AnalysisEndpoints.RefuseAsync(context, 400,
                            $"A layer named '{layerName}' already exists. Choose another name for {parameterName}.").ConfigureAwait(false);
                        return;
                    }
                }

                outputs.Add(new JsonObject { ["name"] = parameterName, ["dataType"] = dataType, ["layerName"] = layerName });
                continue;
            }

            string? value = asked(parameterName);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter["parameterType"]?.GetValue<string>() is not ("Optional" or "Derived") && parameter["defaultValue"] is null)
                {
                    await AnalysisEndpoints.RefuseAsync(context, 400, $"{parameterName} is required.").ConfigureAwait(false);
                    return;
                }

                continue;
            }

            sent[parameterName] = value;

            if (dataType == "GPRasterDataLayer")
            {
                (PublishedCoverage? coverage, string? refused) = await InputCoverageAsync(context, parameterName, value, current, cancellation)
                    .ConfigureAwait(false);

                if (coverage is null)
                {
                    await AnalysisEndpoints.RefuseAsync(context, 400, refused!).ConfigureAwait(false);
                    return;
                }

                inputs[parameterName] = new JsonObject { ["coverage"] = coverage.ServiceName, ["folder"] = coverage.Folder };
            }
            else if (dataType == "GPFeatureRecordSetLayer")
            {
                (PublishedLayer? layer, string? filter, string? why, JsonObject? collection, _) =
                    await AnalysisEndpoints.InputLayerAsync(parameterName, value, current, layers, cancellation).ConfigureAwait(false);

                if (layer is null || collection is not null)
                {
                    await AnalysisEndpoints.RefuseAsync(context, 400,
                        why ?? $"{parameterName} names a hosted layer of this server by its url; a feature collection is not taken by a tool yet.")
                        .ConfigureAwait(false);
                    return;
                }

                inputs[parameterName] = new JsonObject { ["layer"] = layer.Id.ToString(), ["filter"] = filter };
            }
            else
            {
                values[parameterName] = Literal(value, dataType);
            }
        }

        JsonObject plan = new()
        {
            ["toolbox"] = found.Toolbox.Id,
            ["name"] = found.Toolbox.Name,
            ["tool"] = found.Tool["name"]!.GetValue<string>(),
            ["values"] = values,
            ["inputs"] = inputs,
            ["outputs"] = outputs,
            ["sent"] = sent,

            // Who published what the run answers, as the audit log names them (D-325 (c)).
            ["by"] = current.Principal.Name,
            ["from"] = CallerAddress.Of(context)?.ToString(),
        };

        AnalysisRun run = await runs.CreateAsync(
            current.Principal.Id, TaskOf(found.Toolbox, found.Tool), plan.ToJsonString(), cancellation, JobKind.Tool).ConfigureAwait(false);
        signal.Wake(JobKind.Tool);

        await AdminEndpoints.AuditAsync(
            context, audit, "toolbox.run", found.Toolbox.Name, AdminEndpoints.Detail(new { tool = plan["tool"]!.ToString(), job = run.Job.Id }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await AnalysisEndpoints.WriteAsync(context, new { jobId = AnalysisEndpoints.JobIdOf(run.Job.Id), jobStatus = "esriJobSubmitted" })
            .ConfigureAwait(false);
    }

    private static async Task JobAsync(
        HttpContext context, string name, string task, string jobId, IToolboxStore toolboxes, IAnalysisStore runs, CancellationToken cancellation)
    {
        if (await RunAsync(context, name, task, jobId, toolboxes, runs, cancellation).ConfigureAwait(false) is { } run)
        {
            await AnalysisEndpoints.JobAnswerAsync(context, run).ConfigureAwait(false);
        }
    }

    private static async Task ResultAsync(
        HttpContext context, string name, string task, string jobId, string param, IToolboxStore toolboxes, IAnalysisStore runs,
        CancellationToken cancellation)
    {
        if (await RunAsync(context, name, task, jobId, toolboxes, runs, cancellation).ConfigureAwait(false) is { } run)
        {
            await AnalysisEndpoints.ResultAnswerAsync(context, run, param).ConfigureAwait(false);
        }
    }

    private static async Task CancelAsync(
        HttpContext context, string name, string task, string jobId, IToolboxStore toolboxes, IAnalysisStore runs, IJobStore jobs,
        CancellationToken cancellation)
    {
        if (await RunAsync(context, name, task, jobId, toolboxes, runs, cancellation).ConfigureAwait(false) is { } run)
        {
            await AnalysisEndpoints.CancelRunAsync(context, run, runs, jobs, cancellation).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The task string a run is filed under: the toolbox's item id and the tool, so a renamed toolbox keeps its runs.</summary>
    internal static string TaskOf(Toolbox toolbox, JsonObject tool) => $"{toolbox.Id}/{tool["name"]!.GetValue<string>()}";

    private static async Task<AnalysisRun?> RunAsync(
        HttpContext context, string name, string task, string jobId, IToolboxStore toolboxes, IAnalysisStore runs, CancellationToken cancellation)
    {
        if (await ToolAsync(context, name, task, toolboxes, cancellation).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        return await AnalysisEndpoints.OwnRunAsync(context, jobId, TaskOf(found.Toolbox, found.Tool), runs, cancellation).ConfigureAwait(false);
    }

    private static async Task<(Toolbox Toolbox, JsonObject Tool)?> ToolAsync(
        HttpContext context, string name, string task, IToolboxStore toolboxes, CancellationToken cancellation)
    {
        if (await ReadableAsync(context, name, toolboxes, cancellation).ConfigureAwait(false) is not { } toolbox)
        {
            return null;
        }

        if (ToolsOf(toolbox).FirstOrDefault(t => string.Equals(t["name"]?.GetValue<string>(), task, StringComparison.OrdinalIgnoreCase)) is not { } tool)
        {
            await AnalysisEndpoints.RefuseAsync(context, 404, $"The toolbox '{name}' has no tool '{task}'.").ConfigureAwait(false);
            return null;
        }

        return (toolbox, tool);
    }

    private static async Task<Toolbox?> ReadableAsync(HttpContext context, string name, IToolboxStore toolboxes, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await toolboxes.FindByNameAsync(name, cancellation).ConfigureAwait(false) is { } toolbox && MayRun(toolbox, current))
        {
            return toolbox;
        }

        // The same answer for a toolbox that is not there and one that is not shared with the caller.
        await AnalysisEndpoints.RefuseAsync(context, 404, $"No toolbox '{name}' is visible to you.").ConfigureAwait(false);
        return null;
    }

    private static async Task<Toolbox?> ManagedAsync(HttpContext context, string name, IToolboxStore toolboxes, CancellationToken cancellation)
    {
        if (await ReadableAsync(context, name, toolboxes, cancellation).ConfigureAwait(false) is not { } toolbox)
        {
            return null;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (toolbox.Owner != current.Principal.Id && !current.Authorization.Allows(Privilege.AdminManageAllContent))
        {
            await AnalysisEndpoints.RefuseAsync(context, 403, $"'{name}' is not yours to change.").ConfigureAwait(false);
            return null;
        }

        return toolbox;
    }

    private static bool MayRun(Toolbox toolbox, RequestPrincipal current) =>
        LayerAccess.Evaluate(toolbox.Sharing, toolbox.Owner, current.Principal, current.Authorization, toolbox.SharedWith ?? []).IsAllowed();

    private static IEnumerable<JsonObject> ToolsOf(Toolbox toolbox) =>
        (JsonNode.Parse(toolbox.Tools) as JsonArray ?? []).OfType<JsonObject>();

    private static IEnumerable<JsonObject> ParametersOf(JsonObject tool) =>
        (tool["parameters"] as JsonArray ?? []).OfType<JsonObject>();

    private static bool IsOutput(JsonObject parameter) =>
        string.Equals(parameter["direction"]?.GetValue<string>(), "Output", StringComparison.OrdinalIgnoreCase);

    /// <summary>A parameter's GP data type as the task resource names it: a layer of any kind is a feature record set.</summary>
    internal static string DataTypeOf(JsonObject parameter) => (parameter["datatype"]?.GetValue<string>() ?? "GPString") switch
    {
        "GPFeatureLayer" or "GPFeatureRecordSetLayer" or "DEFeatureClass" or "GPLayer" or "Feature Layer" or "Feature Class"
            or "DEShapefile" => "GPFeatureRecordSetLayer",
        "GPRasterLayer" or "GPRasterDataLayer" or "DERasterDataset" or "Raster Layer" or "Raster Dataset"
            or "GPRasterLayerOrDataset" => "GPRasterDataLayer",
        "GPLong" or "Long" => "GPLong",
        "GPDouble" or "Double" => "GPDouble",
        "GPBoolean" or "Boolean" => "GPBoolean",
        "GPDate" or "Date" => "GPDate",
        "GPLinearUnit" or "Linear Unit" => "GPLinearUnit",
        _ => "GPString",
    };

    /// <summary>
    /// The image service a raster parameter names — <c>{"url": ".../ImageServer"}</c> or the bare url, as a web tool's
    /// GPRasterDataLayer is sent — when the caller may read it; else why not (ADR-224 condition 2).
    /// </summary>
    private static async Task<(PublishedCoverage? Coverage, string? Why)> InputCoverageAsync(
        HttpContext context, string parameter, string sent, RequestPrincipal current, CancellationToken cancellation)
    {
        string url = sent.Trim();

        try
        {
            if (JsonNode.Parse(sent) is JsonObject named && named["url"] is JsonValue address)
            {
                url = address.GetValue<string>();
            }
        }
        catch (JsonException)
        {
            // A bare url.
        }

        int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
        string[] path = at < 0 ? [] : url[(at + "/rest/services/".Length)..].Split('?')[0].Trim('/').Split('/');

        if (path.Length is < 2 or > 3 || !string.Equals(path[^1], "ImageServer", StringComparison.Ordinal))
        {
            return (null, $"{parameter} is {{\"url\": \"<an image service of this server, ending in /ImageServer>\"}}.");
        }

        string? folder = path.Length == 3 ? path[0] : null;

        if (context.RequestServices.GetService(typeof(ICoverageCatalog)) is not ICoverageCatalog coverages
            || await coverages.FindAsync(folder, path[^2], cancellation).ConfigureAwait(false) is not { } coverage
            || !LayerAccess.Evaluate(coverage.Sharing, coverage.Owner, current.Principal, current.Authorization, coverage.SharedWith).IsAllowed())
        {
            return (null, $"There is no image service at '{url}' that you may read.");
        }

        if (coverage.Images is { Count: > 1 })
        {
            return (null, $"'{url}' is a mosaic of {coverage.Images.Count} images; a tool takes a single image for now.");
        }

        long cells = (long)coverage.Info.Width * coverage.Info.Height;

        if (cells > ToolRunner.RasterCellLimit)
        {
            return (null, $"'{url}' has {cells:N0} cells, more than the {ToolRunner.RasterCellLimit:N0} a tool is given.");
        }

        return (coverage, null);
    }

    /// <summary>A tool's output, which the runner reports as text, as the type its parameter declares — a GPLong a number.</summary>
    internal static JsonValue? ValueOf(string? text, string dataType) => text is null ? null : Literal(text, dataType);

    /// <summary>A value sent as text, as the type the tool reads it as.</summary>
    private static JsonValue? Literal(string value, string dataType)
    {
        try
        {
            return dataType switch
            {
                "GPLong" => JsonValue.Create(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                "GPDouble" => JsonValue.Create(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                "GPBoolean" => JsonValue.Create(string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)),
                _ => JsonValue.Create(value),
            };
        }
        catch (FormatException)
        {
            return JsonValue.Create(value);
        }
    }

    /// <summary>A service name from what was asked: letters, digits, _ and -, starting with one of the first two, up to 40.</summary>
    internal static string NameFor(string text)
    {
        string cleaned = NotAName().Replace(text.Trim(), "_").TrimStart('_', '-');
        cleaned = cleaned.Length == 0 ? "toolbox" : cleaned;
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex NotAName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex ServiceName();

    private static object Describe(HttpContext context, Toolbox toolbox, RequestPrincipal current) => new
    {
        id = toolbox.Id,
        mine = toolbox.Owner == current.Principal.Id,
        manages = toolbox.Owner == current.Principal.Id || current.Authorization.Allows(Privilege.AdminManageAllContent),
        name = toolbox.Name,
        title = toolbox.Title,
        owner = toolbox.OwnerName,
        sharing = toolbox.Sharing.ToString().ToLowerInvariant(),
        fileName = toolbox.FileName,
        created = toolbox.Created,
        url = $"{TileFaces.Origin(context)}/rest/services/{Folder}/{toolbox.Name}/GPServer",
        tools = ToolsOf(toolbox).Select(t => new
        {
            name = t["name"]!.GetValue<string>(),
            label = t["label"]?.GetValue<string>(),
            description = t["description"]?.GetValue<string>(),
            parameters = ParametersOf(t).Select(p => new
            {
                name = p["name"]!.GetValue<string>(),
                displayName = p["displayName"]?.GetValue<string>(),
                dataType = DataTypeOf(p),
                direction = IsOutput(p) ? "Output" : "Input",
                required = p["parameterType"]?.GetValue<string>() is not ("Optional" or "Derived"),
                choiceList = p["choiceList"]?.DeepClone(),
                defaultValue = p["defaultValue"]?.DeepClone(),
            }),
        }),
    };

    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the next start's sweep of the runs folder; a run's files are its own and nothing reads them.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
