using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Graticula.Host;

/// <summary>
/// Which services have an active feature-service webhook, remembered per node so an edit asks the store once per service
/// rather than once per edit — ADR-266 condition 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured before it was built.</b> The edit path's enqueue statement ran after every edit that kept something, webhooks
/// or none, and cost about 3 ms of a single-feature <c>applyEdits</c> on the fixture (mean 18.8–19.6 ms without it,
/// 20.9–23.9 ms with), over the tenth the condition allowed.
/// </para>
/// <para>
/// <b>Forgotten whole whenever a webhook is made, changed or removed</b>, on this node by the store that wrote it and on
/// the site's others by <see cref="SiteAnnouncements.Webhooks"/>. An entry is trusted for <see cref="Lifetime"/> at most,
/// the backstop for an announcement a listener missed while it reconnected: a webhook made then is heard from within that
/// time, never later.
/// </para>
/// <para>
/// <b>Keyed by the service's id, which no two organisations share</b>, and asked with the same organisation clause the
/// enqueue uses, so an answer is never another organisation's.
/// </para>
/// </remarks>
internal sealed class WebhookedServices(TimeProvider clock)
{
    /// <summary>The most services remembered; past it the whole is forgotten, and each is asked again once.</summary>
    public const int MaximumEntries = 10_000;

    /// <summary>How long an answer is trusted without an announcement.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (bool Has, DateTimeOffset At)> _known = new();
    private long _generation;

    /// <summary>Tells the site's other nodes to forget too; null on a single node.</summary>
    public SiteAnnouncements? Announcer { get; set; }

    /// <summary>
    /// Read before asking the store, and handed back with the answer: a forgetting between the two makes the answer one
    /// that may already be stale, and it is not kept.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Whether the service has a webhook, or null when this node does not know or no longer trusts what it knew.</summary>
    /// <param name="service">The service.</param>
    public bool? Known(Guid service) =>
        _known.TryGetValue(service, out (bool Has, DateTimeOffset At) entry) && clock.GetUtcNow() - entry.At < Lifetime
            ? entry.Has
            : null;

    /// <summary>Keeps what the store said, unless something was forgotten since <paramref name="generation"/>.</summary>
    /// <param name="service">The service.</param>
    /// <param name="has">Whether it has an active feature-service webhook.</param>
    /// <param name="generation">The <see cref="Generation"/> read before the store was asked.</param>
    public void Remember(Guid service, bool has, long generation)
    {
        if (_known.Count >= MaximumEntries)
        {
            _known.Clear();
        }

        if (Generation != generation)
        {
            return;
        }

        _known[service] = (has, clock.GetUtcNow());

        // A forgetting that landed between the check and the write would otherwise keep the stale answer.
        if (Generation != generation)
        {
            _known.TryRemove(service, out _);
        }
    }

    /// <summary>A webhook was made, changed or removed here: forget, and tell the site's other nodes.</summary>
    public void Forget()
    {
        ForgetHere();
        Announcer?.Announce(SiteAnnouncements.Webhooks);
    }

    /// <summary>Forgets on this node only — what another node's announcement asks.</summary>
    public void ForgetHere()
    {
        Interlocked.Increment(ref _generation);
        _known.Clear();
    }
}
