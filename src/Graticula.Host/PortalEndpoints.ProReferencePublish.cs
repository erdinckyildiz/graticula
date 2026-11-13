using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// ArcGIS Pro's last two steps of a reference publish: the upload of the service definition and the
/// <c>PublishServiceDefinition</c> job on <c>System/PublishingTools</c> — ADR-299 condition 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sequence is measured</b> (ADR-299 §4, 2026-11-12): <c>POST admin/uploads/upload</c> with the <c>.sd</c>, then
/// SOAP <c>SubmitJob</c> with <c>ToolName=PublishServiceDefinition</c> and three string values — the uploaded item's
/// id, a configuration override, and <c>{"buildInitialCache":false}</c> — then <c>GetJobStatus</c>,
/// <c>GetJobMessages</c>, <c>GetJobResult</c>, and finally the service's admin document. The envelopes are the SOAP
/// form Esri publishes a WSDL for; the answers are the shapes a real 12.1 server gave to the same calls.
/// </para>
/// <para>
/// <b>The job publishes nothing of its own.</b> It reads the definition (<see cref="ReferenceServiceDefinition"/>),
/// matches every layer's connection to a registered source by §5.1 — the same database <i>and</i> the same database
/// user — and hands the composition to <c>POST /admin/publish</c>'s own handler as the caller, so a service made from
/// Pro is made by the rules a service made in Studio is. The columns Pro's query did not select are hidden through
/// <c>PUT /admin/layers/{name}/fields</c>, so the service shows what Pro drew and nothing more.
/// </para>
/// <para>
/// <b>State: uploads and finished jobs are held by this node for an hour</b>, the upload on its disk and the job in
/// memory, keyed by random ids and answered only to the member who made them. Pro's upload, submit and polls follow
/// each other within seconds on one connection; a node restarted in between loses the job, and Pro reports the
/// publish as failed, which it was.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static readonly TimeSpan HeldFor = TimeSpan.FromHours(1);

    private static readonly string[] NoFile = ["The upload carried no file."];

    private static readonly ConcurrentDictionary<string, ProUpload> ProUploads = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ProJob> ProJobs = new(StringComparer.Ordinal);

    /// <summary>A file Pro uploaded to <c>admin/uploads/upload</c>.</summary>
    private sealed record ProUpload(string Path, Guid Owner, DateTimeOffset At);

    /// <summary>A finished <c>PublishingTools</c> job: its outcome, its messages and its output values by parameter name.</summary>
    internal sealed record ProJob(Guid Owner, bool Succeeded, IReadOnlyList<(string Type, string Text)> Messages, IReadOnlyDictionary<string, string> Results, DateTimeOffset At);

    /// <summary>Keeps an uploaded file and answers in the admin API's <i>Upload Item</i> shape.</summary>
    private static async Task<IResult> UploadAsync(HttpContext context, CancellationToken cancellation)
    {
        IConfiguration configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        IFormFile? file = form.Files.Count > 0 ? form.Files[0] : null;

        if (file is null)
        {
            return Results.Json(new { status = "error", messages = NoFile, code = 400 });
        }

        Forget();
        string itemId = "i" + Guid.NewGuid().ToString("N");
        string itemName = System.IO.Path.GetFileName(file.FileName) is { Length: > 0 } n ? n : "upload.sd";

        // While Pro is measured on dev the upload is kept with the wire log, as it was before this was built.
        string root = RecordingPro(configuration)
            ? System.IO.Path.Combine(
                configuration["Graticula:ProPublishCaptureDirectory"] ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture"),
                "uploads")
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "graticula-pro-uploads");
        string kept = System.IO.Path.Combine(root, itemId);
        Directory.CreateDirectory(kept);
        string path = System.IO.Path.Combine(kept, itemName);

        await using (FileStream target = File.Create(path))
        {
            await file.CopyToAsync(target, cancellation).ConfigureAwait(false);
        }

        if (RecordingPro(configuration))
        {
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(kept, "fields.txt"),
                string.Join('\n', form.Where(f => !CapturedSecret(f.Key)).Select(f => $"{f.Key}={f.Value}")),
                cancellation).ConfigureAwait(false);
        }

        ProUploads[itemId] = new ProUpload(path, caller.Principal.Id, DateTimeOffset.UtcNow);

        return Results.Ok(new
        {
            status = "success",
            item = new
            {
                itemID = itemId,
                itemName,
                description = form["description"].ToString() is { Length: > 0 } d ? d : null,
                pathOnServer = $"/state/uploads/{itemId}/{itemName}",
                date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                committed = true,
            },
        });
    }

    /// <summary>
    /// Answers one SOAP call to <c>System/PublishingTools</c>, or null for an operation it does not know.
    /// </summary>
    private static async Task<IResult?> PublishingToolsAsync(
        HttpContext context, string? operation, string envelope, CancellationToken cancellation)
    {
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;

        switch (operation)
        {
            case "GetExecutionType":
                return Soap("<tns:GetExecutionTypeResponse><Result>esriExecutionTypeAsynchronous</Result></tns:GetExecutionTypeResponse>");

            case "SubmitJob" when SoapText(envelope, "ToolName") == "PublishServiceDefinition":
                Forget();
                List<string?> values = GpValues(envelope);
                ProJob job = await PublishServiceDefinitionAsync(context, values.Count > 0 ? values[0] : null, cancellation)
                    .ConfigureAwait(false);
                string id = "j" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                ProJobs[id] = job;
                return Soap($"<tns:SubmitJobResponse><Result>{id}</Result></tns:SubmitJobResponse>");

            case "GetJobStatus" or "GetJobMessages" or "GetJobResult":
                if (SoapText(envelope, "JobID") is not { } jobId || !ProJobs.TryGetValue(jobId, out ProJob? found)
                    || found.Owner != caller.Principal.Id)
                {
                    return SoapFault("No such job on this server.");
                }

                return operation switch
                {
                    "GetJobStatus" => Soap(
                        $"<tns:GetJobStatusResponse><Result>{(found.Succeeded ? "esriJobSucceeded" : "esriJobFailed")}</Result></tns:GetJobStatusResponse>"),
                    "GetJobMessages" => Soap(
                        "<tns:GetJobMessagesResponse><Result xsi:type=\"tns:ArrayOfJobMessage\">"
                        + string.Concat(found.Messages.Select(m =>
                            $"<JobMessage xsi:type=\"tns:JobMessage\"><MessageType>{m.Type}</MessageType><MessageDesc>{SecurityElement.Escape(m.Text)}</MessageDesc></JobMessage>"))
                        + "</Result></tns:GetJobMessagesResponse>"),

                    // One value for each parameter named in <ParameterNames>, in that order; a name it does not have is
                    // answered empty rather than left out, so the positions still line up.
                    _ => Soap(
                        "<tns:GetJobResultResponse><Result xsi:type=\"tns:GPResult\"><Values xsi:type=\"tns:GPValues\">"
                        + string.Concat(Regex.Matches(envelope, "<(?:\\w+:)?String>(?<n>[^<]*)</(?:\\w+:)?String>")
                            .Select(m => found.Results.TryGetValue(m.Groups["n"].Value, out string? value)
                                ? $"<GPValue xsi:type=\"tns:GPString\"><Value>{SecurityElement.Escape(value)}</Value></GPValue>"
                                : "<GPValue xsi:type=\"tns:GPString\"></GPValue>"))
                        + "</Values></Result></tns:GetJobResultResponse>"),
                };

            default:
                return null;
        }
    }

    /// <summary>Publishes the uploaded definition by reference and says how it went, in the job's words.</summary>
    internal static async Task<ProJob> PublishServiceDefinitionAsync(HttpContext context, string? itemId, CancellationToken cancellation)
    {
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        List<(string, string)> messages = [("esriJobMessageTypeInformative", "Submitted."), ("esriJobMessageTypeInformative", "Executing...")];

        ProJob Failed(params string[] why)
        {
            messages.AddRange(why.Select(w => ("esriJobMessageTypeError", w)));
            messages.Add(("esriJobMessageTypeError", "Failed to execute (Publish Service Definition)."));
            messages.Add(("esriJobMessageTypeInformative", "Failed."));
            return new ProJob(caller.Principal.Id, false, messages, new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        }

        if (!caller.Authorization.Allows(Privilege.ContentPublishFeatures))
        {
            return Failed("Publishing a service needs the privilege to publish features on this server.");
        }

        if (itemId is null || !ProUploads.TryGetValue(itemId, out ProUpload? upload) || upload.Owner != caller.Principal.Id)
        {
            return Failed($"The uploaded service definition '{itemId}' is not on this server. An upload is kept for an hour, "
                + "on the machine that received it; share again.");
        }

        try
        {
            ReferenceDefinition definition;
            try
            {
                definition = await ReferenceServiceDefinition.ReadAsync(upload.Path, cancellation).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                return Failed(e.Message);
            }

            if (definition.Layers.Count == 0)
            {
                return Failed("The map holds no layer to publish.");
            }

            if (definition.Layers.Where(l => l.Refusal is not null).Select(l => l.Refusal!).ToArray() is { Length: > 0 } refused)
            {
                return Failed(refused);
            }

            ReferencePlan plan = await PlanAsync(context, definition, cancellation).ConfigureAwait(false);
            if (plan.Refusals.Count > 0)
            {
                return Failed([.. plan.Refusals]);
            }

            (int status, byte[] body) = await AdminEndpoints.PublishCompositionInProcessAsync(
                context,
                new CompositionRequest(
                    definition.ServiceName,
                    definition.FolderName,
                    definition.Item.Snippet,
                    "private",
                    null,
                    [.. plan.Layers.Select(l => new CompositionNodeRequest(null, l.Request, null))],

                    // Pro reaches this only for a free name or after its user chose to overwrite; replacing somebody
                    // else's service is refused by the publish route whatever this says.
                    Replace: true),
                cancellation).ConfigureAwait(false);

            if (status is < 200 or > 299)
            {
                return Failed(ErrorMessage(body) ?? $"The service could not be published ({status}).");
            }

            using JsonDocument made = JsonDocument.Parse(body);
            Guid serviceId = made.RootElement.GetProperty("id").GetGuid();
            string? folder = made.RootElement.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            string qualified = folder is { Length: > 0 } ? $"{folder}/{definition.ServiceName}" : definition.ServiceName;

            await FinishAsync(context, serviceId, qualified, definition, plan, messages, cancellation).ConfigureAwait(false);

            string origin = $"{context.Request.Scheme}://{context.Request.Host}/arcgis";
            messages.Add(("esriJobMessageTypeInformative", $"Published {qualified} by reference to {plan.Sources} registered database{(plan.Sources == 1 ? string.Empty : "s")}."));
            messages.Add(("esriJobMessageTypeInformative", "Succeeded."));

            // The two outputs Pro asks for by name (measured 2026-11-13): the service's SOAP and REST addresses.
            return new ProJob(caller.Principal.Id, true, messages, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["out_soap_svc_url"] = $"{origin}/services/{qualified}/MapServer",
                ["out_rest_svc_url"] = $"{origin}/rest/services/{qualified}/MapServer",
            }, DateTimeOffset.UtcNow);
        }
        finally
        {
            ProUploads.TryRemove(itemId, out _);
            if (!RecordingPro(context.RequestServices.GetRequiredService<IConfiguration>()))
            {
                TryDelete(System.IO.Path.GetDirectoryName(upload.Path));
            }
        }
    }

    /// <summary>One layer of the composition and what Pro's query left out of it.</summary>
    private sealed record PlannedLayer(PublishRequest Request, string Title, IReadOnlyList<string>? Columns);

    /// <summary>The composition, or why it cannot be made.</summary>
    private sealed record ReferencePlan(IReadOnlyList<PlannedLayer> Layers, IReadOnlyList<string> Refusals, int Sources);

    /// <summary>Matches each layer to a registered source and its table there.</summary>
    private static async Task<ReferencePlan> PlanAsync(HttpContext context, ReferenceDefinition definition, CancellationToken cancellation)
    {
        IAdminCatalog catalog = context.RequestServices.GetRequiredService<IAdminCatalog>();
        IDataSourceProbe probe = context.RequestServices.GetRequiredService<IDataSourceProbe>();
        IReadOnlyDictionary<Guid, string> publishers = catalog is PostgresAdminCatalog postgres
            ? await postgres.PublisherAddressesAsync(cancellation).ConfigureAwait(false)
            : new Dictionary<Guid, string>();

        List<(RegisteredDataSource Source, string Stored, ArcGisConnection Connection)> registered = [];
        foreach (RegisteredDataSource source in await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false))
        {
            if (!string.Equals(source.Kind, DataSourceKinds.PostGis, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (await catalog.ConnectionStringOfAsync(source.Id, cancellation).ConfigureAwait(false) is { Length: > 0 } stored
                    && ArcGisConnection.FromNpgsql(stored) is { } connection)
                {
                    registered.Add((source, stored, connection));
                }
            }
            catch (CryptographicException)
            {
                // Sealed with another key: it cannot be read, so it cannot be matched.
            }
        }

        List<PlannedLayer> layers = [];
        List<string> refusals = [];
        Dictionary<Guid, ProbeResult> probed = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (ReferenceLayer layer in definition.Layers)
        {
            ArcGisConnection wanted = layer.Connection!;
            (RegisteredDataSource Source, string Stored, ArcGisConnection Connection)? match = registered
                .Where(r => wanted.Matches(r.Connection)
                    || (publishers.TryGetValue(r.Source.Id, out string? at) && wanted.Matches(r.Connection.At(at))))
                .Select(r => ((RegisteredDataSource, string, ArcGisConnection)?)r)
                .FirstOrDefault();

            if (match is not { } found)
            {
                string where = $"{wanted.Host}{(wanted.Port is { } p ? "," + p.ToString(CultureInfo.InvariantCulture) : string.Empty)}/{wanted.Database}";
                bool sameDatabase = registered.Any(r => r.Connection.DatabaseKey == wanted.DatabaseKey
                    || (publishers.TryGetValue(r.Source.Id, out string? at) && r.Connection.At(at).DatabaseKey == wanted.DatabaseKey));
                refusals.Add(sameDatabase
                    ? $"'{layer.Title}' reads {where} as '{wanted.User}', and that database is registered on this server for "
                        + "another user. A layer is served only as the database user it was published with (ADR-299 §5.1)."
                    : $"'{layer.Title}': {where} is not a registered database on this server. Register it under Data sources, "
                        + "or share the layer with Copy all data.");
                continue;
            }

            if (!probed.TryGetValue(found.Source.Id, out ProbeResult? tables))
            {
                tables = await probe.ProbeAsync(found.Stored, cancellation).ConfigureAwait(false);
                probed[found.Source.Id] = tables;
            }

            SourceTable? table = tables.Tables
                .Where(t => t.SchemaName == layer.Schema && t.TableName == layer.Table)
                .Concat(tables.Tables.Where(t => string.Equals(t.SchemaName, layer.Schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.TableName, layer.Table, StringComparison.OrdinalIgnoreCase)))
                .Select(t => (SourceTable?)t)
                .FirstOrDefault();

            if (table is not { } t)
            {
                string? skipped = tables.Skipped?.FirstOrDefault(s => string.Equals(s.Name, $"{layer.Schema}.{layer.Table}", StringComparison.OrdinalIgnoreCase)).Reason;
                refusals.Add($"'{layer.Title}': {layer.Schema}.{layer.Table} is not a table this server can serve from '{found.Source.Name}'"
                    + (skipped is { Length: > 0 } ? $" — {skipped}" : tables.Outcome == ProbeOutcome.Usable ? "." : $" — {tables.Message}"));
                continue;
            }

            if (layer.IsTable != (t.GeometryColumn is null))
            {
                refusals.Add(layer.IsTable
                    ? $"'{layer.Title}' is a table in Pro, and {layer.Schema}.{layer.Table} has a geometry column."
                    : $"'{layer.Title}' is a layer in Pro, and {layer.Schema}.{layer.Table} has no geometry column this server reads.");
                continue;
            }

            string? identity = layer.ObjectIdField is { Length: > 0 } oid
                ? t.IdentityCandidates.Concat(t.WideIdentityCandidates ?? []).FirstOrDefault(c => string.Equals(c, oid, StringComparison.OrdinalIgnoreCase))
                    ?? (string.Equals(t.CandidateObjectIdColumn, oid, StringComparison.OrdinalIgnoreCase) ? t.CandidateObjectIdColumn : null)
                : t.CandidateObjectIdColumn;

            if (identity is null)
            {
                refusals.Add(layer.ObjectIdField is { Length: > 0 }
                    ? $"'{layer.Title}' uses '{layer.ObjectIdField}' as its object id, and that column is not unique, not null and integer in {layer.Schema}.{layer.Table}."
                    : $"'{layer.Title}': {layer.Schema}.{layer.Table} has no integer column this server can use as an ArcGIS object id.");
                continue;
            }

            if (layer.Columns is { } chosen)
            {
                foreach (string? needed in (string?[])[identity, t.GeometryColumn])
                {
                    if (needed is not null && !chosen.Contains(needed, StringComparer.OrdinalIgnoreCase))
                    {
                        refusals.Add($"'{layer.Title}': Pro's query does not select '{needed}', which this server needs to serve the table.");
                    }
                }
            }

            string name = t.TableName;
            for (int n = 2; !names.Add(name); n++)
            {
                name = $"{t.TableName}_{n.ToString(CultureInfo.InvariantCulture)}";
            }

            layers.Add(new PlannedLayer(
                new PublishRequest(
                    name, found.Source.Id, t.SchemaName, t.TableName, t.GeometryColumn, identity, identity, t.Srid,
                    t.GeometryColumn is null ? null : t.GeometryType, "private"),
                layer.Title,
                layer.Columns));
        }

        return new ReferencePlan(layers, refusals, layers.Select(l => l.Request.DataSourceId).Distinct().Count());
    }

    /// <summary>Gives each new layer Pro's title and hides the columns Pro's query did not select.</summary>
    private static async Task FinishAsync(
        HttpContext context, Guid serviceId, string qualified, ReferenceDefinition definition, ReferencePlan plan,
        List<(string, string)> messages, CancellationToken cancellation)
    {
        IAdminCatalog catalog = context.RequestServices.GetRequiredService<IAdminCatalog>();
        PostgresLayerCatalog layerCatalog = context.RequestServices.GetRequiredService<PostgresLayerCatalog>();
        ServiceContexts contexts = context.RequestServices.GetRequiredService<ServiceContexts>();

        List<PublishedLayer> fresh = [.. (await layerCatalog.ListAsync(cancellation).ConfigureAwait(false)).Where(l => l.ServiceId == serviceId)];
        List<(PublishedLayer, string)> named = [];

        foreach (PlannedLayer planned in plan.Layers)
        {
            if (fresh.FirstOrDefault(l => string.Equals(l.Definition.Name, planned.Request.Name, StringComparison.Ordinal)) is not { } layer)
            {
                continue;
            }

            named.Add((layer, planned.Title));

            if (planned.Columns is not { } chosen)
            {
                continue;
            }

            (_, Graticula.Features.LayerDescription table) = await contexts.TableAsync(layer, cancellation).ConfigureAwait(false);
            AdminEndpoints.FieldOverrideEntry[] hidden =
            [
                .. table.Fields
                    .Where(field => !chosen.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(field => new AdminEndpoints.FieldOverrideEntry(field.Name, null, Hidden: true)),
            ];

            if (hidden.Length == 0)
            {
                continue;
            }

            (int status, byte[] body) = await AdminEndpoints.SetFieldOverridesInProcessAsync(
                context, layer.Definition.Name, qualified, new AdminEndpoints.FieldOverridesRequest(hidden), cancellation).ConfigureAwait(false);

            messages.Add(status is >= 200 and <= 299
                ? ("esriJobMessageTypeInformative", $"'{planned.Title}': {hidden.Length} column(s) Pro's query did not select are hidden.")
                : ("esriJobMessageTypeWarning", $"'{planned.Title}': the columns Pro's query did not select could not be hidden ({ErrorMessage(body) ?? status.ToString(CultureInfo.InvariantCulture)}); hide them in Studio."));
        }

        await NameLayersAsync(named, catalog, contexts, cancellation).ConfigureAwait(false);
        await ApplyDetailsAsync(definition.Item, definition.ServiceName, catalog, cancellation).ConfigureAwait(false);
    }

    /// <summary>The message of an error body the admin routes write, or null.</summary>
    private static string? ErrorMessage(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of the first element named <paramref name="name"/>, any prefix, or null.</summary>
    internal static string? SoapText(string envelope, string name)
    {
        Match found = Regex.Match(envelope, $"<(?:\\w+:)?{name}>(?<v>[^<]*)</(?:\\w+:)?{name}>", RegexOptions.CultureInvariant);
        return found.Success ? System.Net.WebUtility.HtmlDecode(found.Groups["v"].Value) : null;
    }

    /// <summary>Each <c>GPValue</c>'s <c>Value</c>, in order; null for a value element left empty.</summary>
    internal static List<string?> GpValues(string envelope) =>
    [
        .. Regex.Matches(envelope, @"<GPValue\b[^>]*?(?:/>|>(?<inner>.*?)</GPValue>)", RegexOptions.CultureInvariant | RegexOptions.Singleline)
            .Select(m => Regex.Match(m.Groups["inner"].Value, "<Value>(?<v>.*?)</Value>", RegexOptions.Singleline) is { Success: true } v
                ? System.Net.WebUtility.HtmlDecode(v.Groups["v"].Value)
                : null),
    ];

    private static IResult SoapFault(string text) =>
        Results.Text(
            SoapEnvelopeOpen + "<soap:Fault><faultcode>soap:Client</faultcode><faultstring>" + SecurityElement.Escape(text)
            + "</faultstring></soap:Fault>" + SoapEnvelopeClose,
            "text/xml; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>Drops uploads and jobs older than an hour, and an abandoned upload's file with it.</summary>
    private static void Forget()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow - HeldFor;
        foreach ((string id, ProUpload upload) in ProUploads)
        {
            if (upload.At < before && ProUploads.TryRemove(id, out _))
            {
                TryDelete(System.IO.Path.GetDirectoryName(upload.Path));
            }
        }

        foreach ((string id, ProJob job) in ProJobs)
        {
            if (job.At < before)
            {
                ProJobs.TryRemove(id, out _);
            }
        }
    }

    private static void TryDelete(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
