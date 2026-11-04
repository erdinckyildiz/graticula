using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Graticula.Platform.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Raises webhook events into the outbox — ADR-266 §2. <b>Never fails the act that raised them</b>: the act has already
/// happened and been audited, and a webhook that cannot be queued is logged, not turned into the caller's error.
/// </summary>
internal sealed partial class WebhookRaiser(IWebhookStore store, WebhookSignal signal, TimeProvider clock, ILogger<WebhookRaiser> log)
{
    [LoggerMessage(EventId = 2600, Level = LogLevel.Warning, Message = "A webhook event {Event} could not be queued: {Reason}")]
    private partial void NotQueued(string @event, string reason);

    /// <summary>Raises the portal event an audited act is, if it is one.</summary>
    /// <param name="entry">The audit row.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RaiseAsync(AuditEvent entry, CancellationToken cancellation)
    {
        if (!entry.Succeeded || !WebhookEvents.PortalActs.ContainsKey(entry.Action))
        {
            return;
        }

        try
        {
            (string source, string? named, string? folder) = WebhookEvents.NamedBy(entry);

            // The id ArcGIS names each source by: an item's id, a group's id, a username, a role's name.
            string? id = source switch
            {
                "service" when named is not null =>
                    (await store.ServiceIdAsync(named, folder, cancellation).ConfigureAwait(false))?.ToString("N"),
                "group" when named is not null =>
                    (await store.GroupIdAsync(named, cancellation).ConfigureAwait(false))?.ToString("N"),
                _ => WebhookEvents.ArcGisId(named),
            };

            if (WebhookEvents.FromAudit(entry, id, clock.GetUtcNow()) is not { } raised)
            {
                return;
            }

            if (await store.EnqueuePortalAsync(raised.Uris, raised.Name, raised.Body.ToJsonString(), cancellation)
                    .ConfigureAwait(false) > 0)
            {
                signal.Wake();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            NotQueued(entry.Action, e.Message);
        }
    }

    /// <summary>Raises the feature-service event an edit is, if it kept something.</summary>
    /// <param name="layer">The layer edited.</param>
    /// <param name="outcome">What the edit did.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RaiseAsync(PublishedLayer layer, EditOutcome outcome, CancellationToken cancellation)
    {
        if (WebhookEvents.FromEdit(layer.ServiceName, layer.Folder, layer.LayerIndex, outcome, clock.GetUtcNow()) is not { } raised)
        {
            return;
        }

        try
        {
            if (await store.EnqueueServiceAsync(layer.ServiceId, raised.ChangeTypes, raised.Name, raised.Body.ToJsonString(), cancellation)
                    .ConfigureAwait(false) > 0)
            {
                signal.Wake();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            NotQueued(raised.Name, e.Message);
        }
    }
}

/// <summary>
/// The audit log, which also raises the portal event each audited act is — ADR-266 §2. The audit row is written first
/// and its failure fails the act, as it always has; the event follows it.
/// </summary>
internal sealed class WebhookAuditLog(IAuditLog inner, WebhookRaiser raiser) : IAuditLog
{
    /// <inheritdoc/>
    public async Task RecordAsync(AuditEvent entry, CancellationToken cancellationToken)
    {
        await inner.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        await raiser.RaiseAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Wakes this node's sender when this node queues a delivery; other nodes find it on their next look.</summary>
internal sealed class WebhookSignal : IDisposable
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>Says something was queued. Never accumulates.</summary>
    public void Wake()
    {
        try
        {
            _gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    /// <summary>Waits to be woken or for the time to run out.</summary>
    /// <param name="patience">How long.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WaitAsync(TimeSpan patience, CancellationToken cancellation)
    {
        try
        {
            await _gate.WaitAsync(patience, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the loop ends it.
        }
    }
}

/// <summary>What is posted for a delivery, and its signature — ADR-266 §3 and §6.</summary>
internal static class WebhookPayload
{
    /// <summary>ArcGIS's signature header.</summary>
    internal const string SignatureHeader = "x-esriHook-Signature";

    /// <summary>
    /// The body of a delivery: ArcGIS's portal envelope — <c>info</c>, <c>properties</c>, <c>events</c> — around a
    /// portal event, and ArcGIS's array of one object for a feature-service event, each in the webhook's format.
    /// </summary>
    /// <param name="hook">The webhook.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="body">The stored event.</param>
    /// <param name="when">When it is sent.</param>
    /// <returns>The bytes to post, and their media type.</returns>
    internal static (byte[] Bytes, string MediaType) Compose(WebhookDefinition hook, Guid deliveryId, string body, DateTimeOffset when)
    {
        JsonNode stored = JsonNode.Parse(body) ?? new JsonObject();
        string baseUrl = hook.BaseUrl.TrimEnd('/');
        JsonNode payload;

        if (hook.Kind == WebhookKind.Portal)
        {
            payload = new JsonObject
            {
                ["info"] = new JsonObject
                {
                    ["webhookId"] = hook.Id.ToString("N"),
                    ["webhookName"] = hook.Name,
                    ["portalURL"] = baseUrl + "/",
                    ["when"] = when.ToUnixTimeMilliseconds(),
                    ["deliveryId"] = deliveryId.ToString("N"),
                },
                ["properties"] = new JsonObject(),
                ["events"] = new JsonArray(stored),
            };
        }
        else
        {
            JsonObject edit = stored as JsonObject ?? [];
            string folder = edit["folder"]?.GetValue<string>() is { Length: > 0 } f ? f + "/" : string.Empty;
            string layerUrl = $"{baseUrl}/rest/services/{folder}{edit["serviceName"]?.GetValue<string>()}/FeatureServer/{edit["layerId"]}";
            long[] keys = [.. Ids(edit, "adds"), .. Ids(edit, "updates")];

            JsonObject one = new()
            {
                ["name"] = hook.Name,
                ["layerId"] = edit["layerId"]?.DeepClone(),
                ["serviceName"] = edit["serviceName"]?.DeepClone(),
                ["lastUpdatedTime"] = edit["lastUpdatedTime"]?.DeepClone(),
                ["events"] = edit["events"]?.DeepClone(),
                ["layerUrl"] = layerUrl,
                ["queryUrl"] = keys.Length is > 0 and <= 100
                    ? $"{layerUrl}/query?objectIds={string.Join("%2C", keys)}&outFields=*&f=json"
                    : null,
                ["objectIds"] = edit["objectIds"]?.DeepClone(),
                ["truncated"] = edit["truncated"]?.DeepClone(),
                ["deliveryId"] = deliveryId.ToString("N"),
            };

            payload = new JsonArray(one);
        }

        string json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = hook.PayloadFormat == "pjson" });

        return hook.ContentType == "application/x-www-form-urlencoded"
            ? (Encoding.UTF8.GetBytes("payload=" + Uri.EscapeDataString(json)), "application/x-www-form-urlencoded")
            : (Encoding.UTF8.GetBytes(json), "application/json");
    }

    /// <summary>The signature of the bytes posted: <c>sha256=</c> and the HMAC-SHA256 of them under the secret, in hex.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="bytes">The body as posted.</param>
    /// <returns>The header's value.</returns>
    internal static string Sign(string secret, byte[] bytes) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));

    private static IEnumerable<long> Ids(JsonObject edit, string kind) =>
        edit["objectIds"]?[kind] is JsonArray ids ? ids.Select(id => id!.GetValue<long>()) : [];
}

/// <summary>
/// Sends the outbox — ADR-266 §3: at least once, a few at a time, with backoff, by whichever node claims each delivery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every node runs one, and each delivery is sent by one at a time</b>: the claim is <c>for update skip locked</c>
/// with a lease (<see cref="IWebhookStore.ClaimAsync"/>), so two nodes take different rows. A node that dies holding one
/// leaves a lease that lapses, and another node sends it again — which is why the payload carries a delivery id.
/// </para>
/// <para>
/// <b>The owner is asked again at every send</b> (ADR-266 §7). An organisation's webhook tells its owner about every
/// item, group and member, so its owner must still be an administrator who may see all content; a feature service's
/// tells its owner about the service's edits, so its owner must still own the service or administer all content. A
/// delivery whose owner may no longer know is dropped, not sent.
/// </para>
/// </remarks>
internal sealed partial class WebhookDeliverer : BackgroundService
{
    /// <summary>How long after each failed attempt the next is made: ten seconds, a minute, five, thirty, two hours.</summary>
    internal static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    private const int Batch = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(7);
    private const int KeepPerWebhook = 200;
    private static readonly string Who = "graticula/webhooks " + Environment.MachineName + "#" + Environment.ProcessId;

    private readonly IWebhookStore _store;
    private readonly WebhookSignal _signal;
    private readonly SecretProtector _protector;
    private readonly IRoleGrants _grants;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebhookDeliverer> _log;
    private readonly HttpClient _http;

    public WebhookDeliverer(
        IWebhookStore store,
        WebhookSignal signal,
        SecretProtector protector,
        IRoleGrants grants,
        HostSettings settings,
        TimeProvider clock,
        ILogger<WebhookDeliverer> log)
    {
        _store = store;
        _signal = signal;
        _protector = protector;
        _grants = grants;
        _clock = clock;
        _log = log;
        _http = new HttpClient(WebhookTargets.Handler(settings.WebhooksAllowPrivate), disposeHandler: true)
        {
            Timeout = WebhookTargets.RequestTimeout,
            MaxResponseContentBufferSize = 64 * 1024,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Graticula-Webhook/1");
    }

    [LoggerMessage(EventId = 2601, Level = LogLevel.Warning, Message = "Webhook deliveries could not be claimed: {Reason}")]
    private partial void ClaimFailed(string reason);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Warning, Message = "Webhook {Webhook} was deactivated after failed deliveries: {Reason}")]
    private partial void Deactivated(Guid webhook, string reason);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Information, Message = "Webhook delivery {Delivery} to {Webhook} failed, attempt {Attempt}: {Reason}")]
    private partial void AttemptFailed(Guid delivery, Guid webhook, int attempt, string reason);

    /// <inheritdoc/>
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        DateTimeOffset pruned = DateTimeOffset.MinValue;

        while (!stopping.IsCancellationRequested)
        {
            IReadOnlyList<ClaimedDelivery> taken;

            try
            {
                if (_clock.GetUtcNow() - pruned > PruneEvery)
                {
                    await _store.PruneAsync(KeepFinished, KeepPerWebhook, stopping).ConfigureAwait(false);
                    pruned = _clock.GetUtcNow();
                }

                taken = await _store.ClaimAsync(Who, Batch, Lease, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                ClaimFailed(e.Message);
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), stopping).ConfigureAwait(false);
                continue;
            }

            if (taken.Count == 0)
            {
                await _signal.WaitAsync(Patience, stopping).ConfigureAwait(false);
                continue;
            }

            await Task.WhenAll(taken.Select(delivery => SendAsync(delivery, stopping))).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a webhook's owner may still be told about what its deliveries say — ADR-266 §7.</summary>
    /// <param name="hook">The webhook.</param>
    /// <param name="standing">Its owner, as the store read them at the claim.</param>
    /// <param name="grants">What each role grants.</param>
    /// <returns>Null when they may, otherwise why not.</returns>
    internal static string? Unentitled(WebhookDefinition hook, OwnerStanding standing, IRoleGrants grants)
    {
        if (standing.UserType is null || standing.Disabled)
        {
            return "The webhook's owner is disabled or gone, so nothing is sent on their behalf.";
        }

        Authorization authorization = Authorization.Resolve(standing.UserType, standing.Roles, grants, []);

        return hook.Kind switch
        {
            WebhookKind.Portal when !(authorization.Allows(Privilege.AdminManageSecurity)
                                      && authorization.Allows(Privilege.AdminViewAllContent)) =>
                "The webhook's owner no longer administers the organisation's security and content, which an "
                + "organisation's webhook tells them about.",
            WebhookKind.FeatureService when !standing.OwnsService && !authorization.Allows(Privilege.AdminManageAllContent) =>
                "The webhook's owner no longer owns the service or administers all content.",
            _ => null,
        };
    }

    private async Task SendAsync(ClaimedDelivery delivery, CancellationToken stopping)
    {
        WebhookDefinition hook = delivery.Webhook;

        try
        {
            if (!hook.Active)
            {
                await _store.DropAsync(delivery.Id, Who, "The webhook was deactivated before this was sent.", stopping).ConfigureAwait(false);
                return;
            }

            if (Unentitled(hook, delivery.Standing, _grants) is { } why)
            {
                await _store.DropAsync(delivery.Id, Who, why, stopping).ConfigureAwait(false);
                return;
            }

            (byte[] bytes, string mediaType) = WebhookPayload.Compose(hook, delivery.Id, delivery.Body, _clock.GetUtcNow());

            using HttpRequestMessage request = new(HttpMethod.Post, hook.Url) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("X-Graticula-Delivery", delivery.Id.ToString("N"));
            request.Headers.TryAddWithoutValidation("X-Graticula-Event", delivery.Event);

            if (delivery.Secret is { } sealedSecret && delivery.SecretKeyVersion is { } version)
            {
                request.Headers.TryAddWithoutValidation(
                    WebhookPayload.SignatureHeader, WebhookPayload.Sign(_protector.Unprotect(sealedSecret, version), bytes));
            }

            // The status line and the headers, never the body: a receiver's answer is not ours to keep.
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (status is >= 200 and < 300)
            {
                await _store.DeliveredAsync(delivery.Id, Who, status, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await FailAsync(delivery, status, status is >= 300 and < 400
                ? $"The receiver answered {status}, a redirect; a webhook follows none."
                : $"The receiver answered {status}.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping: the lease lapses and another node, or this one when it starts, sends it again.
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.IO.IOException
                                      or SecretProtectionException or InvalidOperationException or JsonException)
        {
            string reason = e is TaskCanceledException
                ? $"The receiver did not answer within {WebhookTargets.RequestTimeout.TotalSeconds:0} seconds."
                : Innermost(e).Message;

            try
            {
                await FailAsync(delivery, null, reason).ConfigureAwait(false);
            }
            catch (Exception recording) when (recording is not OperationCanceledException)
            {
                ClaimFailed(recording.Message);
            }
        }
    }

    private async Task FailAsync(ClaimedDelivery delivery, int? status, string reason)
    {
        WebhookDefinition hook = delivery.Webhook;
        AttemptFailed(delivery.Id, hook.Id, delivery.Attempts, reason);

        // Attempts are counted from one, so attempt n is retry n - 1; after the last retry, the delivery is given up.
        DateTimeOffset? retry = delivery.Attempts <= hook.MaxRetries
            ? _clock.GetUtcNow() + Backoff[Math.Min(delivery.Attempts - 1, Backoff.Length - 1)]
            : null;

        if (await _store.FailedAsync(delivery.Id, Who, status, reason, retry, CancellationToken.None).ConfigureAwait(false))
        {
            Deactivated(hook.Id, reason);
        }
    }

    private static Exception Innermost(Exception e)
    {
        while (e.InnerException is { } inner)
        {
            e = inner;
        }

        return e;
    }
}
