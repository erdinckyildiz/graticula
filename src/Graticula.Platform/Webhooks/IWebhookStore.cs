using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Webhooks;

/// <summary>Which of ArcGIS's two webhooks a definition is — ADR-266 §1.</summary>
public enum WebhookKind
{
    /// <summary>An organisation's, at <c>portals/self/webhooks</c>, on item, group, user and role events.</summary>
    Portal,

    /// <summary>A feature service's, at its administration's <c>webhooks</c>, on its edits.</summary>
    FeatureService,
}

/// <summary>A webhook as it is stored, without its secret — which is never read back to anybody (ADR-266 §6).</summary>
/// <param name="Id">Its id.</param>
/// <param name="Kind">Which kind.</param>
/// <param name="ServiceId">The service a feature-service webhook watches; null for a portal one.</param>
/// <param name="Number">A feature-service webhook's number within its service, as ArcGIS numbers them; null for a portal one.</param>
/// <param name="Name">Its name.</param>
/// <param name="Url">Where it posts.</param>
/// <param name="Events">The portal event URIs or the feature service change types it fires on.</param>
/// <param name="HasSecret">Whether a secret signs its deliveries.</param>
/// <param name="Active">Whether it fires.</param>
/// <param name="Owner">Who made it.</param>
/// <param name="OwnerName">Their name, or null if gone.</param>
/// <param name="ModifiedBy">Who last changed it, or null.</param>
/// <param name="BaseUrl">The server's address as its maker reached it, which its payloads name.</param>
/// <param name="ContentType">How the payload is posted: <c>application/json</c> or a form.</param>
/// <param name="PayloadFormat"><c>json</c> or <c>pjson</c>.</param>
/// <param name="MaxRetries">How many times a failed delivery is tried again.</param>
/// <param name="FailuresAllowed">The deactivation policy's <c>numberOfFailures</c>.</param>
/// <param name="FailureDays">The deactivation policy's <c>daysInPast</c>.</param>
/// <param name="Properties">The <c>config.properties</c> object it was given, as JSON.</param>
/// <param name="DeactivatedReason">Why the server deactivated it, or null.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Modified">When it last changed.</param>
public sealed record WebhookDefinition(
    Guid Id,
    WebhookKind Kind,
    Guid? ServiceId,
    int? Number,
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    bool HasSecret,
    bool Active,
    Guid Owner,
    string? OwnerName,
    Guid? ModifiedBy,
    string BaseUrl,
    string ContentType,
    string PayloadFormat,
    int MaxRetries,
    int FailuresAllowed,
    int FailureDays,
    string Properties,
    string? DeactivatedReason,
    DateTimeOffset Created,
    DateTimeOffset Modified);

/// <summary>What a new webhook is made with.</summary>
/// <param name="Kind">Which kind.</param>
/// <param name="ServiceId">The service, for a feature-service webhook.</param>
/// <param name="Name">Its name.</param>
/// <param name="Url">Where it posts, already checked.</param>
/// <param name="Events">What it fires on.</param>
/// <param name="Secret">The secret, sealed, or null.</param>
/// <param name="SecretKeyVersion">The key the secret is sealed with, or null.</param>
/// <param name="Active">Whether it fires from the start.</param>
/// <param name="Owner">Who makes it.</param>
/// <param name="BaseUrl">The server's address as its maker reached it.</param>
/// <param name="ContentType">How the payload is posted.</param>
/// <param name="PayloadFormat"><c>json</c> or <c>pjson</c>.</param>
/// <param name="MaxRetries">Retries after a failure.</param>
/// <param name="FailuresAllowed">Failures before deactivation.</param>
/// <param name="FailureDays">The window the failures are counted in.</param>
/// <param name="Properties">The <c>config.properties</c> object, as JSON.</param>
public sealed record NewWebhook(
    WebhookKind Kind,
    Guid? ServiceId,
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    byte[]? Secret,
    int? SecretKeyVersion,
    bool Active,
    Guid Owner,
    string BaseUrl,
    string ContentType,
    string PayloadFormat,
    int MaxRetries,
    int FailuresAllowed,
    int FailureDays,
    string Properties);

/// <summary>A change to a webhook; each null leaves its part as it is.</summary>
/// <param name="Name">A new name.</param>
/// <param name="Url">A new address, already checked.</param>
/// <param name="Events">New events.</param>
/// <param name="Secret">A new sealed secret; an empty array removes it.</param>
/// <param name="SecretKeyVersion">The key the new secret is sealed with.</param>
/// <param name="Active">Whether it fires.</param>
/// <param name="ContentType">How the payload is posted.</param>
/// <param name="PayloadFormat"><c>json</c> or <c>pjson</c>.</param>
/// <param name="MaxRetries">Retries after a failure.</param>
/// <param name="FailuresAllowed">Failures before deactivation.</param>
/// <param name="FailureDays">The window the failures are counted in.</param>
/// <param name="Properties">The <c>config.properties</c> object, as JSON.</param>
public sealed record WebhookUpdate(
    string? Name = null,
    string? Url = null,
    IReadOnlyList<string>? Events = null,
    byte[]? Secret = null,
    int? SecretKeyVersion = null,
    bool? Active = null,
    string? ContentType = null,
    string? PayloadFormat = null,
    int? MaxRetries = null,
    int? FailuresAllowed = null,
    int? FailureDays = null,
    string? Properties = null);

/// <summary>A delivery a sender has claimed, with what it needs to send it and to decide whether it still may.</summary>
/// <param name="Id">The delivery.</param>
/// <param name="Webhook">The webhook it is for.</param>
/// <param name="Event">The event's name, as the delivery log shows it.</param>
/// <param name="Body">The event, as JSON; the envelope is made when it is sent.</param>
/// <param name="Attempts">This attempt's number, from one.</param>
/// <param name="Tenant">The organisation it belongs to.</param>
/// <param name="Secret">The sealed secret, or null.</param>
/// <param name="SecretKeyVersion">Its key, or null.</param>
/// <param name="Standing">Whether the webhook's owner may still be told about this.</param>
public sealed record ClaimedDelivery(
    Guid Id,
    WebhookDefinition Webhook,
    string Event,
    string Body,
    int Attempts,
    Guid Tenant,
    byte[]? Secret,
    int? SecretKeyVersion,
    OwnerStanding Standing);

/// <summary>
/// What the store knows about a webhook's owner when a delivery is claimed, so the sender can ask whether they may still
/// see what it is about (ADR-266 §7): their user type, their roles, whether they are disabled, and — for a feature-service
/// webhook — whether they still own the service.
/// </summary>
/// <param name="UserType">Their user type, or null if they are gone.</param>
/// <param name="Roles">Their roles.</param>
/// <param name="Disabled">Whether their account is disabled.</param>
/// <param name="OwnsService">For a feature-service webhook, whether they own the service; false otherwise.</param>
public sealed record OwnerStanding(string? UserType, IReadOnlyList<string> Roles, bool Disabled, bool OwnsService);

/// <summary>One delivery, as the webhook's recent deliveries list it.</summary>
/// <param name="Id">The delivery.</param>
/// <param name="Event">The event's name.</param>
/// <param name="Status"><c>pending</c>, <c>delivered</c>, <c>failed</c> or <c>dropped</c>.</param>
/// <param name="Attempts">How many times it was tried.</param>
/// <param name="LastStatus">The receiver's last HTTP status, or null.</param>
/// <param name="LastError">Why the last attempt failed, or null.</param>
/// <param name="Created">When the event was raised.</param>
/// <param name="Finished">When it was delivered or given up, or null.</param>
public sealed record WebhookDeliveryRecord(
    Guid Id,
    string Event,
    string Status,
    int Attempts,
    int? LastStatus,
    string? LastError,
    DateTimeOffset Created,
    DateTimeOffset? Finished);

/// <summary>The webhooks and their delivery outbox — ADR-266.</summary>
/// <remarks>
/// <para>
/// <b>An outbox, not a queue in memory.</b> An event is a row from the moment it is raised, so a restart loses nothing
/// that was raised before it; a delivery is claimed by one node at a time with <c>for update skip locked</c> and a lease,
/// so a site of several nodes sends each once per attempt, and a node that dies mid-send leaves a lease that lapses and
/// another node sends it again. That is at least once, and a receiver is told so by the delivery id it is sent.
/// </para>
/// <para>
/// <b>Every read and write is the caller's organisation's</b> through the floor (ADR-204); the sender, which belongs to no
/// organisation, claims across all of them and sends each delivery as its own.
/// </para>
/// </remarks>
public interface IWebhookStore
{
    /// <summary>The most webhooks a feature service may have, ArcGIS's own number.</summary>
    public const int PerService = 10;

    /// <summary>The most undelivered events one webhook may hold; another is recorded as dropped.</summary>
    public const int PendingBound = 1000;

    /// <summary>Makes a webhook.</summary>
    /// <param name="made">What it is made with.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>It, or null when a feature service already has <see cref="PerService"/>.</returns>
    Task<WebhookDefinition?> CreateAsync(NewWebhook made, CancellationToken cancellationToken);

    /// <summary>The organisation's webhooks of a kind — a service's, when one is named.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="serviceId">The service, for feature-service webhooks.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Them, oldest first.</returns>
    Task<IReadOnlyList<WebhookDefinition>> ListAsync(WebhookKind kind, Guid? serviceId, CancellationToken cancellationToken);

    /// <summary>One webhook, by its id.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>It, or null.</returns>
    Task<WebhookDefinition?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One of a feature service's webhooks, by its number.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="number">Its number.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>It, or null.</returns>
    Task<WebhookDefinition?> FindByNumberAsync(Guid serviceId, int number, CancellationToken cancellationToken);

    /// <summary>Changes a webhook.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="update">What changes.</param>
    /// <param name="by">Who changes it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>It as it now is, or null when it is not there.</returns>
    Task<WebhookDefinition?> UpdateAsync(Guid id, WebhookUpdate update, Guid by, CancellationToken cancellationToken);

    /// <summary>Removes a webhook, and its deliveries with it.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was there.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Raises a portal event for every active portal webhook of the organisation that listens for one of its URIs.</summary>
    /// <param name="uris">Every URI the event answers to, from <c>/</c> to <c>/items/{id}/update</c>.</param>
    /// <param name="eventName">The event's name, for the delivery log.</param>
    /// <param name="body">The event, as JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many deliveries were queued.</returns>
    Task<int> EnqueuePortalAsync(IReadOnlyList<string> uris, string eventName, string body, CancellationToken cancellationToken);

    /// <summary>Raises a feature-service event for every active webhook of the service that listens for one of its types.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="changeTypes">The change types the event is, with <c>*</c>.</param>
    /// <param name="eventName">The event's name, for the delivery log.</param>
    /// <param name="body">The event, as JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many deliveries were queued.</returns>
    Task<int> EnqueueServiceAsync(
        Guid serviceId, IReadOnlyList<string> changeTypes, string eventName, string body, CancellationToken cancellationToken);

    /// <summary>Takes up to <paramref name="count"/> due deliveries for this sender, across every organisation.</summary>
    /// <param name="worker">Who is sending.</param>
    /// <param name="count">The most to take.</param>
    /// <param name="lease">How long they are this sender's before another may take them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The deliveries taken, each counted as an attempt.</returns>
    Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(string worker, int count, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Records that a claimed delivery was accepted by its receiver.</summary>
    /// <param name="id">The delivery.</param>
    /// <param name="worker">The sender that claimed it.</param>
    /// <param name="status">The receiver's status.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DeliveredAsync(Guid id, string worker, int status, CancellationToken cancellationToken);

    /// <summary>
    /// Records that an attempt failed — to be tried again at <paramref name="retryAt"/>, or given up when it is null — and
    /// deactivates the webhook when its failures in its window reach its policy.
    /// </summary>
    /// <param name="id">The delivery.</param>
    /// <param name="worker">The sender that claimed it.</param>
    /// <param name="status">The receiver's status, or null when none came.</param>
    /// <param name="reason">Why, bounded.</param>
    /// <param name="retryAt">When to try again, or null to give up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the webhook was deactivated by this failure.</returns>
    Task<bool> FailedAsync(
        Guid id, string worker, int? status, string reason, DateTimeOffset? retryAt, CancellationToken cancellationToken);

    /// <summary>Gives up a claimed delivery without sending it — its owner may no longer see what it is about.</summary>
    /// <param name="id">The delivery.</param>
    /// <param name="worker">The sender that claimed it.</param>
    /// <param name="why">Why.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DropAsync(Guid id, string worker, string why, CancellationToken cancellationToken);

    /// <summary>A webhook's most recent deliveries.</summary>
    /// <param name="webhookId">The webhook.</param>
    /// <param name="count">The most to list.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Them, newest first.</returns>
    Task<IReadOnlyList<WebhookDeliveryRecord>> RecentAsync(Guid webhookId, int count, CancellationToken cancellationToken);

    /// <summary>A service's id by its folder and name in the caller's organisation, for an event that names it so.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its id, or null when there is none.</returns>
    Task<Guid?> ServiceIdAsync(string name, string? folder, CancellationToken cancellationToken);

    /// <summary>A group's id by its name in the caller's organisation, for an event that names it so.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its id, or null when there is none.</returns>
    Task<Guid?> GroupIdAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes finished deliveries older than <paramref name="keep"/>, and all but each webhook's newest few.</summary>
    /// <param name="keep">How long a finished delivery is kept.</param>
    /// <param name="perWebhook">How many finished deliveries each webhook keeps at most.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many were removed.</returns>
    Task<int> PruneAsync(TimeSpan keep, int perWebhook, CancellationToken cancellationToken);
}
