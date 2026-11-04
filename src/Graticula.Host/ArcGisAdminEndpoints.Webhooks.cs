using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Platform.Secrets;
using Graticula.Platform.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// A feature service's webhooks — its administration's <c>webhooks</c>, with <c>create</c>, each webhook by number, its
/// <c>edit</c> and <c>delete</c>, and <c>deleteAll</c> (ADR-266, the ArcGIS veteran's review of v1.0.499, #15).
/// </summary>
/// <remarks>
/// <para>
/// <b>For the service's owner, or an administrator who manages it</b> — the same door as <c>updateDefinition</c>
/// (<see cref="ManagedServiceAsync"/>). A webhook tells its owner which features changed, never what they now hold, and
/// the sender asks again at each delivery whether the owner still owns the service or administers all content.
/// </para>
/// <para>
/// <b>The change types are ArcGIS's eleven, and four of them fire</b>: FeaturesCreated, FeaturesUpdated, FeaturesDeleted
/// and FeaturesEdited (ADR-266 §2). The attachment, schema and definition types are accepted, so an integration that
/// names them is not refused, and the webhook's document says which of its types this server raises.
/// </para>
/// <para>
/// <b><c>scheduleInfo</c> is kept and not honoured</b>: a delivery is sent as each edit commits rather than gathered over
/// an interval (ADR-266 §3, INFERRED).
/// </para>
/// </remarks>
internal static partial class ArcGisAdminEndpoints
{
    private static void MapWebhooks(WebApplication app)
    {
        foreach (string folder in (string[])["", "/{folder}"])
        {
            foreach (string service in (string[])["{serviceName}/FeatureServer", "{serviceName}.FeatureServer"])
            {
                string root = $"/rest/admin/services{folder}/{service}/webhooks";

                app.MapMethods(root, ["GET", "POST"], ListServiceWebhooksAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/create", CreateServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/createWebhook", CreateServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/deleteAll", DeleteAllServiceWebhooksAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapMethods($"{root}/{{webhookId:int}}", ["GET", "POST"], ServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/{{webhookId:int}}/edit", EditServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/{{webhookId:int}}/update", EditServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"{root}/{{webhookId:int}}/delete", DeleteServiceWebhookAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapMethods($"{root}/{{webhookId:int}}/notificationStatus", ["GET", "POST"], ServiceWebhookDeliveriesAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
            }
        }
    }

    /// <summary>One webhook as ArcGIS's feature service writes one — and never its signature key.</summary>
    private static object ServiceWebhook(HttpContext context, PublishedService service, string? folder, WebhookDefinition hook)
    {
        string serviceUrl = $"{TileFaces.Origin(context)}/rest/services/{(folder is null ? string.Empty : folder + "/")}{service.Name}/FeatureServer";
        JsonObject properties = JsonNode.Parse(hook.Properties) as JsonObject ?? [];

        return new
        {
            id = hook.Number,
            name = hook.Name,
            owner = hook.OwnerName,
            active = hook.Active,
            hookUrl = hook.Url,
            serviceUrl,
            url = $"{TileFaces.Origin(context)}/rest/admin/services/{(folder is null ? string.Empty : folder + "/")}{service.Name}/FeatureServer/webhooks/{hook.Number}",
            signatureKey = string.Empty,
            hasSignatureKey = hook.HasSecret,
            format = hook.PayloadFormat,
            contentType = hook.ContentType,
            maxRetries = hook.MaxRetries,
            changeTypes = hook.Events,
            changeTypesRaised = hook.Events.Contains("*")
                ? WebhookEvents.ChangeTypesRaised
                : [.. hook.Events.Where(e => WebhookEvents.ChangeTypesRaised.Contains(e))],
            scheduleInfo = properties["scheduleInfo"],
            deactivatedReason = hook.DeactivatedReason,
            createdTime = WebhookRequests.Millis(hook.Created),
            lastUpdatedTime = WebhookRequests.Millis(hook.Modified),
        };
    }

    private static async Task ListServiceWebhooksAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IWebhookStore store, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "list the webhooks of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;
        IReadOnlyList<WebhookDefinition> all = await store.ListAsync(WebhookKind.FeatureService, service.Id, cancellation).ConfigureAwait(false);

        await Results.Ok(all.Select(h => ServiceWebhook(context, service, folder, h)).ToArray()).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ServiceWebhookAsync(
        HttpContext context, string serviceName, int webhookId, PostgresLayerCatalog layers, IWebhookStore store,
        CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "read the webhooks of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;

        if (await store.FindByNumberAsync(service.Id, webhookId, cancellation).ConfigureAwait(false) is not { } hook)
        {
            await RefuseAsync(context, 404, $"There is no webhook {webhookId} on '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        await Results.Ok(ServiceWebhook(context, service, folder, hook)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ServiceWebhookDeliveriesAsync(
        HttpContext context, string serviceName, int webhookId, PostgresLayerCatalog layers, IWebhookStore store,
        CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "read the webhooks of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        if (await store.FindByNumberAsync(found.Service.Id, webhookId, cancellation).ConfigureAwait(false) is not { } hook)
        {
            await RefuseAsync(context, 404, $"There is no webhook {webhookId} on '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<WebhookDeliveryRecord> recent = await store.RecentAsync(hook.Id, 25, cancellation).ConfigureAwait(false);
        await Results.Ok(new { id = hook.Number, active = hook.Active, total = recent.Count, notifications = WebhookRequests.Deliveries(recent) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>create</c>: <c>name</c>, <c>hookUrl</c>, <c>changeTypes</c>, <c>signatureKey</c>, <c>active</c> (false unless
    /// said, as ArcGIS's), <c>contentType</c>, <c>payloadFormat</c>, <c>maxRetries</c> and <c>scheduleInfo</c>.
    /// </summary>
    private static async Task CreateServiceWebhookAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IWebhookStore store, SecretProtector protector,
        HostSettings settings, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "add a webhook to", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;
        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);
        string name = form["name"].ToString().Trim();

        if (name.Length is 0 or > 256)
        {
            await RefuseAsync(context, 400, "'name' is required, at most 256 characters.").ConfigureAwait(false);
            return;
        }

        if (!WebhookTargets.TryCheck(form["hookUrl"].ToString(), settings.WebhooksAllowPrivate, out Uri? url, out string? refused))
        {
            await RefuseAsync(context, 400, refused!).ConfigureAwait(false);
            return;
        }

        if (ReadServiceOptions(form, null) is { Error: { } bad })
        {
            await RefuseAsync(context, 400, bad).ConfigureAwait(false);
            return;
        }

        ServiceOptions options = ReadServiceOptions(form, null);
        string secret = form["signatureKey"].ToString();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        WebhookDefinition? made = await store.CreateAsync(
            new NewWebhook(
                WebhookKind.FeatureService, service.Id, name, url!.AbsoluteUri, options.ChangeTypes ?? ["*"],
                secret.Length > 0 ? protector.Protect(secret) : null, secret.Length > 0 ? protector.KeyVersion : null,
                options.Active ?? false, current.Principal.Id, TileFaces.Origin(context),
                options.ContentType ?? "application/json", options.Format ?? "json", options.MaxRetries ?? 3,
                FailuresAllowed: 5, FailureDays: 5, options.Properties ?? "{}"),
            cancellation).ConfigureAwait(false);

        if (made is null)
        {
            await RefuseAsync(context, 400, $"'{serviceName}' already has {IWebhookStore.PerService} webhooks, which is as many as a feature service may have.")
                .ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.create", made.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "featureService", service = service.Name, folder, name, host = url.Host, changeTypes = made.Events }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(ServiceWebhook(context, service, folder, made)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task EditServiceWebhookAsync(
        HttpContext context, string serviceName, int webhookId, PostgresLayerCatalog layers, IWebhookStore store,
        SecretProtector protector, HostSettings settings, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "change a webhook of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;

        if (await store.FindByNumberAsync(service.Id, webhookId, cancellation).ConfigureAwait(false) is not { } hook)
        {
            await RefuseAsync(context, 404, $"There is no webhook {webhookId} on '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);
        string? name = form.ContainsKey("name") ? form["name"].ToString().Trim() : null;

        if (name is { Length: 0 or > 256 })
        {
            await RefuseAsync(context, 400, "'name' is at most 256 characters, and not empty.").ConfigureAwait(false);
            return;
        }

        Uri? url = null;

        if (form.ContainsKey("hookUrl") && !WebhookTargets.TryCheck(form["hookUrl"].ToString(), settings.WebhooksAllowPrivate, out url, out string? refused))
        {
            await RefuseAsync(context, 400, refused!).ConfigureAwait(false);
            return;
        }

        ServiceOptions options = ReadServiceOptions(form, hook.Properties);

        if (options.Error is { } bad)
        {
            await RefuseAsync(context, 400, bad).ConfigureAwait(false);
            return;
        }

        string? secret = form.ContainsKey("signatureKey") ? form["signatureKey"].ToString() : null;
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        WebhookDefinition? changed = await store.UpdateAsync(
            hook.Id,
            new WebhookUpdate(
                Name: name, Url: url?.AbsoluteUri, Events: options.ChangeTypes,
                Secret: secret is null ? null : secret.Length == 0 ? [] : protector.Protect(secret),
                SecretKeyVersion: secret is { Length: > 0 } ? protector.KeyVersion : null,
                Active: options.Active, ContentType: options.ContentType, PayloadFormat: options.Format,
                MaxRetries: options.MaxRetries, Properties: options.Properties),
            current.Principal.Id, cancellation).ConfigureAwait(false);

        if (changed is null)
        {
            await RefuseAsync(context, 404, $"There is no webhook {webhookId} on '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.update", hook.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "featureService", service = service.Name, folder, name = changed.Name, host = WebhookRequests.HostOf(changed.Url) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(ServiceWebhook(context, service, folder, changed)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteServiceWebhookAsync(
        HttpContext context, string serviceName, int webhookId, PostgresLayerCatalog layers, IWebhookStore store, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "remove a webhook of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        if (await store.FindByNumberAsync(found.Service.Id, webhookId, cancellation).ConfigureAwait(false) is not { } hook
            || !await store.DeleteAsync(hook.Id, cancellation).ConfigureAwait(false))
        {
            await RefuseAsync(context, 404, $"There is no webhook {webhookId} on '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.delete", hook.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "featureService", service = found.Service.Name, name = hook.Name }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteAllServiceWebhooksAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IWebhookStore store, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "remove the webhooks of", cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        IReadOnlyList<WebhookDefinition> all = await store.ListAsync(WebhookKind.FeatureService, found.Service.Id, cancellation).ConfigureAwait(false);

        foreach (WebhookDefinition hook in all)
        {
            await store.DeleteAsync(hook.Id, cancellation).ConfigureAwait(false);
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.delete", found.Service.Name,
            AdminEndpoints.Detail(new { kind = "featureService", service = found.Service.Name, all = all.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What a feature-service webhook's form says beside its name and URL; each null when not given.</summary>
    private sealed record ServiceOptions(
        string[]? ChangeTypes, bool? Active, string? ContentType, string? Format, int? MaxRetries, string? Properties, string? Error);

    private static ServiceOptions ReadServiceOptions(IFormCollection form, string? properties)
    {
        string[]? types = null;

        if (form.ContainsKey("changeTypes"))
        {
            if (!WebhookRequests.TryReadChangeTypes(form["changeTypes"].ToString(), out string[] read, out string? bad))
            {
                return new ServiceOptions(null, null, null, null, null, null, bad);
            }

            types = read;
        }

        bool? active = form.ContainsKey("active") ? IsTrue(form["active"].ToString()) : null;
        string? contentType = form.ContainsKey("contentType") ? WebhookRequests.ContentType(form["contentType"].ToString()) : null;
        string? format = form.ContainsKey("payloadFormat") ? WebhookRequests.PayloadFormat(form["payloadFormat"].ToString()) : null;

        if ((form.ContainsKey("contentType") && contentType is null) || (form.ContainsKey("payloadFormat") && format is null))
        {
            return new ServiceOptions(null, null, null, null, null, null,
                "'contentType' is application/json or application/x-www-form-urlencoded, and 'payloadFormat' json or pjson.");
        }

        int? retries = null;

        if (form.ContainsKey("maxRetries"))
        {
            if (!int.TryParse(form["maxRetries"], out int r) || r is < 0 or > 5)
            {
                return new ServiceOptions(null, null, null, null, null, null, "'maxRetries' is a number from 0 to 5.");
            }

            retries = r;
        }

        string? kept = null;

        if (form.ContainsKey("scheduleInfo"))
        {
            JsonNode? schedule;

            try
            {
                schedule = JsonNode.Parse(form["scheduleInfo"].ToString() is { Length: > 0 } text ? text : "null");
            }
            catch (System.Text.Json.JsonException)
            {
                return new ServiceOptions(null, null, null, null, null, null, "'scheduleInfo' is not valid JSON.");
            }

            JsonObject all = (properties is null ? null : JsonNode.Parse(properties) as JsonObject) ?? [];
            all["scheduleInfo"] = schedule;
            kept = all.ToJsonString();

            if (kept.Length > WebhookRequests.PropertiesLimit)
            {
                return new ServiceOptions(null, null, null, null, null, null, "'scheduleInfo' is too large.");
            }
        }

        string secret = form["signatureKey"].ToString();

        return secret.Length > WebhookRequests.SecretLimit
            ? new ServiceOptions(null, null, null, null, null, null, $"'signatureKey' is at most {WebhookRequests.SecretLimit} characters.")
            : new ServiceOptions(types, active, contentType, format, retries, kept, null);
    }
}
