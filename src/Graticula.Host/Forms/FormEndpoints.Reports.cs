using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host.Forms.Reports;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Forms;

/// <summary>
/// A form's feature reports — ADR-301: Word templates kept with the form, checked when they are uploaded, and a document per
/// record (or one for several) filled from the records the asker may read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Templates are the form item's resources</b> under <see cref="ReportTemplatesPrefix"/> (ADR-264), so they are listed,
/// shared, recycled and deleted with the form; written by whoever manages the form, read by whoever may open it.
/// </para>
/// <para>
/// <b>Records are read as the asker, through the layer.</b> The form's own layers when the asker may read them, else the
/// form's Inbox view, whose filter the database applies per reader (ADR-300) — through <see cref="ServiceLookup"/>, the
/// layer's Query ceiling and <see cref="ServiceContexts"/>, the same doors and source a query takes; attachments through
/// <see cref="AttachmentEndpoints.StoreOfAsync"/>. A record the asker may not read is one that is not there.
/// </para>
/// </remarks>
public static partial class FormEndpoints
{
    /// <summary>Where a form's report templates are kept among its resources.</summary>
    public const string ReportTemplatesPrefix = "graticula/report-templates/";

    /// <summary>The most records one report request takes (INFERRED: Survey123 takes 2,000 in a job; this is answered at once).</summary>
    public const int MaximumReportRecords = 100;

    /// <summary>The most rows one query of a report reads — a section with where or !important, or statistics' groups (INFERRED).</summary>
    public const int MaximumReportRows = 1000;

    /// <summary>The most maps one report draws (INFERRED): a map for each of 100 records, and two more for each beside.</summary>
    public const int MaximumReportMaps = 300;

    /// <summary>
    /// The most PDF documents one request lays out — ADR-302 §5.5 (INFERRED). Each is a run of the engine, which took up to
    /// 14 s for one public template on the showcase; a merged report is one document and is not counted here.
    /// </summary>
    public const int MaximumPdfDocuments = 25;

    private const string PdfType = "application/pdf";

    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Two reports at once per node; a third waits five seconds and is refused (INFERRED, as the print service's bound).</summary>
    private static readonly SemaphoreSlim ReportsAtOnce = new(2, 2);

    private static void MapReports(WebApplication app)
    {
        app.MapGet("/admin/forms/{id}/reports/templates", TemplatesAsync);
        app.MapPost("/admin/forms/{id}/reports/templates", AddTemplateAsync).DisableAntiforgery();
        app.MapDelete("/admin/forms/{id}/reports/templates/{name}", RemoveTemplateAsync);
        app.MapPost("/admin/forms/{id}/reports", ReportAsync);
    }

    /// <summary>A form the caller may open, signed in, with its publication; null when not.</summary>
    /// <summary>The formats a report is written in here: Word always, PDF where the engine is installed (ADR-302).</summary>
    private static string[] ReportFormats(HttpContext context) =>
        context.RequestServices.GetService<IReportPdf>() is { Available: true } ? ["docx", "pdf"] : ["docx"];

    private static async Task<(Document? Document, JsonNode? Publication)> ReadableFormAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IDocumentStore documents = context.RequestServices.GetRequiredService<IDocumentStore>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        string lower = (id ?? string.Empty).ToLowerInvariant();

        if (current.Principal.IsAnonymous || !WebMaps.IsId(lower)
            || await documents.FindAsync(lower, cancellation).ConfigureAwait(false) is not { } document
            || document.Kind.ArcGisType != "Form"
            || !LayerAccess.Evaluate(document.Sharing, document.Owner, current.Principal, current.Authorization, document.SharedWith).IsAllowed())
        {
            return (null, null);
        }

        return (document, await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false));
    }

    /// <summary>The schema a template is checked and filled against: the published form, or the draft before publishing.</summary>
    private static async Task<ReportSchema?> ReportSchemaAsync(HttpContext context, Document document, JsonNode? publication, CancellationToken cancellation)
    {
        FormModel? model = publication is not null ? await PublishedModelAsync(context, document.Id, cancellation).ConfigureAwait(false) : null;

        if (model is null)
        {
            byte[] content = await context.RequestServices.GetRequiredService<IDocumentStore>().ContentAsync(document.Id, cancellation).ConfigureAwait(false) ?? [];

            try
            {
                model = FormModel.FromJson(JsonNode.Parse(content));
            }
            catch (Exception e) when (e is JsonException or FormWorkbookException)
            {
                return null;
            }
        }

        if (FormDefinition.Check(model).Definition is not { } definition)
        {
            return null;
        }

        string top = (publication?["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(l => ((string?)l["repeat"] ?? string.Empty).Length == 0).Select(l => (string?)l["name"]).FirstOrDefault()
            ?? FormLayerPlan.For(definition, document.Title).ServiceName;

        return new ReportSchema(definition, top);
    }

    private static object IssueJson(ReportIssue i) => new { part = i.Part, paragraph = i.Paragraph, text = i.Text, placeholder = i.Placeholder, message = i.Message };

    // ---------------------------------------------------------------------------------------------------- templates

    /// <summary><c>GET …/reports/templates</c>: the form's templates, and what the engine does not do.</summary>
    private static async Task<IResult> TemplatesAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ReadableFormAsync(context, id, cancellation).ConfigureAwait(false) is not ({ } document, var publication))
        {
            return Error(404, $"No form '{id}' that you can open. It may have been deleted, or it is not shared with you.");
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        (IReadOnlyList<ItemResource> page, _) = await resources.ListAsync(Guid.Parse(document.Id), 0, 1000, cancellation).ConfigureAwait(false);

        return Results.Json(new
        {
            id = document.Id,
            published = publication is not null,
            manages = LayerAccess.MayManage(document.Owner, context.Features.Get<RequestPrincipal>()!.Principal, context.Features.Get<RequestPrincipal>()!.Authorization),
            maximumRecords = MaximumReportRecords,
            formats = ReportFormats(context),
            notSupported = ReportTemplate.NotSupported,
            templates = page.Where(r => r.Path.StartsWith(ReportTemplatesPrefix, StringComparison.Ordinal))
                .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(r => new { name = r.Path[ReportTemplatesPrefix.Length..], size = r.Size, modified = r.Modified }),
        });
    }

    /// <summary>
    /// <c>POST …/reports/templates</c> with a <c>.docx</c> as <c>file</c>: checked against the form and kept, or refused with
    /// each problem's place. A template of the same name is replaced (INFERRED).
    /// </summary>
    private static async Task<IResult> AddTemplateAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "add a report template to", cancellation).ConfigureAwait(false) is not ({ } document, _))
        {
            return Missing(id);
        }

        if (!context.Request.HasFormContentType)
        {
            return Error(400, "Send the template as a file named 'file' in a multipart form.");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (form.Files.GetFile("file") is not { } file || file.Length == 0)
        {
            return Error(400, "Send the template as a file named 'file' in a multipart form.");
        }

        string name = ReportTemplate.FileName(Path.GetFileName(file.FileName ?? "template.docx"));

        if (!name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return Error(400, $"'{name}' is not a Word document: a report template is a .docx file.");
        }

        if (file.Length > ReportTemplate.MaximumBytes)
        {
            return Error(400, $"A template is at most {ReportTemplate.MaximumBytes / (1024 * 1024)} MB, and '{name}' is larger.");
        }

        byte[] bytes;

        using (MemoryStream copy = new())
        {
            await file.CopyToAsync(copy, cancellation).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        JsonNode? publication = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false);

        if (await ReportSchemaAsync(context, document, publication, cancellation).ConfigureAwait(false) is not { } schema)
        {
            return Error(409, "The form does not pass its own check, so a template cannot be checked against it. Fix the form first.");
        }

        ReportTemplate template;

        try
        {
            template = ReportTemplate.Open(bytes);
        }
        catch (ReportTemplateException e)
        {
            return Error(400, $"'{name}' was not added: {e.Message}");
        }

        IReadOnlyList<ReportIssue> issues = template.Check(schema);

        if (issues.Count > 0)
        {
            return Results.Json(new
            {
                error = new
                {
                    code = 400,
                    message = $"'{name}' was not added: {issues.Count} {(issues.Count == 1 ? "placeholder needs" : "placeholders need")} changing in it.",
                    details = issues.Select(i => i.ToString()).ToArray(),
                },
                issues = issues.Select(IssueJson),
                notSupported = ReportTemplate.NotSupported,
            }, statusCode: 400);
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        string path = ReportTemplatesPrefix + name;
        ItemResourceWrite written = await resources.WriteAsync(Guid.Parse(document.Id), path, DocxType, bytes, "inherit", replace: true,
            100L * 1024 * 1024, cancellation).ConfigureAwait(false);

        if (written == ItemResourceWrite.NotThere)
        {
            written = await resources.WriteAsync(Guid.Parse(document.Id), path, DocxType, bytes, "inherit", replace: false,
                100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        }

        if (written == ItemResourceWrite.TooLarge)
        {
            return Error(400, $"'{name}' was not added: the form's files would come to more than the 100 MB an item keeps.");
        }

        await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(), "form.report.template", document.Id,
            AdminEndpoints.Detail(new { name, size = bytes.Length, replaced = written == ItemResourceWrite.Replaced }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return Results.Json(new { name, size = bytes.Length, replaced = written == ItemResourceWrite.Replaced, notSupported = ReportTemplate.NotSupported },
            statusCode: written == ItemResourceWrite.Replaced ? 200 : 201);
    }

    /// <summary><c>DELETE …/reports/templates/{name}</c>.</summary>
    private static async Task<IResult> RemoveTemplateAsync(HttpContext context, string id, string name, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "remove a report template from", cancellation).ConfigureAwait(false) is not ({ } document, _))
        {
            return Missing(id);
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();

        if (await resources.RemoveAsync(Guid.Parse(document.Id), ReportTemplatesPrefix + name, cancellation).ConfigureAwait(false) == 0)
        {
            return Error(404, $"The form has no report template '{name}'.");
        }

        await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(), "form.report.template.remove", document.Id,
            AdminEndpoints.Detail(new { name }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ------------------------------------------------------------------------------------------------------- reports

    /// <summary>What <c>POST …/reports</c> takes.</summary>
    /// <param name="Template">The template's name, as listed.</param>
    /// <param name="ObjectIds">The records of the form's top layer.</param>
    /// <param name="Mode"><c>separate</c> (a document per record; several zipped) or <c>merged</c> (one document).</param>
    /// <param name="Name">The report's name, with placeholders for a separate report; empty for the default.</param>
    /// <param name="UtcOffsetMinutes">The asker's offset from UTC in minutes, east positive; dates are written in it.</param>
    /// <param name="Format"><c>docx</c>, the default, or <c>pdf</c> where the PDF engine is installed (ADR-302).</param>
    public sealed record ReportRequest(string? Template, long[]? ObjectIds, string? Mode, string? Name, int? UtcOffsetMinutes, string? Format);

    private static async Task<IResult> ReportAsync(HttpContext context, string id, ReportRequest request, CancellationToken cancellation)
    {
        if (await ReadableFormAsync(context, id, cancellation).ConfigureAwait(false) is not ({ } document, var publicationNode))
        {
            return Error(404, $"No form '{id}' that you can open. It may have been deleted, or it is not shared with you.");
        }

        if (publicationNode is not JsonObject publication)
        {
            return Error(409, "The form is not published, so it has no records to report on.");
        }

        string format = (request.Format is { Length: > 0 } asked ? asked : "docx").ToLowerInvariant();

        if (format is not ("docx" or "pdf"))
        {
            return Error(400, $"'{request.Format}' is not a report format: a report is written as docx or pdf.");
        }

        IReportPdf? pdfEngine = format == "pdf" ? context.RequestServices.GetService<IReportPdf>() : null;

        if (format == "pdf" && pdfEngine is not { Available: true })
        {
            return Error(400, "This server's PDF engine is not installed, so a report is written as a Word document (.docx) only (ADR-302).");
        }

        string mode = (request.Mode ?? "separate").ToLowerInvariant();

        if (mode is not ("separate" or "merged"))
        {
            return Error(400, $"'mode' is 'separate' (a document per record) or 'merged' (one document); '{request.Mode}' is neither.");
        }

        long[] ids = [.. (request.ObjectIds ?? []).Distinct()];

        if (ids.Length == 0)
        {
            return Error(400, "Choose at least one record: 'objectIds' names the records of the form's layer.");
        }

        if (ids.Length > MaximumReportRecords)
        {
            return Error(400, $"A report takes at most {MaximumReportRecords} records at once, and {ids.Length} were chosen. Choose fewer, or ask for several reports.");
        }

        if (request.UtcOffsetMinutes is { } minutes && Math.Abs(minutes) > 14 * 60)
        {
            return Error(400, "'utcOffsetMinutes' is an offset from UTC of at most 14 hours.");
        }

        if (mode == "merged" && request.Name is { } merged && merged.Contains("${", StringComparison.Ordinal))
        {
            return Error(400, "A merged report's name takes no placeholders, as in Survey123: it is one document for several records.");
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();

        if (request.Template is not { Length: > 0 } templateName
            || await resources.FindAsync(Guid.Parse(document.Id), ReportTemplatesPrefix + templateName, cancellation).ConfigureAwait(false) is not { } stored)
        {
            return Error(404, $"The form has no report template '{request.Template}'.");
        }

        if (await ReportSchemaAsync(context, document, publication, cancellation).ConfigureAwait(false) is not { } schema)
        {
            return Error(409, "The form does not pass its own check, so no report can be made of it.");
        }

        ReportTemplate template;

        try
        {
            template = ReportTemplate.Open(stored.Content);
        }
        catch (ReportTemplateException e)
        {
            return Error(409, $"The template '{templateName}' can no longer be read: {e.Message}");
        }

        // A template checked at upload can stop fitting the form when the form is published again with a question gone.
        if (template.Check(schema) is { Count: > 0 } issues)
        {
            return Results.Json(new
            {
                error = new
                {
                    code = 409,
                    message = $"The template '{templateName}' no longer fits the form as it is published: {issues.Count} {(issues.Count == 1 ? "placeholder needs" : "placeholders need")} changing. Upload it again changed.",
                    details = issues.Select(i => i.ToString()).ToArray(),
                },
                issues = issues.Select(IssueJson),
            }, statusCode: 409);
        }

        TimeSpan offset = TimeSpan.FromMinutes(request.UtcOffsetMinutes ?? 0);
        ReportEvaluator evaluator = new(schema, DateTimeOffset.UtcNow, offset);

        if (request.Name is { Length: > 0 } pattern && mode == "separate")
        {
            try
            {
                _ = ReportTemplate.Name(pattern, null, evaluator);
            }
            catch (ReportSyntaxException e)
            {
                return Error(400, $"The report's name could not be read: {e.Message}");
            }
        }

        if (!await ReportsAtOnce.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false))
        {
            context.Response.Headers.RetryAfter = "5";
            return Error(503, "Two reports are being made on this server already. Ask again in a few seconds.");
        }

        try
        {
            (List<ReportRecord>? records, IResult? refused, ReportReading? reading) = await ReportRecordsAsync(context, publication, schema, template, ids, cancellation).ConfigureAwait(false);

            if (refused is not null)
            {
                return refused;
            }

            // A template with a summary section is one document for all the records chosen, as Survey123 makes it (INFERRED).
            if (template.HasSummary)
            {
                mode = "merged";
            }

            if (pdfEngine is not null && mode == "separate" && records!.Count > MaximumPdfDocuments)
            {
                return Error(400, $"A PDF report lays out at most {MaximumPdfDocuments} documents at once, and {records.Count} were asked "
                    + "for, one per submission. Ask for one document for them all, choose fewer, or make them as Word documents (ADR-302).");
            }

            // Maps and queries are fetched as the asker before the fill that writes them: fill, fetch what was missing, again.
            ReportPrefetch lookups = new();
            await PrefetchAsync(context, publication, schema, template, records!, mode == "merged", evaluator, lookups, reading!, cancellation).ConfigureAwait(false);

            string baseName = ReportTemplate.FileName(request.Name is { Length: > 0 } given && !given.Contains("${", StringComparison.Ordinal)
                ? given
                : document.Title);
            byte[] bytes;
            string fileName;
            string type;
            string extension = "." + format;

            // ADR-302: the Word document is the report; a PDF is that document laid out by the engine, one per file.
            async Task<byte[]> Written(byte[] docx) =>
                pdfEngine is null ? docx : await pdfEngine.ConvertAsync(docx, cancellation).ConfigureAwait(false);

            if (mode == "merged" || records!.Count == 1)
            {
                bytes = await Written(template.Fill(records!, evaluator, lookups)).ConfigureAwait(false);
                fileName = (mode == "merged" ? baseName
                    : request.Name is { Length: > 0 } one ? ReportTemplate.Name(one, records![0], evaluator)
                    : $"{baseName}_{records![0].ObjectId.ToString(CultureInfo.InvariantCulture)}") + extension;
                type = pdfEngine is null ? DocxType : PdfType;
            }
            else
            {
                using MemoryStream zipped = new();

                using (ZipArchive zip = new(zipped, ZipArchiveMode.Create, leaveOpen: true))
                {
                    HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

                    foreach (ReportRecord record in records)
                    {
                        string each = request.Name is { Length: > 0 } named
                            ? ReportTemplate.Name(named, record, evaluator)
                            : $"{baseName}_{record.ObjectId.ToString(CultureInfo.InvariantCulture)}";
                        string unique = each;

                        for (int n = 2; !taken.Add(unique); n++)
                        {
                            unique = $"{each} ({n.ToString(CultureInfo.InvariantCulture)})";
                        }

                        byte[] written = await Written(template.Fill([record], evaluator, lookups)).ConfigureAwait(false);
                        ZipArchiveEntry entry = zip.CreateEntry(unique + extension, CompressionLevel.NoCompression);
                        await using Stream stream = entry.Open();
                        await stream.WriteAsync(written, cancellation).ConfigureAwait(false);
                    }
                }

                bytes = zipped.ToArray();
                fileName = baseName + ".zip";
                type = "application/zip";
            }

            await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(), "form.report", document.Id,
                AdminEndpoints.Detail(new { template = templateName, records = ids.Length, mode, format, bytes = bytes.Length }), succeeded: true, cancellation)
                .ConfigureAwait(false);

            context.Response.Headers.CacheControl = "no-store";

            // What the report could not draw in full — a basemap at another address, a layer not shared — said rather than
            // hidden: one sentence each, percent-encoded so a header carries them.
            if (lookups.Notes.Count > 0)
            {
                context.Response.Headers[ReportNotesHeader] = Uri.EscapeDataString(string.Join(" ", lookups.Notes.Take(20)));
            }

            return Results.File(bytes, type, fileName);
        }
        catch (ReportTemplateException e)
        {
            return Error(400, $"The report was not made: {e.Message}");
        }
        catch (ReportPdfException e)
        {
            // The Word document could be made; only its layout failed, so the answer says to ask for that.
            return Results.Json(new
            {
                error = new
                {
                    code = 422,
                    message = $"{e.Message} The report can be made as a Word document (.docx), which is the report itself.",
                    details = Array.Empty<string>(),
                },
                fallback = "docx",
            }, statusCode: 422);
        }
        finally
        {
            ReportsAtOnce.Release();
        }
    }

    /// <summary>A form's layer as the asker reads it: the form's own, or its Inbox's when only that is theirs to read.</summary>
    private sealed record ReportLayer(JsonObject Published, PublishedLayer Layer, IFeatureSource Source, LayerDescription Described, string Url);

    /// <summary>
    /// The chosen records with their repeats' rows and the attachments the template reads, read as the asker; or the
    /// refusal. Records the asker may not read are absent, and absent records are refused by number.
    /// </summary>
    private static async Task<(List<ReportRecord>? Records, IResult? Refused, ReportReading? Reading)> ReportRecordsAsync(
        HttpContext context, JsonObject publication, ReportSchema schema, ReportTemplate template, long[] ids, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        CatalogFallback catalog = services.GetRequiredService<CatalogFallback>();
        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        List<JsonObject> published = [.. (publication["layers"] as JsonArray ?? []).OfType<JsonObject>()];
        string folder = (string?)publication["folder"] ?? FeatureServerMetadataWriter.HostedFolder;

        // The form's own service when the asker may read it; else the Inbox, which the database filters per reader.
        Dictionary<string, ReportLayer> layers = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string? service, string? serviceFolder, string urlKey) in new[]
        {
            ((string?)publication["service"], folder, "url"),
            ((string?)publication["inboxService"], FeatureServerMetadataWriter.HostedFolder, "inboxUrl"),
        })
        {
            if (service is null || await ServiceLookup.ReadableQuietlyAsync(context, catalog, serviceFolder, service, cancellation).ConfigureAwait(false) is not { } found
                || !ServiceFaces.AnswersFeatures(found))
            {
                continue;
            }

            Dictionary<string, ReportLayer> these = new(StringComparer.OrdinalIgnoreCase);

            foreach (JsonObject layer in published)
            {
                string? url = (string?)layer[urlKey];

                if (url is null || !int.TryParse(url[(url.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    || found.LayerOrTable(index) is not { } one || CapabilityCeilings.Refuses(one, "Query"))
                {
                    continue;
                }

                (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(one, cancellation).ConfigureAwait(false);
                these[(string?)layer["repeat"] ?? string.Empty] = new ReportLayer(layer, one, source, described, url);
            }

            if (these.ContainsKey(string.Empty))
            {
                layers = these;
                break;
            }
        }

        if (!layers.TryGetValue(string.Empty, out ReportLayer? top))
        {
            return (null, Error(403, "You may not read this form's records, so no report can be made of them for you. Its owner can share its results with you, or turn on its Inbox."), null);
        }

        IReadOnlySet<string> fileQuestions = template.FileQuestions(schema);
        Dictionary<string, (ReportLayer Layer, IAttachmentStore? Store)> stores = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string repeat, ReportLayer layer) in layers)
        {
            FormTable table = repeat.Length == 0 ? schema.Root : schema.Root.AllTables.FirstOrDefault(t => string.Equals(t.Name, repeat, StringComparison.OrdinalIgnoreCase)) ?? schema.Root;
            bool wanted = template.ReadsAllFiles || table.Questions.Any(q => fileQuestions.Contains(q.Name));
            IAttachmentStore? store = null;

            if (wanted && layer.Layer.KeepsAttachments)
            {
                (store, _, _) = await AttachmentEndpoints.StoreOfAsync(context, layer.Layer, connections, cancellation).ConfigureAwait(false);
            }

            stores[repeat] = (layer, store);
        }

        long fileBytes = 0;
        List<ReportRecord> tops = await ReadRowsAsync(top, schema.Root, identities: ids, parents: null).ConfigureAwait(false);
        Dictionary<long, ReportRecord> byId = tops.ToDictionary(r => r.ObjectId);
        long[] missing = [.. ids.Where(i => !byId.ContainsKey(i))];

        if (missing.Length > 0)
        {
            return (null, Error(404, $"{(missing.Length == 1 ? "Record" : "Records")} {string.Join(", ", missing.Take(20))} of this form {(missing.Length == 1 ? "is" : "are")} not there for you to read."), null);
        }

        // What a query of the report reads its records by, as these were read: the same layers, the same files.
        ReportReading reading = new(layers, async (string tableName, long[] wanted) =>
        {
            if (!layers.TryGetValue(tableName, out ReportLayer? layer))
            {
                return [];
            }

            FormTable table = tableName.Length == 0 ? schema.Root
                : schema.Root.AllTables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)) ?? schema.Root;
            List<ReportRecord> rows = await ReadRowsAsync(layer, table, identities: wanted, parents: null).ConfigureAwait(false);
            Dictionary<long, ReportRecord> found = rows.ToDictionary(r => r.ObjectId);
            return [.. wanted.Where(found.ContainsKey).Select(i => found[i])];
        });

        return ([.. ids.Select(i => byId[i])], null, reading);

        async Task<List<ReportRecord>> ReadRowsAsync(ReportLayer layer, FormTable table, long[]? identities, IReadOnlyList<(Guid Global, Dictionary<string, List<ReportRecord>> Into)>? parents)
        {
            List<(Feature Feature, Dictionary<string, List<ReportRecord>> Repeats)> read = [];
            string identity = layer.Layer.Definition.IdentityColumn;
            List<string> fields = [.. layer.Described.Fields.Select(f => f.Name)];
            bool geometry = layer.Layer.Definition.GeometryColumn is { Length: > 0 };

            if (identities is not null)
            {
                foreach (long[] batch in identities.Chunk(500))
                {
                    FeatureQuery query = new(batch.Length, fields: fields, includeGeometry: geometry, identities: batch, outSrid: geometry ? 4326 : null);

                    await foreach (Feature f in layer.Source.ReadAsync(query, cancellation).ConfigureAwait(false))
                    {
                        read.Add((f, new Dictionary<string, List<ReportRecord>>(StringComparer.OrdinalIgnoreCase)));
                    }
                }
            }
            else
            {
                Dictionary<string, FieldType> types = layer.Described.Fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.OrdinalIgnoreCase);

                foreach (var batch in parents!.Chunk(50))
                {
                    string where = $"{FormLayerPlan.ParentColumn} IN ({string.Join(", ", batch.Select(p => $"'{p.Global.ToString("B").ToUpperInvariant()}'"))})";

                    if (!WhereClause.TryParse(where, fields, LayerDefinition.Quote, out ParsedWhere parsed, out string? error, types))
                    {
                        throw new InvalidOperationException($"The rows of '{table.Name}' could not be asked for: {error}");
                    }

                    FeatureQuery query = new(FeatureQuery.MaximumLimit, fields: fields, includeGeometry: geometry, where: parsed, outSrid: geometry ? 4326 : null,
                        orderBy: [new Graticula.Features.SortKey(identity, Descending: false)]);

                    await foreach (Feature f in layer.Source.ReadAsync(query, cancellation).ConfigureAwait(false))
                    {
                        read.Add((f, new Dictionary<string, List<ReportRecord>>(StringComparer.OrdinalIgnoreCase)));
                    }
                }
            }

            // Each row's answers by question name, from the column publishing gave the question.
            JsonObject columns = layer.Published["columns"] as JsonObject ?? [];
            List<ReportRecord> records = [];
            IAttachmentStore? withStore = stores.TryGetValue(table.Name, out (ReportLayer Layer, IAttachmentStore? Store) found) ? found.Store : null;

            foreach ((Feature feature, Dictionary<string, List<ReportRecord>> repeats) in read)
            {
                Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);

                foreach (string field in ReportSchema.LayerFields)
                {
                    if (feature.Schema.IndexOf(field) is var at && at >= 0)
                    {
                        values[field] = feature[at];
                    }
                }

                foreach (FormQuestion q in table.Questions)
                {
                    if ((string?)columns[q.Name] is { } column && feature.Schema.IndexOf(column) is var at && at >= 0)
                    {
                        values[q.Name] = feature[at];
                    }
                }

                long objectId = Convert.ToInt64(feature[feature.Schema.IndexOf(identity)], CultureInfo.InvariantCulture);
                List<ReportFile> files = [];

                if (withStore is { } store)
                {
                    foreach (AttachmentInfo info in await store.ListAsync(objectId, cancellation).ConfigureAwait(false))
                    {
                        bool used = template.ReadsAllFiles || fileQuestions.Contains(info.Keywords)
                            || (info.Keywords.Length == 0 && table.Questions.Count(ReportSchema.IsFiles) == 1 && table.Questions.Any(q => fileQuestions.Contains(q.Name)));
                        byte[]? content = null;

                        if (used && info.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            fileBytes += info.Size;

                            if (fileBytes > ReportTemplate.MaximumPictureBytes)
                            {
                                throw new ReportTemplateException($"The chosen records' pictures come to more than {ReportTemplate.MaximumPictureBytes / (1024 * 1024)} MB; choose fewer records.");
                            }

                            await using OpenAttachment? opened = await store.OpenAsync(info.Id, cancellation).ConfigureAwait(false);

                            if (opened is not null)
                            {
                                using MemoryStream copy = new();
                                await opened.Content.CopyToAsync(copy, cancellation).ConfigureAwait(false);
                                content = copy.ToArray();
                            }
                        }

                        files.Add(new ReportFile(info.Id, info.Name, info.ContentType, info.Size, info.Keywords, info.GlobalId, content));
                    }
                }

                records.Add(new ReportRecord
                {
                    Table = table.Name,
                    ObjectId = objectId,
                    Values = values,
                    Point = feature.Geometry is Point p ? (p.X, p.Y) : feature.Geometry is { } g && g.Envelope is { } e && !e.IsEmpty ? ((e.MinX + e.MaxX) / 2, (e.MinY + e.MaxY) / 2) : null,
                    Shape = feature.Geometry is { IsEmpty: false } shape ? shape : null,
                    Repeats = repeats.ToDictionary(r => r.Key, r => (IReadOnlyList<ReportRecord>)r.Value, StringComparer.OrdinalIgnoreCase),
                    Files = files,
                });
            }

            // Each repeat's rows, read by their parent's GlobalID, and given to their parents.
            for (int i = 0; i < records.Count; i++)
            {
                foreach (FormTable repeat in table.Repeats)
                {
                    read[i].Repeats[repeat.Name] = [];
                }
            }

            foreach (FormTable repeat in table.Repeats)
            {
                if (!layers.TryGetValue(repeat.Name, out ReportLayer? child))
                {
                    continue;
                }

                List<(Guid, Dictionary<string, List<ReportRecord>>)> owners = [];

                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Values.GetValueOrDefault("globalid") is { } g && (g is Guid guid || Guid.TryParse(Convert.ToString(g, CultureInfo.InvariantCulture), out guid)))
                    {
                        owners.Add((guid, read[i].Repeats));
                    }
                }

                if (owners.Count == 0)
                {
                    continue;
                }

                foreach (ReportRecord row in await ReadRowsAsync(child, repeat, null, owners).ConfigureAwait(false))
                {
                    if (row.Values.GetValueOrDefault(FormLayerPlan.ParentColumn) is { } parent
                        && (parent is Guid pg || Guid.TryParse(Convert.ToString(parent, CultureInfo.InvariantCulture), out pg))
                        && owners.FirstOrDefault(o => o.Item1 == pg) is { Item2: { } into })
                    {
                        into[repeat.Name].Add(row);
                    }
                }
            }

            // The records were made before their repeats were read: give them the lists now filled.
            return [.. records.Select((r, i) => new ReportRecord
            {
                Table = r.Table,
                ObjectId = r.ObjectId,
                Values = r.Values,
                Point = r.Point,
                Shape = r.Shape,
                Files = r.Files,
                Repeats = read[i].Repeats.ToDictionary(x => x.Key, x => (IReadOnlyList<ReportRecord>)x.Value, StringComparer.OrdinalIgnoreCase),
            })];
        }
    }
}
