using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host.Forms;

/// <summary>
/// A form whose settings sheet has <c>submission_url</c> writes to that feature layer of this server instead of making
/// one — Survey123's <i>survey from an existing feature layer</i>, ADR-287 §5.17.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publishing adds nothing to the target.</b> No field, no GlobalID column, no editor tracking, no choice list: the
/// form is matched to the layer as it is (<see cref="SubmissionMatcher"/>), and what does not match is refused with its
/// row and column. What publishing does make is what the form owns: the item's resources, its link to the service, and —
/// when the form is shared — the answers view, over exactly the layers the form writes to.
/// </para>
/// <para>
/// <b>Republishing changes nothing in the target either</b>: it is matched again, and the publication is rewritten with the
/// questions as they are now. <b>Deleting the form deletes the form item</b>; no route here deletes a service, and the
/// service a form names is not the form's to delete.
/// </para>
/// </remarks>
public static partial class FormEndpoints
{
    /// <summary>The publication key that marks a form which writes to a layer it did not make.</summary>
    private const string ExistingKey = "existing";

    private static bool IsExisting(JsonNode? publication) => publication?[ExistingKey]?.GetValueKind() == System.Text.Json.JsonValueKind.True;

    /// <summary>The numbers of the layers a publication writes to.</summary>
    private static int[] LayerNumbers(JsonObject publication) =>
        [.. (publication["layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(l => (string?)l["url"] ?? string.Empty)
            .Select(u => int.TryParse(u[(u.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : -1)
            .Where(n => n >= 0)
            .Order()];

    /// <summary>The name an answers view is given: the service's, kept short enough for a view's name (40) with <c>_submit</c>.</summary>
    private static string ViewNameFor(string service) => (service.Length > 33 ? service[..33] : service) + "_submit";

    private sealed record Target(SubmissionAddress Address, PublishedService Service, SubmissionMatch Match);

    /// <summary>Resolves the form's <c>submission_url</c> to a service of this server and matches the form to it.</summary>
    /// <returns>The target, or the refusal to answer with.</returns>
    private static async Task<(Target? Target, IResult? Refused)> ResolveTargetAsync(
        HttpContext context, FormCheck check, FormLayerPlan plan, string what, CancellationToken cancellation)
    {
        string url = check.Definition!.Settings.SubmissionUrl!;
        IResult Refused(string message) => Refusal([new FormIssue("settings", 2, "submission_url", message)], check.Notices, what);

        if (!SubmissionTarget.TryParse(url, out SubmissionAddress? address, out string? why, context.Request.PathBase.Value ?? string.Empty))
        {
            return (null, Refused(why!));
        }

        string host = context.Request.Host.Value ?? string.Empty;

        if (!SubmissionTarget.IsOwn(address!, host, context.Request.Scheme))
        {
            return (null, Refused($"'{url}' is on {address!.Authority}, and this server answers at {host}. A form writes to a layer of the server that publishes it; "
                + "a layer of another server is not written to from here."));
        }

        if (!string.Equals(address!.Folder, FeatureServerMetadataWriter.HostedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return (null, Refused($"'{url}' is not in the '{FeatureServerMetadataWriter.HostedFolder}' folder. A form writes to a hosted layer, and hosted layers are served from there."));
        }

        IServiceProvider services = context.RequestServices;
        PostgresLayerCatalog catalogue = services.GetRequiredService<PostgresLayerCatalog>();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await catalogue.FindServiceAsync(address.Folder, address.Service, cancellation).ConfigureAwait(false) is not { } service)
        {
            return (null, Refused($"There is no feature service '{address.Service}' on this server at '{url}'."));
        }

        if (!LayerAccess.MayManage(service.Owner, current.Principal, current.Authorization))
        {
            return (null, Refused($"The service '{service.Name}' is not yours to publish a form to: it is its owner's, or an administrator's, to name in a form."));
        }

        if (service.ViewOf is not null)
        {
            return (null, Refused($"'{service.Name}' is a view. Name the layer it is a view of; a form's own answers view is made over that."));
        }

        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        PostgresRelationshipCatalog relationships = services.GetRequiredService<PostgresRelationshipCatalog>();
        List<ExistingLayer> layers = [];

        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            (_, LayerDescription shape) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            IReadOnlyList<LayerRelationship> related = await relationships.ForLayerAsync(layer.Id, cancellation).ConfigureAwait(false);

            layers.Add(new ExistingLayer(
                layer.LayerIndex,
                layer.Id,
                layer.Definition.Name,
                layer.IsTable ? null : layer.GeometryType,
                layer.Definition.IsHosted && layer.Definition.IntegerIdentityColumn is not null,
                layer.KeepsAttachments,
                shape.Fields,
                [.. related.Where(r => r.OriginLayerId == layer.Id).Select(r => new ExistingRelationship(r.OriginKey, r.RelatedLayerId, r.RelatedKey, r.Number))]));
        }

        SubmissionMatch match = SubmissionMatcher.Match(check.Definition, plan, address, new ExistingService(service.Folder, service.Name, layers));

        return match.Accepted
            ? (new Target(address, service, match), null)
            : (null, Refusal(match.Refusals, check.Notices, what));
    }

    /// <summary>What the form client and the results screen read: the same shape publishing a new layer writes.</summary>
    private static JsonObject ExistingPublication(Target target, FormDefinition definition, JsonObject? before)
    {
        string root = target.Address.Path(layer: null);
        JsonObject publication = new()
        {
            ["service"] = target.Service.Name,
            ["folder"] = target.Service.Folder,
            [ExistingKey] = true,
            ["submissionUrl"] = definition.Settings.SubmissionUrl,
            ["featureServer"] = root,
            ["srid"] = FormLayerPlan.Srid,
            ["version"] = definition.Settings.Version,
            ["publishedAt"] = (string?)before?["publishedAt"] ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["layers"] = new JsonArray([.. target.Match.Matched.Select(m => (JsonNode)new JsonObject
            {
                ["repeat"] = m.Planned.Repeat,
                ["name"] = m.Existing.Name,
                ["id"] = m.Existing.Id.ToString(),
                ["url"] = $"{root}/{m.Existing.Index}",
                ["geometryType"] = m.Planned.GeometryType,
                ["location"] = m.Planned.LocationQuestion,
                ["parentRepeat"] = m.Planned.ParentRepeat,
                ["relationshipId"] = m.RelationshipNumber,
                ["columns"] = new JsonObject([.. m.Planned.Fields.Select(f =>
                    KeyValuePair.Create(f.Question, (JsonNode?)JsonValue.Create(PostGisImporter.ColumnNameFor(f.Column))))]),
                ["attachments"] = new JsonArray([.. m.Planned.AttachmentQuestions.Select(q => (JsonNode?)JsonValue.Create(q))]),
            })]),
        };

        return publication;
    }

    private static async Task<IResult> PublishToExistingAsync(
        HttpContext context, Document document, FormModel model, FormCheck check, FormLayerPlan plan, CancellationToken cancellation)
    {
        (Target? target, IResult? refused) = await ResolveTargetAsync(context, check, plan, "The form was not published.", cancellation).ConfigureAwait(false);

        if (target is null)
        {
            return refused!;
        }

        IServiceProvider services = context.RequestServices;
        JsonObject publication = ExistingPublication(target, check.Definition!, before: null);

        await services.GetRequiredService<IItemResourceStore>().WriteAsync(Guid.Parse(document.Id), PublicationResource, "application/json",
            Encoding.UTF8.GetBytes(publication.ToJsonString()), "inherit", replace: false, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);
        await services.GetRequiredService<IDocumentStore>().SetLinkAsync(document.Id, (string?)publication["featureServer"],
            ["Form", "Graticula Form", "XLSForm"], cancellation).ConfigureAwait(false);
        await WritePublishedModelAsync(context, document.Id, model, cancellation).ConfigureAwait(false);

        // The form item is filed; the service is somebody's already and is neither retitled nor moved (§5.12, §5.17).
        await FileAsync(context, document, target.Service.Name, document.Title, cancellation, touchService: false).ConfigureAwait(false);

        if (document.Sharing is SharingScope.Organization or SharingScope.Public or SharingScope.Group)
        {
            (int opened, string? why) = await OpenAnswersAsync(context, document, publication, document.Sharing, cancellation).ConfigureAwait(false);

            if (opened is < 200 or >= 300)
            {
                return Error(opened, $"The form was published, but its answers could not be opened to its sharing: {why ?? "no reason given"}. Share it again.");
            }
        }

        // ADR-300 §5.1, §5.17: over the layer the form writes to, where it is a hosted layer its publisher manages.
        (int boxed, string? boxedWhy) = await SyncInboxAsync(
            context, document, publication, null, document.Sharing, document.SharedWithNames ?? [], cancellation).ConfigureAwait(false);

        if (boxed is < 200 or >= 300)
        {
            return Error(boxed, $"The form was published, but its Inbox could not be made: {boxedWhy ?? "no reason given"}. Turn it on again.");
        }

        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.publish", document.Id,
            AdminEndpoints.Detail(new { service = target.Service.Name, layers = target.Match.Matched.Count, existing = true }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(publication);
    }

    /// <summary>
    /// Publishes again a form that writes to an existing layer — nothing in the layer changes. Answers null when the form is
    /// not one, and has never been, so the ordinary republish goes on.
    /// </summary>
    private static async Task<IResult?> RepublishToExistingAsync(
        HttpContext context, Document document, FormModel model, FormCheck check, FormLayerPlan plan, JsonObject publication,
        CancellationToken cancellation)
    {
        bool was = IsExisting(publication);
        string? url = check.Definition!.Settings.SubmissionUrl;

        if (!was && url is null)
        {
            return null;
        }

        IServiceProvider services = context.RequestServices;
        const string What = "The form was not published again.";

        if (was != (url is not null))
        {
            return Refusal([new FormIssue("settings", 2, "submission_url", was
                ? $"The form writes to the layer '{(string?)publication["service"]}' and cannot stop: its answers are there. Put the submission_url back, or publish a copy."
                : $"The form was published with a layer of its own, '{(string?)publication["service"]}', and cannot start writing to another one. Take the submission_url out, or publish a copy.")],
                check.Notices, What);
        }

        (Target? target, IResult? refused) = await ResolveTargetAsync(context, check, plan, What, cancellation).ConfigureAwait(false);

        if (target is null)
        {
            return refused!;
        }

        int[] before = LayerNumbers(publication);
        int top = (publication["layers"] as JsonArray)?.OfType<JsonObject>()
            .Where(l => ((string?)l["repeat"] ?? string.Empty).Length == 0).Select(NumberOf).FirstOrDefault() ?? -1;

        if (!string.Equals((string?)publication["service"], target.Service.Name, StringComparison.OrdinalIgnoreCase)
            || top != target.Match.Matched[0].Existing.Index)
        {
            return Refusal([new FormIssue("settings", 2, "submission_url",
                $"The form was published to '{(string?)publication["service"]}' and its answers are there; it cannot be moved to '{target.Service.Name}'. Put the submission_url back, or publish a copy.")],
                check.Notices, What);
        }

        JsonObject updated = ExistingPublication(target, check.Definition, publication);
        string? view = (string?)publication["submitService"];

        // ADR-300 §5.1: the Inbox is made again over the layers as they are now, below.
        await RemoveInboxAsync(context, publication, cancellation).ConfigureAwait(false);

        // The answers view is made over the layers the form writes to. When a new repeat brings another one in, it is made
        // again, as publishing again does for a new form; when the set is the same it stays as it is.
        bool remake = view is not null && !before.SequenceEqual(LayerNumbers(updated));

        if (remake)
        {
            await RemoveAnswersViewAsync(context, publication, cancellation).ConfigureAwait(false);
        }
        else if (view is not null)
        {
            updated["submitService"] = view;

            foreach (JsonObject layer in (updated["layers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                layer["submitUrl"] = $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{view}/FeatureServer/{NumberOf(layer)}";
            }
        }

        updated["republishedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        updated["fieldsAdded"] = 0;

        await services.GetRequiredService<IItemResourceStore>().WriteAsync(Guid.Parse(document.Id), PublicationResource, "application/json",
            Encoding.UTF8.GetBytes(updated.ToJsonString()), "inherit", replace: true, 100L * 1024 * 1024, cancellation).ConfigureAwait(false);

        if (remake)
        {
            (int opened, string? why) = await OpenAnswersAsync(context, document, updated, document.Sharing, cancellation).ConfigureAwait(false);

            if (opened is < 200 or >= 300)
            {
                return Error(opened, $"The form was published again, but its answers view could not be made again: {why ?? "no reason given"}. Share it again.");
            }
        }

        (int boxed, string? boxedWhy) = await SyncInboxAsync(
            context, document, updated, null, document.Sharing, document.SharedWithNames ?? [], cancellation).ConfigureAwait(false);

        if (boxed is < 200 or >= 300)
        {
            return Error(boxed, $"The form was published again, but its Inbox could not be made again: {boxedWhy ?? "no reason given"}. Turn it on again.");
        }

        await WritePublishedModelAsync(context, document.Id, model, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "form.republish", document.Id,
            AdminEndpoints.Detail(new { service = target.Service.Name, fieldsAdded = 0, existing = true }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(updated);
    }

    private static int? NumberOf(JsonObject layer) =>
        int.TryParse(((string?)layer["url"])?[(((string?)layer["url"])!.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>Takes a form's answers view away, and the <c>submitUrl</c> of each layer that named it.</summary>
    private static async Task RemoveAnswersViewAsync(HttpContext context, JsonObject publication, CancellationToken cancellation)
    {
        if ((string?)publication["submitService"] is not { } view)
        {
            return;
        }

        IServiceProvider services = context.RequestServices;
        await PortalEndpoints.InnerStatusAsync(context, () => AdminEndpoints.DeleteServiceAsync(
            context, view, FeatureServerMetadataWriter.HostedFolder, true,
            services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
            services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
            services.GetRequiredService<ITileCache>(), services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<SourceQuiesce>(), services.GetRequiredService<RecycleBinService>(),
            services.GetRequiredService<LayerConnections>(), cancellation)).ConfigureAwait(false);
        publication.Remove("submitService");

        foreach (JsonObject layer in (publication["layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            layer.Remove("submitUrl");
        }
    }
}
