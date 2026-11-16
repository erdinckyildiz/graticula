using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
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
/// A form's Inbox — ADR-300: the records its readers download, change and send back, each reader seeing the rows the
/// publisher's filter gives them, through a view of the form's layer that the database filters by who is reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings, then a view.</b> <c>PUT /admin/forms/{id}/inbox</c> keeps the publisher's settings — on or off, the filter
/// in Survey123's spelling (<c>created_user = ${username}</c>), the record limit — in the item's own resources, beside its
/// publication, so a form not yet published keeps them until it is (ADR-300 §5.9 (f), INFERRED: the item's resources
/// rather than its XLSForm). When the form is published the Inbox view is made, made again, or dropped now, and every
/// publish and every change of sharing makes it again: it holds no rows of its own, so remaking it loses nothing.
/// </para>
/// <para>
/// <b>The view.</b> <c>&lt;service&gt;_inbox</c>, a hosted view (ADR-113) of the layers the form writes to, offering Query and
/// Update, no tiles; the top layer filtered by the publisher's filter, each repeat's table filtered to the rows whose
/// parent the parent's Inbox layer shows, so a repeat cannot be read past its parent (§5.4). Made private, filtered, and
/// only then shared as the form is — never with everyone: a public form's Inbox is shared with the organisation (§5.1,
/// §5.9 (b)).
/// </para>
/// <para>
/// <b>What the client reads.</b> The publication gains <c>inbox: {limit}</c>, <c>inboxService</c>, and on each layer
/// <c>inboxUrl</c> — and, on a repeat's layer, <c>inboxRelationshipId</c>, the relationship between the Inbox's own
/// layers. The form client is served none of them when nobody is signed in.
/// </para>
/// </remarks>
public static partial class FormEndpoints
{
    /// <summary>The item resource that keeps a form's Inbox settings — ADR-300 §5.1.</summary>
    public const string InboxResource = "graticula/inbox.json";

    /// <summary>The filter an Inbox has until its publisher writes another: each member's own submissions (§5.9 (a)).</summary>
    internal const string DefaultInboxFilter = "created_user = " + PublishedLayer.ReaderVariable;

    /// <summary>The record limit an Inbox has until its publisher sets another (§5.9 (c)).</summary>
    internal const int DefaultInboxLimit = 1000;

    /// <summary>The most records an Inbox may be set to download — Survey123's ceiling.</summary>
    internal const int MaximumInboxLimit = 5000;

    /// <summary>A form's Inbox settings.</summary>
    /// <param name="Enabled">Whether it has one.</param>
    /// <param name="Filter">The <c>where</c> over its top layer, which may name <c>${username}</c>.</param>
    /// <param name="Limit">The most records a reader downloads.</param>
    internal sealed record InboxSettings(bool Enabled, string Filter, int Limit)
    {
        /// <summary>What a form that was never given settings has.</summary>
        public static InboxSettings Default { get; } = new(false, DefaultInboxFilter, DefaultInboxLimit);

        public JsonObject ToJson() => new() { ["enabled"] = Enabled, ["filter"] = Filter, ["limit"] = Limit };

        public static InboxSettings FromJson(JsonNode? node) => node is JsonObject o
            ? new InboxSettings(
                o["enabled"]?.GetValueKind() == JsonValueKind.True,
                (string?)o["filter"] is { Length: > 0 } f ? f : DefaultInboxFilter,
                o["limit"] is JsonValue l && l.TryGetValue(out int n) ? n : DefaultInboxLimit)
            : Default;
    }

    /// <summary>What <c>PUT /admin/forms/{id}/inbox</c> reads; an absent member keeps what was set.</summary>
    /// <param name="Enabled">Whether the form has an Inbox.</param>
    /// <param name="Filter">The filter.</param>
    /// <param name="Limit">The record limit, 1 to 5,000.</param>
    internal sealed record InboxRequest(bool? Enabled, string? Filter, int? Limit);

    private static void MapInbox(WebApplication app)
    {
        app.MapGet("/admin/forms/{id}/inbox", InboxReadAsync);
        app.MapPut("/admin/forms/{id}/inbox", InboxWriteAsync);
    }

    private static async Task<InboxSettings> InboxSettingsAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        return await resources.FindAsync(Guid.Parse(id), InboxResource, cancellation).ConfigureAwait(false) is { } found
            ? InboxSettings.FromJson(JsonNode.Parse(found.Content))
            : InboxSettings.Default;
    }

    /// <summary>Writes an item resource: replaced when there is one, added the first time.</summary>
    private static async Task WriteResourceAsync(HttpContext context, string id, string path, JsonNode content, CancellationToken cancellation)
    {
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        byte[] bytes = Encoding.UTF8.GetBytes(content.ToJsonString());

        if (await resources.WriteAsync(Guid.Parse(id), path, "application/json", bytes, "inherit", replace: true,
                100L * 1024 * 1024, cancellation).ConfigureAwait(false) == ItemResourceWrite.NotThere)
        {
            await resources.WriteAsync(Guid.Parse(id), path, "application/json", bytes, "inherit", replace: false,
                100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        }
    }

    private static object InboxAnswer(Document document, InboxSettings settings, JsonNode? publication) => new
    {
        id = document.Id,
        enabled = settings.Enabled,
        filter = settings.Filter,
        limit = settings.Limit,
        published = publication is not null,
        service = (string?)publication?["inboxService"],
        layers = (publication?["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(l => l["inboxUrl"] is not null)
            .Select(l => new { repeat = (string?)l["repeat"], inboxUrl = (string?)l["inboxUrl"] }),
    };

    /// <summary><c>GET /admin/forms/{id}/inbox</c>: the settings, and the view they made when the form is published.</summary>
    private static async Task<IResult> InboxReadAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ManagedFormAsync(context, id, "open the Inbox of", cancellation).ConfigureAwait(false) is not ({ } document, _))
        {
            return Missing(id);
        }

        InboxSettings settings = await InboxSettingsAsync(context, document.Id, cancellation).ConfigureAwait(false);
        JsonNode? publication = await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false);
        return Results.Json(InboxAnswer(document, settings, publication));
    }

    /// <summary>
    /// <c>PUT /admin/forms/{id}/inbox</c>: keeps the settings, and on a published form makes, remakes or drops the view now.
    /// </summary>
    private static async Task<IResult> InboxWriteAsync(HttpContext context, string id, InboxRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ManagedFormAsync(context, id, "change the Inbox of", cancellation).ConfigureAwait(false) is not ({ } document, { } model))
        {
            return Missing(id);
        }

        InboxSettings before = await InboxSettingsAsync(context, document.Id, cancellation).ConfigureAwait(false);
        InboxSettings wanted = new(
            request.Enabled ?? before.Enabled,
            request.Filter is null ? before.Filter : request.Filter.Trim(),
            request.Limit ?? before.Limit);

        if (wanted.Limit is < 1 or > MaximumInboxLimit)
        {
            return Error(400, $"An Inbox downloads from 1 to {MaximumInboxLimit:N0} records; 'limit' was {wanted.Limit}.");
        }

        if (wanted.Filter.Length == 0)
        {
            return Error(400, $"An Inbox has a filter, a where over the form's layer such as {DefaultInboxFilter}. Send '1=1' for every record.");
        }

        if (wanted.Enabled && !await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        JsonObject? publication = (await PublicationAsync(context, document.Id, cancellation).ConfigureAwait(false)) as JsonObject;

        if (await InboxRefusalAsync(context, model, document, publication, wanted.Filter, cancellation).ConfigureAwait(false) is { } refused)
        {
            return Error(400, refused);
        }

        await WriteResourceAsync(context, document.Id, InboxResource, wanted.ToJson(), cancellation).ConfigureAwait(false);

        if (publication is not null)
        {
            (int status, string? message) = await SyncInboxAsync(
                context, document, publication, wanted, document.Sharing, document.SharedWithNames ?? [], cancellation).ConfigureAwait(false);

            if (status is < 200 or >= 300)
            {
                return Error(status, $"The Inbox settings were kept, but its view could not be {(wanted.Enabled ? "made" : "dropped")}: {message ?? "no reason given"}.");
            }
        }

        await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(), "form.inbox", document.Id,
            AdminEndpoints.Detail(new { wanted.Enabled, wanted.Filter, wanted.Limit, service = (string?)publication?["inboxService"] }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(InboxAnswer(document, wanted, publication));
    }

    /// <summary>
    /// Why this filter cannot be a form's Inbox filter — read by the top layer's own parser, as a view filter is — or null.
    /// </summary>
    /// <remarks>
    /// Against the published layer when there is one; for a form not yet published, against the layer publishing would
    /// make (its questions' columns, GlobalID and editor tracking), or the layer its <c>submission_url</c> names (§5.17),
    /// which must be a hosted layer its publisher manages.
    /// </remarks>
    private static async Task<string?> InboxRefusalAsync(
        HttpContext context, FormModel model, Document document, JsonObject? publication, string filter, CancellationToken cancellation)
    {
        ServiceContexts contexts = context.RequestServices.GetRequiredService<ServiceContexts>();

        if (publication is not null)
        {
            if (await InboxSourceAsync(context, publication, cancellation).ConfigureAwait(false) is not ({ } source, { } top))
            {
                return "The form's layer is no longer there, so it can have no Inbox. Publish it again.";
            }

            if (source.LayersAndTables.Where(l => LayerNumbers(publication).Contains(l.LayerIndex))
                    .FirstOrDefault(l => !(l.Definition.IsHosted && l.Definition.IntegerIdentityColumn is not null)) is { } foreign)
            {
                return $"The form writes to '{foreign.Definition.Name}', which is not a hosted layer, and an Inbox is a view of hosted layers.";
            }

            (ParsedWhere? parsed, string? why) = await AdminEndpoints.ParseViewFilterAsync(contexts, top, filter, cancellation).ConfigureAwait(false);
            return parsed is null ? $"The Inbox filter could not be read: {why}" : null;
        }

        FormCheck check = FormDefinition.Check(model);

        if (!check.Accepted)
        {
            return "The form does not pass its check, so its Inbox filter cannot be read against its layer. Correct the form first.";
        }

        FormLayerPlan plan = FormLayerPlan.For(check.Definition!, document.Title);

        if (check.Definition!.Settings.SubmissionUrl is not null)
        {
            (Target? target, _) = await ResolveTargetAsync(context, check, plan, "The Inbox was not set.", cancellation).ConfigureAwait(false);

            if (target is null)
            {
                return "The form's submission_url does not name a layer it can write to, so its Inbox filter cannot be read. Publish it first.";
            }

            PublishedLayer? top = target.Service.LayersAndTables.FirstOrDefault(l => l.LayerIndex == target.Match.Matched[0].Existing.Index);

            if (target.Match.Matched.Select(m => target.Service.LayersAndTables.FirstOrDefault(l => l.LayerIndex == m.Existing.Index))
                    .Any(l => l is null || !(l.Definition.IsHosted && l.Definition.IntegerIdentityColumn is not null))
                || top is null)
            {
                return $"The form writes to a layer of '{target.Service.Name}' that is not a hosted layer, and an Inbox is a view of hosted layers.";
            }

            (ParsedWhere? parsedTarget, string? whyTarget) = await AdminEndpoints.ParseViewFilterAsync(contexts, top, filter, cancellation).ConfigureAwait(false);
            return parsedTarget is null ? $"The Inbox filter could not be read: {whyTarget}" : null;
        }

        PlannedLayer main = plan.Layers.First(l => l.Repeat.Length == 0);
        Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase)
        {
            ["objectid"] = FieldType.Integer,
            ["globalid"] = FieldType.Guid,
            ["created_user"] = FieldType.Text,
            ["created_date"] = FieldType.Date,
            ["last_edited_user"] = FieldType.Text,
            ["last_edited_date"] = FieldType.Date,
        };

        foreach (PlannedField field in main.Fields)
        {
            types[PostGisImporter.ColumnNameFor(field.Column)] = field.Type;
        }

        return WhereClause.TryParse(ReaderFilters.Prepare(filter), [.. types.Keys], LayerDefinition.Quote, out ParsedWhere planned, out string? error, types)
            ? ReaderFilters.Misplaced(planned) is { } misplaced ? $"The Inbox filter could not be read: {misplaced}" : null
            : $"The Inbox filter could not be read: {ReaderFilters.Words(error)}";
    }

    /// <summary>The service a publication writes to and its top layer, or nothing when either is gone.</summary>
    private static async Task<(PublishedService? Source, PublishedLayer? Top)> InboxSourceAsync(
        HttpContext context, JsonObject publication, CancellationToken cancellation)
    {
        PostgresLayerCatalog catalogue = context.RequestServices.GetRequiredService<PostgresLayerCatalog>();
        string folder = (string?)publication["folder"] ?? FeatureServerMetadataWriter.HostedFolder;

        if ((string?)publication["service"] is not { } name
            || await catalogue.FindServiceAsync(folder, name, cancellation).ConfigureAwait(false) is not { } source)
        {
            return (null, null);
        }

        int? top = (publication["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(l => ((string?)l["repeat"] ?? string.Empty).Length == 0).Select(NumberOf).FirstOrDefault();

        return (source, source.LayersAndTables.FirstOrDefault(l => l.LayerIndex == top));
    }

    /// <summary>The name an Inbox view is given: the service's, kept short enough for a view's name (40) with <c>_inbox</c>.</summary>
    private static string InboxNameFor(string service) => (service.Length > 34 ? service[..34] : service) + "_inbox";

    /// <summary>Takes a form's Inbox view away, and what each layer of the publication said about it.</summary>
    private static async Task RemoveInboxAsync(HttpContext context, JsonObject publication, CancellationToken cancellation)
    {
        if ((string?)publication["inboxService"] is { } view)
        {
            IServiceProvider services = context.RequestServices;
            await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.DeleteServiceAsync(
                context, view, FeatureServerMetadataWriter.HostedFolder, true,
                services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
                services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
                services.GetRequiredService<ITileCache>(), services.GetRequiredService<IAuditLog>(),
                services.GetRequiredService<SourceQuiesce>(), services.GetRequiredService<RecycleBinService>(),
                services.GetRequiredService<LayerConnections>(), cancellation)).ConfigureAwait(false);
        }

        publication.Remove("inboxService");
        publication.Remove("inbox");

        foreach (JsonObject layer in (publication["layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            layer.Remove("inboxUrl");
            layer.Remove("inboxRelationshipId");
        }
    }

    /// <summary>
    /// Brings a published form's Inbox view to its settings and sharing: drops the one it has, makes it again when the Inbox
    /// is on, and writes the publication — §5.1.
    /// </summary>
    /// <returns>200, or the failure's status and its reason; on a failure no half-made view is left.</returns>
    private static async Task<(int Status, string? Message)> SyncInboxAsync(
        HttpContext context, Document document, JsonObject publication, InboxSettings? settings, SharingScope sharing,
        IReadOnlyList<string> groups, CancellationToken cancellation)
    {
        settings ??= await InboxSettingsAsync(context, document.Id, cancellation).ConfigureAwait(false);
        bool had = publication["inboxService"] is not null;
        await RemoveInboxAsync(context, publication, cancellation).ConfigureAwait(false);

        (int status, string? message) = settings.Enabled
            ? await MakeInboxAsync(context, document, publication, settings, sharing, groups, cancellation).ConfigureAwait(false)
            : (200, null);

        if (had || settings.Enabled)
        {
            await WriteResourceAsync(context, document.Id, PublicationResource, publication, CancellationToken.None).ConfigureAwait(false);
        }

        return (status, message);
    }

    private static async Task<(int Status, string? Message)> MakeInboxAsync(
        HttpContext context, Document document, JsonObject publication, InboxSettings settings, SharingScope sharing,
        IReadOnlyList<string> groups, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IAdminCatalog admin = services.GetRequiredService<IAdminCatalog>();
        PostgresLayerCatalog catalogue = services.GetRequiredService<PostgresLayerCatalog>();
        PostGisImporter importer = services.GetRequiredService<PostGisImporter>();
        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        PostgresRelationshipCatalog relationships = services.GetRequiredService<PostgresRelationshipCatalog>();
        const string Hosted = FeatureServerMetadataWriter.HostedFolder;

        if (await InboxSourceAsync(context, publication, cancellation).ConfigureAwait(false) is not ({ } source, { } top))
        {
            return (409, "the form's layer is no longer there");
        }

        if (!string.Equals(source.Folder, Hosted, StringComparison.OrdinalIgnoreCase))
        {
            return (409, $"'{source.Name}' is not a hosted service, and an Inbox is a view of hosted layers");
        }

        int[] layers = LayerNumbers(publication);
        string view = InboxNameFor(source.Name);
        AdminEndpoints.ViewRequest asked = new(
            view, layers, new Dictionary<string, string?> { [top.LayerIndex.ToString(CultureInfo.InvariantCulture)] = settings.Filter },
            ["Query", "Update"], "private");

        (int status, string? message) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.CreateViewAsync(
            context, source.Name, source.Folder, asked, admin, catalogue, importer, contexts,
            services.GetRequiredService<IAuditLog>(), cancellation)).ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return (status, message);
        }

        publication["inboxService"] = view;

        try
        {
            // No tiles, whatever the filter: the Inbox is a reader's working list, not a map layer (§5.1).
            if (!await admin.SetServiceCapabilitiesAsync(view, Hosted, new ServiceCapabilityLimits(null, false, ["Query", "Update"], null), cancellation)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException($"The Inbox view '{view}' was made but its tiles could not be turned off.");
            }

            PublishedService inbox = (await catalogue.FindServiceAsync(Hosted, view, cancellation).ConfigureAwait(false))!;
            bool perReader = ReaderFilters.Names(settings.Filter);
            IReadOnlyList<LayerRelationship> declared = await relationships.ListAsync(cancellation).ConfigureAwait(false);
            Dictionary<int, int> relationshipNumbers = [];

            // §5.4: a repeat's table shows the rows whose parent the parent's Inbox layer shows, and no other.
            foreach (PublishedLayer child in inbox.LayersAndTables.Where(l => l.LayerIndex != top.LayerIndex))
            {
                PublishedLayer sourceChild = source.LayersAndTables.First(l => l.LayerIndex == child.LayerIndex);
                LayerRelationship? link = declared.FirstOrDefault(r => r.RelatedLayerId == sourceChild.Id
                    && source.LayersAndTables.Any(p => p.Id == r.OriginLayerId && inbox.LayersAndTables.Any(v => v.LayerIndex == p.LayerIndex)));

                if (link is null)
                {
                    throw new InvalidOperationException(
                        $"'{sourceChild.Definition.Name}' is not related to another layer of the form, so its rows cannot be kept to the records the Inbox shows.");
                }

                int parentIndex = source.LayersAndTables.First(p => p.Id == link.OriginLayerId).LayerIndex;
                PublishedLayer parent = inbox.LayersAndTables.First(v => v.LayerIndex == parentIndex);
                string filterSql = $"{LayerDefinition.Quote(link.RelatedKey)} in (select p.{LayerDefinition.Quote(link.OriginKey)} from "
                    + $"{LayerDefinition.Quote(PostGisImporter.HostedSchema)}.{LayerDefinition.Quote(parent.Definition.TableName)} p)";

                await importer.CreateViewAsync(
                    sourceChild.Definition.TableName, child.Definition.TableName, sourceChild.Definition.IntegerIdentityColumn!,
                    filterSql, [], cancellation).ConfigureAwait(false);

                // Recorded as what it is, and naming its reader where its parent does, so the faces closed for a per-reader
                // view (PublishedLayer.IsPerReader) are closed for it too.
                string recorded = $"{link.RelatedKey} in ({link.OriginKey} of the rows layer {parentIndex} shows"
                    + (perReader ? $" to {PublishedLayer.ReaderVariable})" : ")");
                await admin.SetViewDefinitionAsync(child.Id, recorded, cancellation).ConfigureAwait(false);
                contexts.Forget(child);

                Guid id = await relationships.DeclareAsync(new LayerRelationship(
                    Guid.Empty, $"{view}_{parentIndex}_{child.LayerIndex}", parent.Id, link.OriginKey, child.Id, link.RelatedKey,
                    link.Cardinality, Composite: false), cancellation).ConfigureAwait(false);

                if (await relationships.FindAsync(id, cancellation).ConfigureAwait(false) is { Number: { } number })
                {
                    relationshipNumbers[child.LayerIndex] = number;
                }
            }

            // Shared as the form is, never with everyone (§5.1, §5.9 (b)); a private form's Inbox is its owner's.
            SharingScope scope = sharing == SharingScope.Public ? SharingScope.Organization : sharing;

            if (scope != SharingScope.Private)
            {
                (int shared, string? why) = await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.SetServiceSharingAsync(
                    context, view, Hosted, new SharingRequest(scope.ToString().ToLowerInvariant()),
                    services.GetRequiredService<PostgresSystemServices>(), admin, services.GetRequiredService<IAuditLog>(), catalogue,
                    cancellation)).ConfigureAwait(false);

                if (shared is >= 200 and < 300 && scope == SharingScope.Group)
                {
                    (shared, why) = await ShareGroupsAsync(context, [], groups, (name, on) =>
                        services.GetRequiredService<IGroupDirectory>().ShareAsync(Caller(context).Principal.Id,
                            Caller(context).Authorization.Allows(Privilege.AdminManageAllContent), name, view, Hosted, on, cancellation)).ConfigureAwait(false);
                }

                if (shared is < 200 or >= 300)
                {
                    throw new InvalidOperationException($"The Inbox view could not be shared: {why ?? "no reason given"}.");
                }
            }

            await FileAsync(context, document, view, document.Title + "_inbox", cancellation, touchService: true).ConfigureAwait(false);

            publication["inbox"] = new JsonObject { ["limit"] = settings.Limit };

            foreach (JsonObject layer in (publication["layers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (NumberOf(layer) is { } index && inbox.LayersAndTables.Any(l => l.LayerIndex == index))
                {
                    layer["inboxUrl"] = $"/rest/services/{Hosted}/{view}/FeatureServer/{index}";

                    if (relationshipNumbers.TryGetValue(index, out int number))
                    {
                        layer["inboxRelationshipId"] = number;
                    }
                }
            }

            return (200, null);
        }
        catch (Exception e) when (e is InvalidOperationException or Npgsql.PostgresException)
        {
            await RemoveInboxAsync(context, publication, CancellationToken.None).ConfigureAwait(false);
            return (409, e.Message);
        }
    }

    /// <summary>
    /// The publication as an anonymous caller is served it: without the Inbox, which is for signed-in members only — §5.1.
    /// </summary>
    internal static void WithoutInbox(JsonNode? publication)
    {
        if (publication is not JsonObject o)
        {
            return;
        }

        o.Remove("inbox");
        o.Remove("inboxService");

        foreach (JsonObject layer in (o["layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            layer.Remove("inboxUrl");
            layer.Remove("inboxRelationshipId");
        }
    }
}
