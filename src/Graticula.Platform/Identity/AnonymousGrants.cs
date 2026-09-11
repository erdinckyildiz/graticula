using System;
using System.Collections.Generic;
using System.Threading;

namespace Graticula.Platform.Identity;

/// <summary>
/// What the anonymous principal is granted, held between requests while somebody is listening
/// for it to change.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-249](../../../docs/architecture-debt.md), by owner decision 2026-09-11.</b> Every
/// anonymous request asked the platform store what an anonymous caller may do, and that round trip
/// was measured at 2.6× to 3.9× of this server's ceiling on a path that otherwise touches nothing.
/// Offered a thirty-second hold, no hold, or a hold that hears about changes at once, the owner
/// chose the last: *"tutsun ama anında haber alsın."*
/// </para>
/// <para>
/// <b>So there is no clock in here, and that is the design rather than an omission.</b> An answer
/// is served only while <see cref="Listening"/> says a connection is subscribed to the store's
/// announcements (migration 44). While nothing listens — before the subscription is up, after it
/// drops, on a server whose listener failed — every request reads the store, which is what every
/// request did before this existed. The cache can be absent; it cannot be stale.
/// </para>
/// <para>
/// <b>A generation, because a read and an announcement can cross.</b> A request that reads the
/// store, is overtaken by a change and its announcement, and then offers what it read would put the
/// old answer back after the invalidation had cleared it — and it would then be served until the
/// next change, which may be never. Each read carries the generation it started under, an
/// announcement moves the generation, and an answer is kept only while its generation is current.
/// </para>
/// <para>
/// <b>Anonymous only.</b> One principal is the whole of the measured cost — an authenticated caller
/// already pays a session lookup the cache would not remove — and it is the principal whose grants
/// change least: nothing in the API edits them.
/// </para>
/// </remarks>
public sealed class AnonymousGrants
{
    private long _generation;
    private volatile bool _listening;
    private volatile Held? _held;

    /// <summary>The anonymous principal's grants, as <c>GrantsOfAsync</c> returns them.</summary>
    /// <param name="UserType">Its user type.</param>
    /// <param name="Roles">Its roles.</param>
    /// <param name="Groups">The groups it is in.</param>
    /// <param name="EditableGroups">The groups whose items it may edit.</param>
    public sealed record Snapshot(
        string UserType,
        IReadOnlyList<string> Roles,
        IReadOnlyList<Guid> Groups,
        IReadOnlyList<Guid> EditableGroups);

    private sealed record Held(long Generation, Snapshot Value);

    /// <summary>Whether a connection is subscribed to the store's announcements.</summary>
    public bool Listening => _listening;

    /// <summary>The generation a read starts under; pass it back to <see cref="Offer"/>.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>The held answer, when there is one that may be served.</summary>
    /// <param name="snapshot">The answer, or null.</param>
    /// <returns>Whether it may be served.</returns>
    public bool TryGet(out Snapshot? snapshot)
    {
        Held? held = _held;

        if (_listening && held is not null && held.Generation == Interlocked.Read(ref _generation))
        {
            snapshot = held.Value;
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>Offers an answer read from the store.</summary>
    /// <param name="readUnder">The <see cref="Generation"/> taken before the read began.</param>
    /// <param name="snapshot">What the store said.</param>
    /// <remarks>
    /// Kept only while somebody is listening and nothing has been announced since the read began;
    /// otherwise dropped, and the next request reads again.
    /// </remarks>
    public void Offer(long readUnder, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_listening && Interlocked.Read(ref _generation) == readUnder)
        {
            _held = new Held(readUnder, snapshot);
        }
    }

    /// <summary>Something that decides the anonymous caller's grants has changed.</summary>
    public void Changed()
    {
        Interlocked.Increment(ref _generation);
        _held = null;
    }

    /// <summary>The subscription came up or went down.</summary>
    /// <param name="listening">Which.</param>
    /// <remarks>
    /// <b>Both directions invalidate.</b> Going down, because nothing will say when the answer
    /// stops being true. Coming up, because anything read before the subscription existed may
    /// already be out of date, and nothing announced it to anybody.
    /// </remarks>
    public void Subscribed(bool listening)
    {
        _listening = listening;
        Changed();
    }
}
