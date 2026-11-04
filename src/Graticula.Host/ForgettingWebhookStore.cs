using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Webhooks;

namespace Graticula.Host;

/// <summary>
/// The webhook store the server's endpoints write through, which makes the edit path forget which services have webhooks
/// whenever one is made, changed or removed — ADR-266 condition 5.
/// </summary>
/// <remarks>
/// <b>Here rather than at each endpoint</b>: eight calls in two faces write webhooks, and a ninth added later would be the
/// one that left an edit unannounced for <see cref="WebhookedServices.Lifetime"/>. A webhook the sender turns off after
/// failures is not forgotten: the edit path then asks once more than it needs to, and the enqueue finds nobody.
/// </remarks>
internal sealed class ForgettingWebhookStore(IWebhookStore inner, WebhookedServices webhooked) : IWebhookStore
{
    public async Task<WebhookDefinition?> CreateAsync(NewWebhook made, CancellationToken cancellationToken)
    {
        WebhookDefinition? created = await inner.CreateAsync(made, cancellationToken).ConfigureAwait(false);
        webhooked.Forget();
        return created;
    }

    public async Task<WebhookDefinition?> UpdateAsync(Guid id, WebhookUpdate update, Guid by, CancellationToken cancellationToken)
    {
        WebhookDefinition? changed = await inner.UpdateAsync(id, update, by, cancellationToken).ConfigureAwait(false);
        webhooked.Forget();
        return changed;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        bool deleted = await inner.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        webhooked.Forget();
        return deleted;
    }

    public Task<IReadOnlyList<WebhookDefinition>> ListAsync(WebhookKind kind, Guid? serviceId, CancellationToken cancellationToken) =>
        inner.ListAsync(kind, serviceId, cancellationToken);

    public Task<WebhookDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

    public Task<WebhookDefinition?> FindByNumberAsync(Guid serviceId, int number, CancellationToken cancellationToken) =>
        inner.FindByNumberAsync(serviceId, number, cancellationToken);

    public Task<int> EnqueuePortalAsync(IReadOnlyList<string> uris, string eventName, string body, CancellationToken cancellationToken) =>
        inner.EnqueuePortalAsync(uris, eventName, body, cancellationToken);

    public Task<int> EnqueueServiceAsync(
        Guid serviceId, IReadOnlyList<string> changeTypes, string eventName, string body, CancellationToken cancellationToken) =>
        inner.EnqueueServiceAsync(serviceId, changeTypes, eventName, body, cancellationToken);

    public Task<bool> ServiceHasWebhooksAsync(Guid serviceId, CancellationToken cancellationToken) =>
        inner.ServiceHasWebhooksAsync(serviceId, cancellationToken);

    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(string worker, int count, TimeSpan lease, CancellationToken cancellationToken) =>
        inner.ClaimAsync(worker, count, lease, cancellationToken);

    public Task DeliveredAsync(Guid id, string worker, int status, CancellationToken cancellationToken) =>
        inner.DeliveredAsync(id, worker, status, cancellationToken);

    public Task<bool> FailedAsync(
        Guid id, string worker, int? status, string reason, DateTimeOffset? retryAt, CancellationToken cancellationToken) =>
        inner.FailedAsync(id, worker, status, reason, retryAt, cancellationToken);

    public Task DropAsync(Guid id, string worker, string why, CancellationToken cancellationToken) =>
        inner.DropAsync(id, worker, why, cancellationToken);

    public Task<IReadOnlyList<WebhookDeliveryRecord>> RecentAsync(Guid webhookId, int count, CancellationToken cancellationToken) =>
        inner.RecentAsync(webhookId, count, cancellationToken);

    public Task<Guid?> ServiceIdAsync(string name, string? folder, CancellationToken cancellationToken) =>
        inner.ServiceIdAsync(name, folder, cancellationToken);

    public Task<Guid?> GroupIdAsync(string name, CancellationToken cancellationToken) => inner.GroupIdAsync(name, cancellationToken);

    public Task<int> PruneAsync(TimeSpan keep, int perWebhook, CancellationToken cancellationToken) =>
        inner.PruneAsync(keep, perWebhook, cancellationToken);
}
