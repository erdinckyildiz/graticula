using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Graticula.Platform.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The organisation's webhooks — <c>portals/self/webhooks</c>, with <c>createWebhook</c>, each webhook, its
/// <c>update</c>, <c>delete</c>, <c>activate</c>, <c>deactivate</c> and <c>notificationStatus</c> (ADR-266, the ArcGIS
/// veteran's review of v1.0.499, #15).
/// </summary>
/// <remarks>
/// <para>
/// <b>For an administrator who may manage the organisation's security and see all of its content</b>
/// (<c>admin:manageSecurity</c> and <c>admin:viewAllContent</c>): an organisation's webhook tells another system about
/// every item, group and member, so it is made only by somebody who may already see all of them, and the sender asks
/// again at every delivery (ADR-266 §7). ArcGIS says only <i>administrators</i>; the two privileges are what that means
/// here.
/// </para>
/// <para>
/// <b>The secret is taken and never given back</b>: it is sealed with the deployment's key, a webhook's document says
/// <c>"secret": ""</c> and <c>hasSecret</c>, and a new one replaces it through <c>update</c>.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static void MapWebhooks(WebApplication app)
    {
        string root = $"{Path}/portals/self/webhooks";

        Discoverable(app, root, ListWebhooksAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPost($"{root}/createWebhook", CreateWebhookAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        Discoverable(app, $"{root}/{{webhookId}}", WebhookAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPost($"{root}/{{webhookId}}/update", UpdateWebhookAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{root}/{{webhookId}}/delete", DeleteWebhookAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{root}/{{webhookId}}/activate", (HttpContext c, string webhookId, CancellationToken t) =>
            SetWebhookActiveAsync(c, webhookId, true, t)).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{root}/{{webhookId}}/deactivate", (HttpContext c, string webhookId, CancellationToken t) =>
            SetWebhookActiveAsync(c, webhookId, false, t)).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        Discoverable(app, $"{root}/{{webhookId}}/notificationStatus", NotificationStatusAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        Discoverable(app, $"{root}/settings", WebhookSettings).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPost($"{root}/settings/update", UpdateWebhookSettings).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
    }

    /// <summary>
    /// <c>webhooks/settings</c>: how this server delivers, in the three numbers Portal's settings carry — ADR-266 condition
    /// 4. The ArcGIS API for Python's <c>gis.admin.webhooks.settings</c> reads them, and was answered 404.
    /// </summary>
    /// <remarks>
    /// <b>What the sender does, not what was configured</b>: a portal webhook is retried <c>MaxRetries</c> (3) times, so
    /// four attempts; each waits <see cref="WebhookTargets.RequestTimeout"/> for an answer; the first retry follows ten
    /// seconds after a failure and the later ones further apart (<see cref="WebhookDeliverer.Backoff"/>), which one number
    /// can only begin to say.
    /// </remarks>
    private static IResult WebhookSettings(HttpContext context) =>
        !ManagesWebhooks(context.Features.Get<RequestPrincipal>()!)
            ? PortalError(403, WebhooksRefused)
            : Results.Ok(new
            {
                notificationAttempts = PortalWebhookRetries + 1,
                notificationTimeOutInSeconds = (int)WebhookTargets.RequestTimeout.TotalSeconds,
                notificationElapsedTimeInSeconds = (int)WebhookDeliverer.Backoff[0].TotalSeconds,
            });

    /// <summary>
    /// <c>webhooks/settings/update</c>: refused in words. <b>INFERRED</b> (ADR-266 condition 4): the delivery policy is
    /// this server's, one retry ladder for every webhook, and making it an organisation's setting is a decision of its own.
    /// </summary>
    private static IResult UpdateWebhookSettings(HttpContext context) =>
        !ManagesWebhooks(context.Features.Get<RequestPrincipal>()!)
            ? PortalError(403, WebhooksRefused)
            : PortalError(400,
                "Webhook delivery settings are fixed on this server: four attempts, a 15-second wait for each answer, and "
                + "retries 10 seconds, 1 minute and 5 minutes after the failures before them (ADR-266).");

    /// <summary>How many times a portal webhook's delivery is retried after its first attempt.</summary>
    private const int PortalWebhookRetries = 3;

    /// <summary>Whether the caller may manage the organisation's webhooks — ADR-266 §7.</summary>
    internal static bool ManagesWebhooks(RequestPrincipal current) =>
        current.Authorization.Allows(Privilege.AdminManageSecurity) && current.Authorization.Allows(Privilege.AdminViewAllContent);

    private const string WebhooksRefused =
        "The organization's webhooks are managed by an administrator who may manage security and view all content.";

    /// <summary>One webhook as ArcGIS's portal writes one — and never its secret.</summary>
    private static object PortalWebhook(HttpContext context, WebhookDefinition hook) => new
    {
        id = hook.Id.ToString("N"),
        accountId = PortalId(context),
        payloadUrl = hook.Url,
        secret = string.Empty,
        hasSecret = hook.HasSecret,
        isActive = hook.Active,
        name = hook.Name,
        config = new
        {
            deactivationPolicy = new { numberOfFailures = hook.FailuresAllowed, daysInPast = hook.FailureDays },
            properties = JsonNode.Parse(hook.Properties),
        },
        ownerId = hook.Owner.ToString("N"),
        owner = hook.OwnerName,
        modifiedId = hook.ModifiedBy?.ToString("N"),
        created = WebhookRequests.Millis(hook.Created),
        modified = WebhookRequests.Millis(hook.Modified),
        events = hook.Events,
        contentType = hook.ContentType,
        payloadFormat = hook.PayloadFormat,
        deactivatedReason = hook.DeactivatedReason,
    };

    private static async Task<IResult> ListWebhooksAsync(HttpContext context, IWebhookStore store, CancellationToken cancellation)
    {
        if (!ManagesWebhooks(context.Features.Get<RequestPrincipal>()!))
        {
            return PortalError(403, WebhooksRefused);
        }

        IReadOnlyList<WebhookDefinition> all = await store.ListAsync(WebhookKind.Portal, null, cancellation).ConfigureAwait(false);

        // <b>Paged as Portal pages it</b> (ADR-266 condition 4): the ArcGIS API for Python's `gis.admin.webhooks.list()`
        // reads `nextStart` and asks again until it is -1, and failed with a KeyError on the bare array this answered.
        PortalPage.Page page = PortalPage.Of(
            [.. all.Select(h => PortalWebhook(context, h))], context,
            context.Request.HasFormContentType ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false) : null);

        return Results.Ok(new
        {
            total = page.Total,
            start = page.Start,
            num = page.Num,
            nextStart = page.NextStart,
            webhooks = page.Results,
        });
    }

    private static async Task<WebhookDefinition?> PortalWebhookAsync(IWebhookStore store, string webhookId, CancellationToken cancellation) =>
        Guid.TryParse(webhookId, out Guid id) && await store.FindAsync(id, cancellation).ConfigureAwait(false) is { Kind: WebhookKind.Portal } hook
            ? hook
            : null;

    private static async Task<IResult> WebhookAsync(HttpContext context, string webhookId, IWebhookStore store, CancellationToken cancellation)
    {
        if (!ManagesWebhooks(context.Features.Get<RequestPrincipal>()!))
        {
            return PortalError(403, WebhooksRefused);
        }

        return await PortalWebhookAsync(store, webhookId, cancellation).ConfigureAwait(false) is { } hook
            ? Results.Ok(PortalWebhook(context, hook))
            : PortalError(404, $"There is no webhook '{webhookId}'.");
    }

    /// <summary>
    /// <c>createWebhook</c>: <c>name</c>, <c>url</c>, <c>secret</c>, <c>config</c> with its deactivation policy and
    /// properties, <c>changes</c> (<c>manualChanges</c> or <c>allChanges</c>) and <c>events</c> — and the URL checked as
    /// every connection to it will be (ADR-266 §4).
    /// </summary>
    private static async Task<IResult> CreateWebhookAsync(
        HttpContext context, IWebhookStore store, SecretProtector protector, HostSettings settings, IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!ManagesWebhooks(current))
        {
            return PortalError(403, WebhooksRefused);
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string name = form["name"].ToString().Trim();

        if (name.Length is 0 or > 256)
        {
            return PortalError(400, "'name' is required, at most 256 characters.");
        }

        if (!WebhookTargets.TryCheck(form["url"].ToString(), settings.WebhooksAllowPrivate, out Uri? url, out string? refused))
        {
            return PortalError(400, refused!);
        }

        string changes = form["changes"].ToString().Trim();
        string[] events;

        if (changes.Equals("allChanges", StringComparison.OrdinalIgnoreCase))
        {
            events = ["/"];
        }
        else if (changes.Length > 0 && !changes.Equals("manualChanges", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400, "'changes' is manualChanges or allChanges.");
        }
        else if (!WebhookRequests.TryReadPortalEvents(form["events"].ToString(), out events, out string? badEvents))
        {
            return PortalError(400, badEvents!);
        }

        if (!WebhookRequests.TryReadConfig(form["config"].ToString(), out int? failures, out int? days, out string? properties, out string? badConfig))
        {
            return PortalError(400, badConfig!);
        }

        string? contentType = WebhookRequests.ContentType(form["contentType"].ToString());
        string? format = WebhookRequests.PayloadFormat(form["payloadFormat"].ToString());

        if (contentType is null || format is null)
        {
            return PortalError(400, "'contentType' is application/json or application/x-www-form-urlencoded, and 'payloadFormat' json or pjson.");
        }

        string secret = form["secret"].ToString();

        if (secret.Length > WebhookRequests.SecretLimit)
        {
            return PortalError(400, $"'secret' is at most {WebhookRequests.SecretLimit} characters.");
        }

        WebhookDefinition? made = await store.CreateAsync(
            new NewWebhook(
                WebhookKind.Portal, null, name, url!.AbsoluteUri, events,
                secret.Length > 0 ? protector.Protect(secret) : null, secret.Length > 0 ? protector.KeyVersion : null,
                Active: true, current.Principal.Id, TileFaces.Origin(context), contentType, format,
                MaxRetries: PortalWebhookRetries, failures ?? 5, days ?? 5, properties ?? "{}"),
            cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, audit, "webhook.create", made!.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "portal", name, host = url.Host, events }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true, webhook = PortalWebhook(context, made) });
    }

    /// <summary><c>update</c>: any of <c>name</c>, <c>url</c>, <c>secret</c>, <c>config</c>, <c>changes</c>/<c>events</c>.</summary>
    private static async Task<IResult> UpdateWebhookAsync(
        HttpContext context, string webhookId, IWebhookStore store, SecretProtector protector, HostSettings settings, IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!ManagesWebhooks(current))
        {
            return PortalError(403, WebhooksRefused);
        }

        if (await PortalWebhookAsync(store, webhookId, cancellation).ConfigureAwait(false) is not { } hook)
        {
            return PortalError(404, $"There is no webhook '{webhookId}'.");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string? name = form.ContainsKey("name") ? form["name"].ToString().Trim() : null;

        if (name is { Length: 0 or > 256 })
        {
            return PortalError(400, "'name' is at most 256 characters, and not empty.");
        }

        Uri? url = null;

        if (form.ContainsKey("url") && !WebhookTargets.TryCheck(form["url"].ToString(), settings.WebhooksAllowPrivate, out url, out string? refused))
        {
            return PortalError(400, refused!);
        }

        string[]? events = null;

        if (form["changes"].ToString().Equals("allChanges", StringComparison.OrdinalIgnoreCase))
        {
            events = ["/"];
        }
        else if (form.ContainsKey("events"))
        {
            if (!WebhookRequests.TryReadPortalEvents(form["events"].ToString(), out string[] read, out string? badEvents))
            {
                return PortalError(400, badEvents!);
            }

            events = read;
        }

        if (!WebhookRequests.TryReadConfig(form["config"].ToString(), out int? failures, out int? days, out string? properties, out string? badConfig))
        {
            return PortalError(400, badConfig!);
        }

        string? contentType = form.ContainsKey("contentType") ? WebhookRequests.ContentType(form["contentType"].ToString()) : hook.ContentType;
        string? format = form.ContainsKey("payloadFormat") ? WebhookRequests.PayloadFormat(form["payloadFormat"].ToString()) : hook.PayloadFormat;

        if (contentType is null || format is null)
        {
            return PortalError(400, "'contentType' is application/json or application/x-www-form-urlencoded, and 'payloadFormat' json or pjson.");
        }

        string? secret = form.ContainsKey("secret") ? form["secret"].ToString() : null;

        if (secret is { Length: > WebhookRequests.SecretLimit })
        {
            return PortalError(400, $"'secret' is at most {WebhookRequests.SecretLimit} characters.");
        }

        WebhookDefinition? changed = await store.UpdateAsync(
            hook.Id,
            new WebhookUpdate(
                Name: name, Url: url?.AbsoluteUri, Events: events,
                Secret: secret is null ? null : secret.Length == 0 ? [] : protector.Protect(secret),
                SecretKeyVersion: secret is { Length: > 0 } ? protector.KeyVersion : null,
                ContentType: contentType, PayloadFormat: format,
                FailuresAllowed: failures, FailureDays: days, Properties: properties),
            current.Principal.Id, cancellation).ConfigureAwait(false);

        if (changed is null)
        {
            return PortalError(404, $"There is no webhook '{webhookId}'.");
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.update", hook.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "portal", name = changed.Name, host = WebhookRequests.HostOf(changed.Url), secretChanged = secret is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true, webhook = PortalWebhook(context, changed) });
    }

    private static async Task<IResult> DeleteWebhookAsync(
        HttpContext context, string webhookId, IWebhookStore store, IAuditLog audit, CancellationToken cancellation)
    {
        if (!ManagesWebhooks(context.Features.Get<RequestPrincipal>()!))
        {
            return PortalError(403, WebhooksRefused);
        }

        if (await PortalWebhookAsync(store, webhookId, cancellation).ConfigureAwait(false) is not { } hook
            || !await store.DeleteAsync(hook.Id, cancellation).ConfigureAwait(false))
        {
            return PortalError(404, $"There is no webhook '{webhookId}'.");
        }

        await AdminEndpoints.AuditAsync(context, audit, "webhook.delete", hook.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "portal", name = hook.Name }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> SetWebhookActiveAsync(HttpContext context, string webhookId, bool active, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!ManagesWebhooks(current))
        {
            return PortalError(403, WebhooksRefused);
        }

        IWebhookStore store = context.RequestServices.GetRequiredService<IWebhookStore>();

        if (await PortalWebhookAsync(store, webhookId, cancellation).ConfigureAwait(false) is not { } hook
            || await store.UpdateAsync(hook.Id, new WebhookUpdate(Active: active), current.Principal.Id, cancellation).ConfigureAwait(false) is null)
        {
            return PortalError(404, $"There is no webhook '{webhookId}'.");
        }

        await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(),
            active ? "webhook.activate" : "webhook.deactivate", hook.Id.ToString("N"),
            AdminEndpoints.Detail(new { kind = "portal", name = hook.Name }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true });
    }

    /// <summary>
    /// <c>notificationStatus</c>: the webhook's most recent deliveries, newest first, with each one's state, the receiver's
    /// status and why it failed — never the payload, which was the receiver's to keep.
    /// </summary>
    private static async Task<IResult> NotificationStatusAsync(
        HttpContext context, string webhookId, IWebhookStore store, CancellationToken cancellation)
    {
        if (!ManagesWebhooks(context.Features.Get<RequestPrincipal>()!))
        {
            return PortalError(403, WebhooksRefused);
        }

        if (await PortalWebhookAsync(store, webhookId, cancellation).ConfigureAwait(false) is not { } hook)
        {
            return PortalError(404, $"There is no webhook '{webhookId}'.");
        }

        // <b>Paged, and the rows under `WebhookStatus` as well</b> (ADR-266 condition 4): the ArcGIS API for Python's
        // `Webhook.notifications` posts `start` and `num` and reads `WebhookStatus` and `nextStart`, and raised a KeyError
        // on this answer's own name for them. The row's fields stay this server's; `notifications` stays for the callers
        // that read it.
        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false) : null;
        string? Asked(string key) => form?[key].ToString() is { Length: > 0 } posted ? posted : context.Request.Query[key].ToString();

        int num = int.TryParse(Asked("num"), out int n) ? Math.Clamp(n, 1, 100) : 25;
        int start = int.TryParse(Asked("start"), out int s) ? Math.Clamp(s, 1, 1000) : 1;

        // One more than the page, to know whether another follows; the store keeps at most 200 finished per webhook.
        IReadOnlyList<WebhookDeliveryRecord> recent = await store.RecentAsync(hook.Id, Math.Min(start - 1 + num + 1, 1000), cancellation)
            .ConfigureAwait(false);
        WebhookDeliveryRecord[] page = [.. recent.Skip(start - 1).Take(num)];
        object[] rows = [.. WebhookRequests.Deliveries(page)];

        // A dictionary, because the web serializer would write an anonymous `WebhookStatus` as `webhookStatus`.
        return Results.Ok(new Dictionary<string, object?>
        {
            ["webhookId"] = hook.Id.ToString("N"),
            ["isActive"] = hook.Active,
            ["deactivatedReason"] = hook.DeactivatedReason,
            ["total"] = recent.Count,
            ["start"] = start,
            ["num"] = page.Length,
            ["nextStart"] = recent.Count > start - 1 + num ? start + num : -1,
            ["WebhookStatus"] = rows,
            ["notifications"] = rows,
        });
    }
}
