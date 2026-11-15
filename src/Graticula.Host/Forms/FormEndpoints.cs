using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Forms;

/// <summary>
/// Forms as items: added from a workbook or a model, checked, saved, downloaded, published, and read by the form client
/// — ADR-287 §5.1, §5.2, §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>A form is a document of the kind <c>Form</c></b> (ADR-264), whose content is its model's JSON, so it is shared,
/// listed, moved and deleted like every other item. What publishing made — the service, each layer's id and address,
/// each question's column — is the item resource <see cref="PublicationResource"/>, beside the model and not inside
/// it, because the model is the form's own lossless XLSForm and holds nothing of this server's.
/// </para>
/// <para>
/// <b>Publishing is Studio's work, run in this request.</b> Each layer is made by <c>/admin/hosted/define</c>'s own
/// handler, and its GlobalIDs and editor tracking by theirs (a hosted layer keeps attachments from the start), so a form's layers are refused and audited
/// exactly as hand-designed ones are. A step that fails removes what the earlier steps made: the service and the choice
/// lists this publish created. The relationships between a form's own new layers are declared here, by the publisher,
/// rather than through <c>/admin/relationships</c>, which an administrator alone may call: relating the tables a
/// publisher just made is part of making them (INFERRED, ADR-287 Q-230).
/// </para>
/// </remarks>
public static partial class FormEndpoints
{
    /// <summary>The item resource that records what publishing made.</summary>
    public const string PublicationResource = "graticula/publication.json";

    /// <summary>
    /// The item resource that holds the model as it was last published — what the form client is served, while the item's
    /// own model is the draft the designer saves (ADR-287 §5.13).
    /// </summary>
    public const string PublishedModelResource = "graticula/published-model.json";

    /// <summary>The most a form's workbook upload may be.</summary>
    public const long MaximumUploadBytes = 16L * 1024 * 1024;

    /// <summary>Maps the routes.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/forms", AddAsync).DisableAntiforgery();
        app.MapGet("/admin/forms/{id}", ReadAsync);
        app.MapPut("/admin/forms/{id}/model", SaveAsync);
        app.MapPost("/admin/forms/{id}/check", CheckUploadAsync).DisableAntiforgery();
        app.MapGet("/admin/forms/{id}/workbook", WorkbookAsync);
        app.MapPost("/admin/forms/{id}/publish", PublishAsync);
        app.MapPut("/admin/forms/{id}/sharing", ShareAsync);
        app.MapGet("/forms/{id}/form.json", ClientAsync);
        app.MapGet("/forms/{id:regex(^[0-9a-fA-F]{{32}}$)}", PageAsync);
        app.MapGet("/forms/preview", (HttpContext context, CancellationToken cancellation) => PageAsync(context, "preview", cancellation));

        // The map library the form's location questions draw with (§5.15), under /forms/ so the service worker keeps it
        // with the page and a form opened offline still has its map: the same files Studio loads.
        app.MapGet("/forms/ol.js", () => Library("ol.js", "text/javascript; charset=utf-8"));
        app.MapGet("/forms/ol.css", () => Library("ol.css", "text/css; charset=utf-8"));
    }

    /// <summary>
    /// <c>/forms/{itemId}</c>, the address §5.4 names: the client's page, <c>wwwroot/forms/fill.html</c>, which reads the
    /// id from its own path. The same file as <c>/forms/fill.html?id=…</c>, served rather than copied, so the two cannot
    /// drift; whether the caller may read the form is decided when the page asks for <c>form.json</c>.
    /// </summary>
    private static IResult Library(string file, string type)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", file);
        return File.Exists(path) ? Results.File(path, type) : Results.NotFound();
    }

    /// <remarks>
    /// <b><c>/forms/preview</c> is the same page</b>, for the designer in Studio to frame (ADR-287 §5.8): the page reads
    /// its address, takes the form it is sent rather than fetching one, and sends nothing. It is outside the service
    /// worker's shell on purpose — a page from its cache would carry the headers that refuse framing.
    /// </remarks>
    private static async Task<IResult> PageAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        _ = id;
        string page = Path.Combine(AppContext.BaseDirectory, "wwwroot", "forms", "fill.html");

        if (!File.Exists(page))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(await File.ReadAllTextAsync(page, cancellation).ConfigureAwait(false), "text/html; charset=utf-8");
    }

    // ------------------------------------------------------------------------------------------------- add, read, save

    private static async Task<IResult> AddAsync(HttpContext context, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        (FormModel? model, string? title, IResult? refused) = await ReadUploadAsync(context, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        FormCheck check = FormDefinition.Check(model!);

        if (!check.Accepted)
        {
            return Refusal(check.Refusals, check.Notices, "The form was not added.");
        }

        title ??= check.Definition!.Settings.Title ?? "Form";
        IServiceProvider services = context.RequestServices;
        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        DocumentKind kind = Documents.AppOfType("Form")!;
        byte[] content = Encoding.UTF8.GetBytes(model!.ToJson().ToJsonString());

        Document made = await documents.CreateAsync(title, null, current.Principal.Id, SharingScope.Private,
            Documents.CleanFileName(title + kind.Extension, kind), kind, content, cancellation).ConfigureAwait(false);
        await documents.SetLinkAsync(made.Id, null, ["Form", "Graticula Form", "XLSForm"], cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.create", made.Id,
            AdminEndpoints.Detail(new { title, questions = check.Definition!.AllQuestions.Count() }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;
        return Results.Json(new { id = made.Id, title, notices = Issues(check.Notices) }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "open", cancellation).ConfigureAwait(false) is not ({ } document, { } model))
        {
            return Missing(id);
        }

        FormCheck check = FormDefinition.Check(model);
        JsonNode? publication = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false);

        return Results.Json(new
        {
            id = document.Id,
            title = document.Title,
            sharing = document.Sharing.ToString().ToLowerInvariant(),
            model = model.ToJson(),
            changedSincePublished = publication is not null
                && await PublishedModelAsync(context, document.Id, cancellation).ConfigureAwait(false) is { } live && !live.SameAs(model),
            refusals = Issues(check.Refusals),
            notices = Issues(check.Notices),
            publication,
        });
    }

    private static async Task<IResult> SaveAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "change", cancellation).ConfigureAwait(false) is not ({ } document, _))
        {
            return Missing(id);
        }

        FormModel model;

        try
        {
            model = FormModel.FromJson(await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (e is JsonException or FormWorkbookException)
        {
            return Error(400, $"The model was not saved: {e.Message}");
        }

        FormCheck check = FormDefinition.Check(model);

        // §5.3: the same check as an upload, so the designer cannot save what an upload would refuse.
        if (!check.Accepted)
        {
            return Refusal(check.Refusals, check.Notices, "The form was not saved.");
        }

        // A published form is saved as its draft: the form client goes on being served the published model until the
        // form is published again (§5.13), so a saved change never sends answers to a column that is not there yet.
        bool published = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false) is not null;

        IServiceProvider services = context.RequestServices;
        await services.GetRequiredService<IDocumentStore>().ReplaceAsync(document.Id, document.FileName, document.Kind,
            Encoding.UTF8.GetBytes(model.ToJson().ToJsonString()), cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.save", document.Id,
            AdminEndpoints.Detail(new { questions = check.Definition!.AllQuestions.Count() }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(new
        {
            id = document.Id,
            notices = Issues(check.Notices),
            changedSincePublished = published
                && await PublishedModelAsync(context, document.Id, cancellation).ConfigureAwait(false) is { } live && !live.SameAs(model),
        });
    }

    /// <summary>Checks a workbook or a model against the list without storing it — the designer's and the upload screen's preview.</summary>
    private static async Task<IResult> CheckUploadAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        (FormModel? model, _, IResult? refused) = await ReadUploadAsync(context, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        FormCheck check = FormDefinition.Check(model!);
        // A stored form's plan is named as publishing will name it, after its title; anything else after the id given.
        string fallback = (await ManagedFormAsync(context, id, "check", cancellation).ConfigureAwait(false)).Document?.Title ?? id;
        FormLayerPlan? plan = check.Definition is { } definition ? FormLayerPlan.For(definition, fallback) : null;
        IReadOnlyList<FormIssue> refusals = [.. check.Refusals, .. plan?.Refusals ?? []];

        return Results.Json(new
        {
            accepted = refusals.Count == 0,
            refusals = Issues(refusals),
            notices = Issues(check.Notices),
            plan = plan is null ? null : PlanJson(plan),

            // The form as the client fills it in, for the designer's preview — what form.json serves once it is saved.
            form = refusals.Count == 0 && check.Definition is { } compiled ? FormCompiled.Json(compiled) : null,
        });
    }

    private static async Task<IResult> WorkbookAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "download", cancellation).ConfigureAwait(false) is not ({ } document, { } model))
        {
            return Missing(id);
        }

        using MemoryStream book = new();
        model.WriteWorkbook(book);
        string name = FormLayerPlan.Identifier(document.Title) is { Length: > 0 } safe ? safe : "form";

        return Results.File(book.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
    }

    // ---------------------------------------------------------------------------------------------------------- publish

    private static async Task<IResult> PublishAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (await ManagedFormAsync(context, id, "publish", cancellation).ConfigureAwait(false) is not ({ } document, { } model))
        {
            return Missing(id);
        }

        if (await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false) is JsonObject published)
        {
            return await RepublishAsync(context, document, model, published, cancellation).ConfigureAwait(false);
        }

        FormCheck check = FormDefinition.Check(model);

        if (!check.Accepted)
        {
            return Refusal(check.Refusals, check.Notices, "The form was not published.");
        }

        FormLayerPlan plan = FormLayerPlan.For(check.Definition!, document.Title);

        if (!plan.Accepted)
        {
            return Refusal(plan.Refusals, check.Notices, "The form was not published.");
        }

        if (await MissingPicturesAsync(context, document.Id, check.Definition!, cancellation).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            return Refusal(missing, check.Notices, "The form was not published.");
        }

        // ADR-287 §5.17: a form that names a layer writes to it and makes none.
        if (check.Definition!.Settings.SubmissionUrl is not null)
        {
            return await PublishToExistingAsync(context, document, model, check, plan, cancellation).ConfigureAwait(false);
        }

        Publisher publisher = new(context, plan, cancellation);
        (int status, string? message) = await publisher.RunAsync().ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return Error(status, message ?? "The form was not published.");
        }

        JsonObject publication = publisher.Publication(check.Definition!);
        IServiceProvider services = context.RequestServices;
        await services.GetRequiredService<IItemResourceStore>().WriteAsync(Guid.Parse(document.Id), PublicationResource, "application/json",
            Encoding.UTF8.GetBytes(publication.ToJsonString()), "inherit", replace: false, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        await services.GetRequiredService<IDocumentStore>().SetLinkAsync(document.Id, (string?)publication["featureServer"],
            ["Form", "Graticula Form", "XLSForm"], cancellation).ConfigureAwait(false);
        await WritePublishedModelAsync(context, document.Id, model, cancellation).ConfigureAwait(false);

        await FileAsync(context, document, plan.ServiceName, document.Title, cancellation).ConfigureAwait(false);

        // A form shared before it was published opens its answers now, as sharing it afterwards would have.
        if (document.Sharing is SharingScope.Organization or SharingScope.Public or SharingScope.Group)
        {
            (int opened, string? why) = await OpenAnswersAsync(context, document, publication, document.Sharing, cancellation).ConfigureAwait(false);

            if (opened is < 200 or >= 300)
            {
                return Error(opened, $"The form was published, but its answers could not be opened to its sharing: {why ?? "no reason given"}. Share it again.");
            }
        }
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.publish", document.Id,
            AdminEndpoints.Detail(new { service = plan.ServiceName, layers = plan.Layers.Count }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(publication);
    }

    /// <summary>One publish: makes the plan's choice lists, layers, GlobalIDs, tracking, attachments and relationships.</summary>
    private sealed class Publisher(HttpContext context, FormLayerPlan plan, CancellationToken cancellation)
    {
        private readonly IServiceProvider services = context.RequestServices;
        private readonly List<Guid> createdDomains = [];
        private readonly Dictionary<string, Guid> domainIds = new(StringComparer.Ordinal);
        private readonly List<(PlannedLayer Layer, Guid Id, string Url)> made = [];
        private readonly Dictionary<string, int> relationshipNumbers = new(StringComparer.Ordinal);

        public async Task<(int Status, string? Message)> RunAsync()
        {
            try
            {
                (int status, string? message) = await DomainsAsync().ConfigureAwait(false);

                if (status == 200)
                {
                    (status, message) = await LayersAsync().ConfigureAwait(false);
                }

                if (status == 200)
                {
                    (status, message) = await FinishLayersAsync().ConfigureAwait(false);
                }

                if (status == 200)
                {
                    await RelateAsync().ConfigureAwait(false);
                }
                else
                {
                    await UndoAsync().ConfigureAwait(false);
                }

                return (status, message);
            }
            catch
            {
                await UndoAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(int, string?)> DomainsAsync()
        {
            IFieldDomainStore store = services.GetRequiredService<IFieldDomainStore>();
            RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

            foreach (PlannedDomain planned in plan.Domains)
            {
                FieldDomain domain = FieldDomain.Coded(planned.Name, planned.Codes);

                if (await store.FindByNameAsync(planned.Name, cancellation).ConfigureAwait(false) is { } existing)
                {
                    // The same list published before — another copy of the form, say — is shared rather than refused.
                    if (existing.Domain.Kind == DomainKind.CodedValue
                        && existing.Domain.Codes.Select(c => (c.Code.ToString(), c.Name)).SequenceEqual(planned.Codes.Select(c => (c.Code.ToString(), c.Name))))
                    {
                        domainIds[planned.List] = existing.Id;
                        continue;
                    }

                    return (409, $"A shared domain named '{planned.Name}' already exists with other choices, and the choice list '{planned.List}' would become it. Give the form another form_id.");
                }

                SharedDomain? created = await store.CreateAsync(domain, current.Principal.Id, cancellation).ConfigureAwait(false);

                if (created is null)
                {
                    return (409, $"A shared domain named '{planned.Name}' was created by somebody else meanwhile; publish again.");
                }

                createdDomains.Add(created.Id);
                domainIds[planned.List] = created.Id;
            }

            return (200, null);
        }

        private async Task<(int, string?)> LayersAsync()
        {
            PostGisImporter importer = services.GetRequiredService<PostGisImporter>();
            IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();
            IFieldDomainStore domains = services.GetRequiredService<IFieldDomainStore>();
            IAuditLog audit = services.GetRequiredService<IAuditLog>();

            foreach (PlannedLayer layer in plan.Layers)
            {
                List<HostedDataEndpoints.FieldDesign> fields = [.. layer.Fields.Select(f => new HostedDataEndpoints.FieldDesign(
                    f.Column, f.Type.ToString(), true, f.Length, f.Domain is { } d ? domainIds[d.List] : null))];

                if (layer.ParentRepeat is not null)
                {
                    fields.Add(new HostedDataEndpoints.FieldDesign(FormLayerPlan.ParentColumn, "Guid", true));
                }

                HostedDataEndpoints.LayerDesign design = new(
                    layer.Name, layer.GeometryType ?? "Table", fields, "private", plan.ServiceName,
                    Srid: layer.GeometryType is null ? null : FormLayerPlan.Srid);

                (int status, string? message, JsonElement answer) = await PortalEndpoints.InnerAnswerOfAsync(context, () =>
                    HostedDataEndpoints.DefineAsync(context, design, importer, catalog, domains, audit, cancellation)).ConfigureAwait(false);

                if (status is < 200 or >= 300)
                {
                    return (status, $"The layer '{layer.Name}' was not made: {message ?? "no reason given"}. Nothing of the form was kept.");
                }

                Guid id = answer.GetProperty("id").GetGuid();
                string url = answer.TryGetProperty("services", out JsonElement links) && links.TryGetProperty("feature", out JsonElement feature)
                    ? feature.GetString() ?? string.Empty
                    : string.Empty;
                made.Add((layer, id, url));
            }

            return (200, null);
        }

        private async Task<(int, string?)> FinishLayersAsync()
        {
            PostgresLayerCatalog layers = services.GetRequiredService<PostgresLayerCatalog>();
            PostGisImporter importer = services.GetRequiredService<PostGisImporter>();
            ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
            ITileCache tiles = services.GetRequiredService<ITileCache>();
            IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();
            IAuditLog audit = services.GetRequiredService<IAuditLog>();
            LayerConnections connections = services.GetRequiredService<LayerConnections>();

            foreach ((PlannedLayer layer, Guid id, _) in made)
            {
                // Aliases first, merged into what the define left (the domains), before tracking adds its own.
                if (await layers.FindByIdAsync(id, cancellation).ConfigureAwait(false) is { } found)
                {
                    List<FieldOverride> overrides = [.. found.FieldOverrides];

                    foreach (PlannedField field in layer.Fields)
                    {
                        string column = PostGisImporter.ColumnNameFor(field.Column);
                        int at = overrides.FindIndex(o => o.Matches(column));

                        if (at >= 0)
                        {
                            overrides[at] = overrides[at] with { Alias = field.Alias };
                        }
                        else if (!string.Equals(field.Alias, column, StringComparison.Ordinal))
                        {
                            overrides.Add(new FieldOverride(column, field.Alias, Hidden: false));
                        }
                    }

                    await catalog.SetFieldOverridesAsync(id, overrides, cancellation).ConfigureAwait(false);
                }

                foreach ((string step, Func<Task> handler) in new (string, Func<Task>)[]
                {
                    ("GlobalIDs", () => HostedDataEndpoints.AddGlobalIdsAsync(context, layer.Name, layers, importer, contexts, tiles, catalog, audit, cancellation)),
                    ("editor tracking", () => HostedDataEndpoints.TrackEditsAsync(context, layer.Name, layers, importer, contexts, tiles, catalog, audit, connections, cancellation)),
                })
                {
                    (int status, string? message) = await PortalEndpoints.InnerStatusAsync(context, handler).ConfigureAwait(false);

                    if (status is < 200 or >= 300)
                    {
                        return (status, $"The layer '{layer.Name}' could not be given {step}: {message ?? "no reason given"}. Nothing of the form was kept.");
                    }
                }

                // Attachments need no step: a hosted layer keeps them beside its table from the start, and the switch
                // says so (measured 2026-11-09, the first publish answered 409 "nothing to switch").
            }

            return (200, null);
        }

        private async Task RelateAsync()
        {
            PostgresRelationshipCatalog relationships = services.GetRequiredService<PostgresRelationshipCatalog>();

            foreach ((PlannedLayer layer, Guid id, _) in made.Where(m => m.Layer.ParentRepeat is not null))
            {
                (PlannedLayer parent, Guid parentId, _) = made.First(m => m.Layer.Repeat == layer.ParentRepeat);
                Guid relationship = await relationships.DeclareAsync(new LayerRelationship(
                    Guid.Empty, $"{parent.Name}_{layer.Repeat}", parentId, "globalid", id, FormLayerPlan.ParentColumn,
                    RelationshipCardinality.OneToMany, Composite: false), cancellation).ConfigureAwait(false);

                if (await relationships.FindAsync(relationship, cancellation).ConfigureAwait(false) is { Number: { } number })
                {
                    relationshipNumbers[layer.Repeat] = number;
                }
            }
        }

        private async Task UndoAsync()
        {
            if (made.Count > 0)
            {
                await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.DeleteServiceAsync(
                    context, plan.ServiceName, FeatureServerMetadataWriter.HostedFolder, true,
                    services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
                    services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
                    services.GetRequiredService<ITileCache>(), services.GetRequiredService<IAuditLog>(),
                    services.GetRequiredService<SourceQuiesce>(), services.GetRequiredService<RecycleBinService>(),
                    services.GetRequiredService<LayerConnections>(), CancellationToken.None)).ConfigureAwait(false);
                made.Clear();
            }

            IFieldDomainStore store = services.GetRequiredService<IFieldDomainStore>();

            foreach (Guid domain in createdDomains)
            {
                await store.DeleteAsync(domain, CancellationToken.None).ConfigureAwait(false);
            }

            createdDomains.Clear();
        }

        /// <summary>What was made, as the publication resource records it and the client reads it.</summary>
        public JsonObject Publication(FormDefinition definition)
        {
            string? service = made.Count > 0 ? made[0].Url : null;
            int slash = service?.LastIndexOf('/') ?? -1;

            return new JsonObject
            {
                ["service"] = plan.ServiceName,
                ["featureServer"] = slash > 0 ? service![..slash] : service,
                ["srid"] = FormLayerPlan.Srid,
                ["version"] = definition.Settings.Version,
                ["publishedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["layers"] = new JsonArray([.. made.Select(m => (JsonNode)new JsonObject
                {
                    ["repeat"] = m.Layer.Repeat,
                    ["name"] = m.Layer.Name,
                    ["id"] = m.Id.ToString(),
                    ["url"] = m.Url,
                    ["geometryType"] = m.Layer.GeometryType,
                    ["location"] = m.Layer.LocationQuestion,
                    ["parentRepeat"] = m.Layer.ParentRepeat,
                    ["relationshipId"] = relationshipNumbers.TryGetValue(m.Layer.Repeat, out int n) ? n : null,
                    ["columns"] = new JsonObject([.. m.Layer.Fields.Select(f =>
                        KeyValuePair.Create(f.Question, (JsonNode?)JsonValue.Create(PostGisImporter.ColumnNameFor(f.Column))))]),
                    ["attachments"] = new JsonArray([.. m.Layer.AttachmentQuestions.Select(q => (JsonNode?)JsonValue.Create(q))]),
                })]),
            };
        }
    }

    /// <summary>
    /// The pictures the form names that are not among the item's files — found when the form is published, because an
    /// upload comes before the item and its files exist (ADR-287 §5.10). A form never goes out with a picture that
    /// cannot be shown; each is listed with its sheet, row and column. A picture is looked for at <c>media/name</c>,
    /// the folder Survey123 keeps them in, and at <c>name</c>.
    /// </summary>
    private static async Task<List<FormIssue>> MissingPicturesAsync(HttpContext context, string id, FormDefinition definition, CancellationToken cancellation)
    {
        List<(string Sheet, int Row, string Column, string Name)> named = [.. definition.MediaReferences()];

        if (named.Count == 0)
        {
            return [];
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        (IReadOnlyList<ItemResource> page, _) = await resources.ListAsync(Guid.Parse(id), 0, 10_000, cancellation).ConfigureAwait(false);
        HashSet<string> paths = new(page.Select(r => r.Path), StringComparer.Ordinal);

        return [.. named.Where(n => !paths.Contains("media/" + n.Name) && !paths.Contains(n.Name)).Select(n => new FormIssue(n.Sheet, n.Row, n.Column,
            $"The picture '{n.Name}' is not among this form's files. Add it to the item's files as media/{n.Name}."))];
    }

    private static async Task<FormModel?> PublishedModelAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        return await resources.FindAsync(Guid.Parse(id), PublishedModelResource, cancellation).ConfigureAwait(false) is { } found
            ? FormModel.FromJson(JsonNode.Parse(found.Content))
            : null;
    }

    /// <summary>Keeps the model as published: replaced when one is kept, added the first time — a replace finds nothing to replace then.</summary>
    private static async Task WritePublishedModelAsync(HttpContext context, string id, FormModel model, CancellationToken cancellation)
    {
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        byte[] content = Encoding.UTF8.GetBytes(model.ToJson().ToJsonString());

        if (await resources.WriteAsync(Guid.Parse(id), PublishedModelResource, "application/json", content, "inherit", replace: true,
                100L * 1024 * 1024, cancellation).ConfigureAwait(false) == ItemResourceWrite.NotThere)
        {
            await resources.WriteAsync(Guid.Parse(id), PublishedModelResource, "application/json", content, "inherit", replace: false,
                100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------------------------------------- republish

    /// <summary>
    /// Publishes a published form again, as Survey123 does: a new question becomes a new field, a question taken out keeps
    /// its field and what was answered in it, and labels, choices and rules change freely — ADR-287 §5.13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything that would be refused is found before anything is changed</b>: a question whose field already exists
    /// with another type, a changed <c>form_id</c> (the layer's name, which every address holds), and a new repeat (a new
    /// table and a relationship inside a live service — not built). The answer lists them all, not the first.
    /// </para>
    /// <para>
    /// <b>The answers view is made again</b> when the form is shared: a view's columns are fixed when it is made
    /// (ADR-113 §3), so a new field would not reach it. It holds no data of its own, so dropping and making it loses
    /// nothing; for the seconds between, a submission is refused and waits in the device's outbox.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RepublishAsync(
        HttpContext context, Document document, FormModel model, JsonObject publication, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        FormCheck check = FormDefinition.Check(model);

        if (!check.Accepted)
        {
            return Refusal(check.Refusals, check.Notices, "The form was not published again.");
        }

        FormLayerPlan plan = FormLayerPlan.For(check.Definition!, document.Title);

        if (!plan.Accepted)
        {
            return Refusal(plan.Refusals, check.Notices, "The form was not published again.");
        }

        if (await MissingPicturesAsync(context, document.Id, check.Definition!, cancellation).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            return Refusal(missing, check.Notices, "The form was not published again.");
        }

        if (await RepublishToExistingAsync(context, document, model, check, plan, publication, cancellation).ConfigureAwait(false) is { } handled)
        {
            return handled;
        }

        string service = (string?)publication["service"] ?? string.Empty;
        List<FormIssue> refused = [];

        if (!string.Equals(plan.ServiceName, service, StringComparison.Ordinal))
        {
            refused.Add(new FormIssue("settings", 2, "form_id",
                $"The layer is named '{service}' and cannot be renamed once published; this form would name it '{plan.ServiceName}'. Put the form_id back, or publish a copy."));
        }

        PostgresLayerCatalog catalogue = services.GetRequiredService<PostgresLayerCatalog>();
        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        JsonArray existing = publication["layers"] as JsonArray ?? [];
        List<(PlannedLayer Layer, JsonObject Published, PublishedLayer Found, List<PlannedField> Added)> layers = [];

        foreach (PlannedLayer layer in plan.Layers)
        {
            JsonObject? published = existing.OfType<JsonObject>().FirstOrDefault(l => ((string?)l["repeat"] ?? "") == layer.Repeat);

            if (published is null || !Guid.TryParse((string?)published["id"], out Guid layerId)
                || await catalogue.FindByIdAsync(layerId, cancellation).ConfigureAwait(false) is not { } found)
            {
                refused.Add(new FormIssue("survey", 0, "type", layer.Repeat.Length == 0
                    ? "The form's main layer is no longer there, so it cannot be published again. Publish a copy."
                    : $"The repeat '{layer.Repeat}' is new, and a new repeat needs a new table inside the published service, which publishing again does not make yet. Publish a copy, or take the repeat out."));
                continue;
            }

            (_, Graticula.Features.LayerDescription shape) = await contexts.GetAsync(found, cancellation).ConfigureAwait(false);
            List<PlannedField> added = [];

            foreach (PlannedField field in layer.Fields)
            {
                string column = PostGisImporter.ColumnNameFor(field.Column);

                if (shape.Find(column) is { } now)
                {
                    if (now.Type != field.Type)
                    {
                        refused.Add(new FormIssue("survey", 0, "type",
                            $"The question '{field.Question}' is stored in '{column}', a {now.Type} field, and this form would make it {field.Type}. A published field keeps its type: change the question back, or give it a new name so it gets a field of its own."));
                    }
                }
                else
                {
                    added.Add(field);
                }
            }

            layers.Add((layer, published, found, added));
        }

        if (refused.Count > 0)
        {
            return Refusal(refused, check.Notices, "The form was not published again.");
        }

        // Choice lists: a new one is made; a changed one keeps every code it had, so an answer already given stays valid.
        IFieldDomainStore domains = services.GetRequiredService<IFieldDomainStore>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Dictionary<string, Guid> domainIds = new(StringComparer.Ordinal);

        foreach (PlannedDomain planned in plan.Domains)
        {
            if (await domains.FindByNameAsync(planned.Name, cancellation).ConfigureAwait(false) is { } found)
            {
                List<CodedValue> codes = [.. planned.Codes];
                codes.AddRange(found.Domain.Codes.Where(old => !planned.Codes.Any(c => c.Code.ToString() == old.Code.ToString())));

                if (found.Domain.Kind == DomainKind.CodedValue
                    && !codes.Select(c => (c.Code.ToString(), c.Name)).SequenceEqual(found.Domain.Codes.Select(c => (c.Code.ToString(), c.Name))))
                {
                    await domains.UpdateAsync(found.Id, FieldDomain.Coded(planned.Name, codes), cancellation).ConfigureAwait(false);
                }

                domainIds[planned.List] = found.Id;
            }
            else if (await domains.CreateAsync(FieldDomain.Coded(planned.Name, planned.Codes), current.Principal.Id, cancellation).ConfigureAwait(false) is { } created)
            {
                domainIds[planned.List] = created.Id;
            }
        }

        // The answers view goes first, so the source takes new fields with no view depending on them, and comes back last
        // — made again whatever happened between, so a shared form never stays without one.
        string? view = (string?)publication["submitService"];

        if (view is not null)
        {
            await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.DeleteServiceAsync(
                context, view, FeatureServerMetadataWriter.HostedFolder, true,
                services.GetRequiredService<IAdminCatalog>(), catalogue, services.GetRequiredService<PostGisImporter>(), contexts,
                services.GetRequiredService<ITileCache>(), services.GetRequiredService<IAuditLog>(),
                services.GetRequiredService<SourceQuiesce>(), services.GetRequiredService<RecycleBinService>(),
                services.GetRequiredService<LayerConnections>(), cancellation)).ConfigureAwait(false);
            publication.Remove("submitService");

            foreach (JsonObject layer in existing.OfType<JsonObject>())
            {
                layer.Remove("submitUrl");
            }
        }

        (int status, string? message) = (200, null);
        int fieldsAdded = 0;

        try
        {
            IAdminCatalog admin = services.GetRequiredService<IAdminCatalog>();

            foreach ((PlannedLayer layer, JsonObject published, PublishedLayer found, List<PlannedField> added) in layers)
            {
                foreach (PlannedField field in added)
                {
                    HostedDataEndpoints.FieldDesign design = new(field.Column, field.Type.ToString(), true, field.Length,
                        field.Domain is { } d && domainIds.TryGetValue(d.List, out Guid domain) ? domain : null);
                    (status, message) = await PortalEndpoints.InnerStatusAsync(context, () => HostedDataEndpoints.AddFieldToAsync(
                        context, found, design, services.GetRequiredService<PostGisImporter>(), contexts,
                        services.GetRequiredService<ITileCache>(), admin, services.GetRequiredService<IAuditLog>(), cancellation)).ConfigureAwait(false);

                    if (status is < 200 or >= 300)
                    {
                        message = $"The field for '{field.Question}' was not added to '{layer.Name}': {message ?? "no reason given"}. The fields added before it are kept, empty; publish again to finish.";
                        break;
                    }

                    fieldsAdded++;
                }

                if (status is < 200 or >= 300)
                {
                    break;
                }

                // Every label is the field's alias again, the new fields' and the changed ones'.
                if (await catalogue.FindByIdAsync(found.Id, cancellation).ConfigureAwait(false) is { } fresh)
                {
                    List<FieldOverride> overrides = [.. fresh.FieldOverrides];

                    foreach (PlannedField field in layer.Fields)
                    {
                        string column = PostGisImporter.ColumnNameFor(field.Column);
                        int at = overrides.FindIndex(o => o.Matches(column));

                        if (at >= 0)
                        {
                            overrides[at] = overrides[at] with { Alias = field.Alias };
                        }
                        else if (!string.Equals(field.Alias, column, StringComparison.Ordinal))
                        {
                            overrides.Add(new FieldOverride(column, field.Alias, Hidden: false));
                        }
                    }

                    await admin.SetFieldOverridesAsync(found.Id, overrides, cancellation).ConfigureAwait(false);
                }

                // What the client reads: the columns of the questions the form asks now. A question taken out leaves its
                // field and its answers in the layer, and is no longer sent to.
                published["columns"] = new JsonObject([.. layer.Fields.Select(f =>
                    KeyValuePair.Create(f.Question, (JsonNode?)JsonValue.Create(PostGisImporter.ColumnNameFor(f.Column))))]);
                published["attachments"] = new JsonArray([.. layer.AttachmentQuestions.Select(q => (JsonNode?)JsonValue.Create(q))]);
                published["location"] = layer.LocationQuestion;
            }
        }
        finally
        {
            publication["version"] = check.Definition!.Settings.Version;
            publication["republishedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            await services.GetRequiredService<IItemResourceStore>().WriteAsync(Guid.Parse(document.Id), PublicationResource, "application/json",
                Encoding.UTF8.GetBytes(publication.ToJsonString()), "inherit", replace: true, 100L * 1024 * 1024, CancellationToken.None).ConfigureAwait(false);

            if (view is not null)
            {
                (int opened, string? why) = await OpenAnswersAsync(context, document, publication, document.Sharing, CancellationToken.None).ConfigureAwait(false);

                if (opened is < 200 or >= 300 && status is >= 200 and < 300)
                {
                    (status, message) = (opened, $"The form was published again, but its answers view could not be made again: {why ?? "no reason given"}. Share it again.");
                }
            }
        }

        if (status is < 200 or >= 300)
        {
            return Error(status, message ?? "The form was not published again.");
        }

        await WritePublishedModelAsync(context, document.Id, model, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.republish", document.Id,
            AdminEndpoints.Detail(new { service, fieldsAdded }), succeeded: true, cancellation).ConfigureAwait(false);

        publication["fieldsAdded"] = fieldsAdded;
        return Results.Json(publication);
    }

    // ---------------------------------------------------------------------------------------------------------- sharing

    /// <summary>A form's sharing: who may open it, and where their answers go.</summary>
    /// <param name="Sharing"><c>private</c>, <c>group</c>, <c>organization</c> or <c>public</c>.</param>
    /// <param name="Groups">For <c>group</c>: the groups it is shared with, by name — the whole set, not a change.</param>
    internal sealed record FormSharingRequest(string? Sharing, IReadOnlyList<string>? Groups = null);

    /// <summary>
    /// Shares a form, and with it a view of its layers that takes new records and nothing else — ADR-287 §5.1, ADR-113.
    /// </summary>
    /// <remarks>
    /// <b>Survey123's arrangement.</b> The layers stay their publisher's; the people the form is shared with answer
    /// through a view whose only capability is <c>Create</c>, shared as the form is, so a public survey takes answers
    /// from anyone and shows nobody the answers. Each layer of the publication gains its <c>submitUrl</c> in that view;
    /// the client sends there when it has one. A private form has no view to send through, and its owner writes to the
    /// layers. Taking a form back to private takes its view back to private too.
    /// </remarks>
    private static async Task<IResult> ShareAsync(HttpContext context, string id, FormSharingRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        // enum-default-is-deliberate: refused. A form shared to all organizations would need its submit view shared the same
        // way, which is not built (ADR-287 §5.11 (l)); that scope is refused with a sentence, not mapped. Groups since §5.14.
        SharingScope scope = request.Sharing?.ToLowerInvariant() switch
        {
            "private" => SharingScope.Private,
            "group" => SharingScope.Group,
            "organization" => SharingScope.Organization,
            "public" => SharingScope.Public,
            _ => (SharingScope)(-1),
        };

        if (!Enum.IsDefined(scope))
        {
            return Error(400, "A form is shared as 'private', 'group', 'organization' or 'public'.");
        }

        List<string> wanted = [.. (request.Groups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (scope == SharingScope.Group && wanted.Count == 0)
        {
            return Error(400, "A form shared with groups names at least one group in 'groups'.");
        }

        if (scope == SharingScope.Group && !await Authorize.RequireAsync(context, Privilege.GroupsShareTo).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (scope == SharingScope.Public && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (await ManagedFormAsync(context, id, "share", cancellation).ConfigureAwait(false) is not ({ } document, _))
        {
            return Missing(id);
        }

        IServiceProvider services = context.RequestServices;
        JsonObject? publication = (await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false)) as JsonObject;
        string word = scope.ToString().ToLowerInvariant();
        IReadOnlyList<string> had = document.SharedWithNames ?? [];

        // The form's own groups first: a group the caller may not share with refuses here, before any view changes.
        if (scope == SharingScope.Group)
        {
            (int grouped, string? why) = await ShareGroupsAsync(context, had, wanted, (name, on) =>
                services.GetRequiredService<IGroupDirectory>().ShareDocumentAsync(Caller(context).Principal.Id,
                    Caller(context).Authorization.Allows(Privilege.AdminManageAllContent), name, document.Id, on, cancellation)).ConfigureAwait(false);

            if (grouped != 200)
            {
                return Error(grouped, $"The form was not shared with its groups: {why}. Its sharing was not changed.");
            }
        }

        if (publication is not null)
        {
            (int status, string? message) = await OpenAnswersAsync(context, document, publication, scope, cancellation,
                scope == SharingScope.Group ? wanted : null, had).ConfigureAwait(false);

            if (status is < 200 or >= 300)
            {
                return Error(status, $"The form's answers could not be opened to '{word}': {message ?? "no reason given"}. Its sharing was not changed.");
            }
        }

        await services.GetRequiredService<IDocumentStore>().UpdateAsync(document.Id, document.Title, document.Snippet, scope, cancellation)
            .ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.share", document.Id,
            AdminEndpoints.Detail(new { sharing = word, view = (string?)publication?["submitService"] }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(new { id = document.Id, sharing = word, groups = scope == SharingScope.Group ? wanted : [], publication });
    }

    private static RequestPrincipal Caller(HttpContext context) => context.Features.Get<RequestPrincipal>()!;

    /// <summary>
    /// Brings one item's groups to the set wanted: shares with each one added and stops with each one left out — §5.14.
    /// </summary>
    /// <returns>200, or the refusal's status and its reason, naming the group.</returns>
    private static async Task<(int Status, string? Message)> ShareGroupsAsync(
        HttpContext context, IReadOnlyList<string> had, IReadOnlyList<string> wanted, Func<string, bool, Task<GroupChange>> share)
    {
        _ = context;
        HashSet<string> before = new(had, StringComparer.OrdinalIgnoreCase);
        HashSet<string> after = new(wanted, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, bool on) in after.Where(g => !before.Contains(g)).Select(g => (g, true))
                     .Concat(before.Where(g => !after.Contains(g)).Select(g => (g, false))))
        {
            GroupChange outcome = await share(name, on).ConfigureAwait(false);

            if (outcome != GroupChange.Done)
            {
                return (outcome == GroupChange.Absent ? 404 : 403,
                    outcome == GroupChange.Absent ? $"there is no group '{name}'" : $"you may not share with the group '{name}'");
            }
        }

        return (200, null);
    }

    /// <summary>
    /// Opens a published form's answers to a sharing scope: makes its Create-only submit view, or reshares the one it has,
    /// and records each layer's <c>submitUrl</c>. Called when a form is shared and when an already shared form is
    /// published, so a form shared before it was published takes answers too (found by the Studio screens 2026-11-09).
    /// </summary>
    private static async Task<(int Status, string? Message)> OpenAnswersAsync(
        HttpContext context, Document document, JsonObject publication, SharingScope scope, CancellationToken cancellation,
        IReadOnlyList<string>? groups = null, IReadOnlyList<string>? hadGroups = null)
    {
        IServiceProvider services = context.RequestServices;
        string word = scope.ToString().ToLowerInvariant();
        string service = (string?)publication["service"] ?? string.Empty;
        string view = (string?)publication["submitService"] ?? ViewNameFor(service);

        // A form that writes to a layer it did not make opens its answers over the layers it writes to, not over every
        // layer of a service somebody else designed (§5.17). A form that made its service uses every hosted layer of it.
        int[]? only = IsExisting(publication) ? LayerNumbers(publication) : null;
        bool exists = publication["submitService"] is not null;
        (int status, string? message) = (200, null);

        if (!exists && scope != SharingScope.Private)
        {
            // <b>Made private, its tiles turned off, and only then shared — found 2026-11-09 on dev.</b> A view is a
            // hosted service, and a hosted service draws vector tiles by default; until the owner's Q-231 decision the
            // same day tiles answered to servesTiles alone, so the Create-only view handed every submitted answer, with
            // its point, to anyone who asked for a tile. Query off now turns tiles off anyway (ADR-031 §2a); this is the
            // second lock, set before the view is shared so there is no moment when it is open with them on. Written to the catalogue directly: the publisher made this view as a step of
            // publishing, and the capabilities endpoint asks for server administration.
            AdminEndpoints.ViewRequest asked = new(view, only, null, ["Create"], "private");
            (status, message) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.CreateViewAsync(
                context, service, FeatureServerMetadataWriter.HostedFolder, asked,
                services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
                services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
                services.GetRequiredService<IAuditLog>(), cancellation)).ConfigureAwait(false);

            if (status is >= 200 and < 300
                && !await services.GetRequiredService<IAdminCatalog>().SetServiceCapabilitiesAsync(
                    view, FeatureServerMetadataWriter.HostedFolder, new ServiceCapabilityLimits(null, false, ["Create"], null),
                    cancellation).ConfigureAwait(false))
            {
                (status, message) = (500, $"The answers view '{view}' was made but its tiles could not be turned off, so it was not shared.");
            }
        }

        if (status is >= 200 and < 300 && (exists || scope != SharingScope.Private))
        {
            (status, message) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.SetServiceSharingAsync(
                context, view, FeatureServerMetadataWriter.HostedFolder, new SharingRequest(word),
                services.GetRequiredService<PostgresSystemServices>(), services.GetRequiredService<IAdminCatalog>(),
                services.GetRequiredService<IAuditLog>(), services.GetRequiredService<PostgresLayerCatalog>(), cancellation))
                .ConfigureAwait(false);
        }

        // §5.14: a view shared as 'group' is read by the members of the groups it is in — the form's own, and no other.
        if (status is >= 200 and < 300 && scope == SharingScope.Group)
        {
            IReadOnlyList<string> want = groups ?? document.SharedWithNames ?? [];
            IReadOnlyList<string> before = exists ? hadGroups ?? [] : [];
            (status, message) = await ShareGroupsAsync(context, before, want, (name, on) =>
                services.GetRequiredService<IGroupDirectory>().ShareAsync(Caller(context).Principal.Id,
                    Caller(context).Authorization.Allows(Privilege.AdminManageAllContent), name, view,
                    FeatureServerMetadataWriter.HostedFolder, on, cancellation)).ConfigureAwait(false);
        }

        if (status is < 200 or >= 300)
        {
            return (status, message);
        }

        if (!exists && scope != SharingScope.Private)
        {
            await FileAsync(context, document, view, document.Title + "_form", cancellation).ConfigureAwait(false);
            publication["submitService"] = view;

            foreach (JsonNode? layer in publication["layers"] as JsonArray ?? [])
            {
                string url = (string?)layer?["url"] ?? string.Empty;
                string index = url[(url.LastIndexOf('/') + 1)..];
                layer!["submitUrl"] = $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{view}/FeatureServer/{index}";
            }

            await services.GetRequiredService<IItemResourceStore>().WriteAsync(Guid.Parse(document.Id), PublicationResource, "application/json",
                Encoding.UTF8.GetBytes(publication.ToJsonString()), "inherit", replace: true, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        }

        return (200, null);
    }

    /// <summary>The longest folder title a member may have — the folder routes' own limit.</summary>
    private const int FolderTitleLength = 128;

    /// <summary>
    /// Titles one of a form's services and files it, with the form, in the form's folder — Survey123's arrangement,
    /// owner decision 2026-11-09 (ADR-287 §5.12).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="document">The form item.</param>
    /// <param name="service">The service's name in the hosted folder; its address does not change.</param>
    /// <param name="title">The item title it is given: the form's title, or the form's title and <c>_form</c> for the view.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="touchService">False when the service is somebody else's and only the form item is filed (§5.17).</param>
    /// <remarks>
    /// <para>
    /// <b>The folder is <c>Survey-</c> and the form's title, the owner's</b>, found by title when it exists and made when it
    /// does not, so publishing and sharing later land in the same one. Only the form's owner's items go in: a service
    /// somebody else made — an administrator publishing a member's form — stays where its own owner's content is, because
    /// a folder is one member's and Move refuses another's item (ADR-114).
    /// </para>
    /// <para>
    /// <b>The form item moves only from the root.</b> One its owner already filed in a folder of their own stays there —
    /// INFERRED: Survey123 always moves it, and a choice the owner made by hand is worth more than the convention.
    /// </para>
    /// <para>
    /// <b>Filing never fails a publish.</b> The services are made and work at their addresses whatever folder lists them;
    /// a title or a folder that could not be written leaves the item at the root under its name, which is where every
    /// form's items were before this.
    /// </para>
    /// </remarks>
    private static async Task FileAsync(
        HttpContext context, Document document, string service, string title, CancellationToken cancellation, bool touchService = true)
    {
        IServiceProvider services = context.RequestServices;
        IContentFolderStore folders = services.GetRequiredService<IContentFolderStore>();
        string named = title.Length > WebMaps.MaximumTitleLength ? title[..WebMaps.MaximumTitleLength] : title;
        string folderTitle = "Survey-" + document.Title.Trim();
        folderTitle = folderTitle.Length > FolderTitleLength ? folderTitle[..FolderTitleLength] : folderTitle;

        // A service the form only writes to (§5.17) is not retitled or moved: it was somebody's before the form.
        PublishedService? made = touchService
            ? await services.GetRequiredService<PostgresLayerCatalog>()
                .FindServiceAsync(FeatureServerMetadataWriter.HostedFolder, service, cancellation).ConfigureAwait(false)
            : null;

        if (made is not null)
        {
            await services.GetRequiredService<IAdminCatalog>()
                .SetServiceTitleAsync(service, FeatureServerMetadataWriter.HostedFolder, named, cancellation).ConfigureAwait(false);
        }

        ContentFolder? folder = await folders.CreateAsync(document.Owner, folderTitle, cancellation).ConfigureAwait(false)
            ?? (await folders.ListAsync(document.Owner, cancellation).ConfigureAwait(false))
                .FirstOrDefault(f => string.Equals(f.Title, folderTitle, StringComparison.OrdinalIgnoreCase));

        if (folder is null)
        {
            return;
        }

        if (made is not null && made.Owner == document.Owner)
        {
            await folders.MoveServiceAsync(made.Id, folder.Id, cancellation).ConfigureAwait(false);
        }

        if (document.ContentFolder is null || document.ContentFolder == folder.Id)
        {
            await folders.MoveDocumentAsync(document.Id, folder.Id, cancellation).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the client

    /// <summary>
    /// The form as the client fills it in: the definition, worked out once here so the browser never reads XLSForm's
    /// sheets, and the publication, so it knows where each answer goes. Readable by whoever the item is shared with.
    /// </summary>
    private static async Task<IResult> ClientAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IDocumentStore documents = context.RequestServices.GetRequiredService<IDocumentStore>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        string lower = (id ?? string.Empty).ToLowerInvariant();

        if (!WebMaps.IsId(lower) || await documents.FindAsync(lower, cancellation).ConfigureAwait(false) is not { } document
            || document.Kind.ArcGisType != "Form"
            || !LayerAccess.Evaluate(document.Sharing, document.Owner, current.Principal, current.Authorization, document.SharedWith).IsAllowed())
        {
            return TokenRequired.Applies(context) ? TokenRequired.Result(context)
                : Error(404, $"No form '{id}' that you can open. It may have been deleted, or it is not shared with you.");
        }

        // The published model when there is one — a draft saved since is not what the layers hold (§5.13). A form published
        // before the snapshot existed could not be changed after publishing, so its stored model is the published one.
        JsonNode? publication = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false);
        FormModel model = (publication is not null ? await PublishedModelAsync(context, document.Id, cancellation).ConfigureAwait(false) : null)
            ?? FormModel.FromJson(JsonNode.Parse(await documents.ContentAsync(document.Id, cancellation).ConfigureAwait(false) ?? []));
        FormCheck check = FormDefinition.Check(model);

        if (check.Definition is null)
        {
            return Error(409, "The form cannot be filled in: it no longer passes its check.");
        }

        return Results.Json(new { id = document.Id, title = document.Title, form = FormCompiled.Json(check.Definition), publication });
    }

    // ---------------------------------------------------------------------------------------------------------- helpers

    private static async Task<(FormModel? Model, string? Title, IResult? Refused)> ReadUploadAsync(HttpContext context, CancellationToken cancellation)
    {
        try
        {
            if (context.Request.HasFormContentType)
            {
                IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
                string? title = form["title"].ToString() is { Length: > 0 } t ? t.Trim() : null;

                if (form.Files.GetFile("file") is { } file)
                {
                    if (file.Length > MaximumUploadBytes)
                    {
                        return (null, null, Error(400, $"A form's workbook is at most {MaximumUploadBytes / 1048576} MB."));
                    }

                    await using Stream stream = file.OpenReadStream();
                    using MemoryStream copy = new();
                    await stream.CopyToAsync(copy, cancellation).ConfigureAwait(false);
                    copy.Position = 0;
                    return (FormModel.ReadWorkbook(copy), title ?? Path.GetFileNameWithoutExtension(file.FileName), null);
                }

                if (form["model"].ToString() is { Length: > 0 } json)
                {
                    return (FormModel.FromJson(JsonNode.Parse(json)), title, null);
                }

                return (null, null, Error(400, "Send the form as 'file', an XLSForm workbook, or as 'model', its JSON."));
            }

            JsonNode? body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellation).ConfigureAwait(false);
            string? named = body?["title"]?.GetValueKind() == JsonValueKind.String ? (string?)body["title"] : null;
            return (FormModel.FromJson(body?["model"]), named, null);
        }
        catch (Exception e) when (e is JsonException or FormWorkbookException or InvalidDataException)
        {
            return (null, null, Error(400, $"The form could not be read: {e.Message}"));
        }
    }

    private static async Task<(Document? Document, FormModel? Model)> ManagedFormAsync(HttpContext context, string id, string verb, CancellationToken cancellation)
    {
        IDocumentStore documents = context.RequestServices.GetRequiredService<IDocumentStore>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        string lower = (id ?? string.Empty).ToLowerInvariant();

        if (current.Principal.IsAnonymous || !WebMaps.IsId(lower)
            || await documents.FindAsync(lower, cancellation).ConfigureAwait(false) is not { } document
            || document.Kind.ArcGisType != "Form"
            || !LayerAccess.MayManage(document.Owner, current.Principal, current.Authorization))
        {
            _ = verb;
            return (null, null);
        }

        byte[] content = await documents.ContentAsync(document.Id, cancellation).ConfigureAwait(false) ?? [];

        try
        {
            return (document, FormModel.FromJson(JsonNode.Parse(content)));
        }
        catch (Exception e) when (e is JsonException or FormWorkbookException)
        {
            // A Form item added through the portal holds whatever JSON its client wrote; not every one is a model.
            return (document, null);
        }
    }

    private static async Task<JsonNode?> PublicationAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        return await resources.FindAsync(Guid.Parse(id), PublicationResource, cancellation).ConfigureAwait(false) is { } found
            ? JsonNode.Parse(found.Content)
            : null;
    }

    private static object PlanJson(FormLayerPlan plan) => new
    {
        service = plan.ServiceName,
        layers = plan.Layers.Select(l => new
        {
            l.Name,
            repeat = l.Repeat,
            geometryType = l.GeometryType ?? "Table",
            fields = l.Fields.Select(f => new { f.Question, column = PostGisImporter.ColumnNameFor(f.Column), type = f.Type.ToString(), f.Length, f.Alias, domain = f.Domain?.Name }),
            attachments = l.AttachmentQuestions,
            parentRepeat = l.ParentRepeat,
        }),
        domains = plan.Domains.Select(d => new { d.Name, d.List, codes = d.Codes.Count }),
    };

    private static object[] Issues(IEnumerable<FormIssue> issues) =>
        [.. issues.Select(i => new { sheet = i.Sheet, row = i.Row, column = i.Column, message = i.Message })];

    private static IResult Refusal(IReadOnlyList<FormIssue> refusals, IReadOnlyList<FormIssue> notices, string what) =>
        Results.Json(new
        {
            error = new
            {
                code = 400,
                message = $"{what} {refusals.Count} {(refusals.Count == 1 ? "thing needs" : "things need")} changing in it.",
                details = refusals.Select(r => $"{r.Sheet} row {r.Row}{(r.Column is null ? string.Empty : $", {r.Column}")}: {r.Message}").ToArray(),
            },
            refusals = Issues(refusals),
            notices = Issues(notices),
        }, statusCode: 400);

    private static IResult Error(int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code);

    private static IResult Missing(string id) =>
        Error(404, $"No form '{id}' that you can manage. It may have been deleted, or it is not yours.");
}
