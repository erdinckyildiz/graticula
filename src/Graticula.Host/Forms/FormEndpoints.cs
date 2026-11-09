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
public static class FormEndpoints
{
    /// <summary>The item resource that records what publishing made.</summary>
    public const string PublicationResource = "graticula/publication.json";

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
    }

    /// <summary>
    /// <c>/forms/{itemId}</c>, the address §5.4 names: the client's page, <c>wwwroot/forms/fill.html</c>, which reads the
    /// id from its own path. The same file as <c>/forms/fill.html?id=…</c>, served rather than copied, so the two cannot
    /// drift; whether the caller may read the form is decided when the page asks for <c>form.json</c>.
    /// </summary>
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

        if (await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false) is not null)
        {
            return Error(409, "The form is published, and changing a published form's questions is not built yet; its layers would no longer match it. Publish a copy instead.");
        }

        IServiceProvider services = context.RequestServices;
        await services.GetRequiredService<IDocumentStore>().ReplaceAsync(document.Id, document.FileName, document.Kind,
            Encoding.UTF8.GetBytes(model.ToJson().ToJsonString()), cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.save", document.Id,
            AdminEndpoints.Detail(new { questions = check.Definition!.AllQuestions.Count() }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(new { id = document.Id, notices = Issues(check.Notices) });
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

        if (await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false) is not null)
        {
            return Error(409, "The form is already published. Republishing a changed form is not built yet.");
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

        // A form shared before it was published opens its answers now, as sharing it afterwards would have.
        if (document.Sharing is SharingScope.Organization or SharingScope.Public)
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

    // ---------------------------------------------------------------------------------------------------------- sharing

    /// <summary>A form's sharing: who may open it, and where their answers go.</summary>
    /// <param name="Sharing"><c>private</c>, <c>organization</c> or <c>public</c>.</param>
    internal sealed record FormSharingRequest(string? Sharing);

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
        // enum-default-is-deliberate: refused. A form shared to groups or to all organizations would need its submit view
        // shared the same way, which is not built (ADR-287 §5.11 (l)); those scopes are refused with a sentence, not mapped.
        SharingScope scope = request.Sharing?.ToLowerInvariant() switch
        {
            "private" => SharingScope.Private,
            "organization" => SharingScope.Organization,
            "public" => SharingScope.Public,
            _ => (SharingScope)(-1),
        };

        if (!Enum.IsDefined(scope))
        {
            return Error(400, "A form is shared as 'private', 'organization' or 'public'.");
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

        if (publication is not null)
        {
            (int status, string? message) = await OpenAnswersAsync(context, document, publication, scope, cancellation).ConfigureAwait(false);

            if (status is < 200 or >= 300)
            {
                return Error(status, $"The form's answers could not be opened to '{word}': {message ?? "no reason given"}. Its sharing was not changed.");
            }
        }

        await services.GetRequiredService<IDocumentStore>().UpdateAsync(document.Id, document.Title, document.Snippet, scope, cancellation)
            .ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.share", document.Id,
            AdminEndpoints.Detail(new { sharing = word, view = (string?)publication?["submitService"] }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(new { id = document.Id, sharing = word, publication });
    }

    /// <summary>
    /// Opens a published form's answers to a sharing scope: makes its Create-only submit view, or reshares the one it has,
    /// and records each layer's <c>submitUrl</c>. Called when a form is shared and when an already shared form is
    /// published, so a form shared before it was published takes answers too (found by the Studio screens 2026-11-09).
    /// </summary>
    private static async Task<(int Status, string? Message)> OpenAnswersAsync(
        HttpContext context, Document document, JsonObject publication, SharingScope scope, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        string word = scope.ToString().ToLowerInvariant();
        string service = (string?)publication["service"] ?? string.Empty;
        string view = (string?)publication["submitService"] ?? $"{service}_submit";
        bool exists = publication["submitService"] is not null;
        (int status, string? message) = (200, null);

        if (!exists && scope != SharingScope.Private)
        {
            AdminEndpoints.ViewRequest asked = new(view, null, null, ["Create"], word);
            (status, message) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.CreateViewAsync(
                context, service, FeatureServerMetadataWriter.HostedFolder, asked,
                services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
                services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
                services.GetRequiredService<IAuditLog>(), cancellation)).ConfigureAwait(false);
        }
        else if (exists)
        {
            (status, message) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.SetServiceSharingAsync(
                context, view, FeatureServerMetadataWriter.HostedFolder, new SharingRequest(word),
                services.GetRequiredService<PostgresSystemServices>(), services.GetRequiredService<IAdminCatalog>(),
                services.GetRequiredService<IAuditLog>(), services.GetRequiredService<PostgresLayerCatalog>(), cancellation))
                .ConfigureAwait(false);
        }

        if (status is < 200 or >= 300)
        {
            return (status, message);
        }

        if (!exists && scope != SharingScope.Private)
        {
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

        FormModel model = FormModel.FromJson(JsonNode.Parse(await documents.ContentAsync(document.Id, cancellation).ConfigureAwait(false) ?? []));
        FormCheck check = FormDefinition.Check(model);

        if (check.Definition is null)
        {
            return Error(409, "The form cannot be filled in: it no longer passes its check.");
        }

        JsonNode? publication = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false);
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
