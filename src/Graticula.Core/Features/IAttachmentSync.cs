using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>What happened to an attachment, net, over a window of time.</summary>
public enum AttachmentChangeKind
{
    /// <summary>It did not exist at the start of the window and does at the end.</summary>
    Insert,

    /// <summary>It existed at both ends and was written in between.</summary>
    Update,

    /// <summary>It existed at the start of the window and does not at the end.</summary>
    Delete,
}

/// <summary>One attachment's net change.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="AttachmentId">The attachment's id.</param>
/// <param name="FeatureId">The feature it belongs to.</param>
/// <param name="GlobalId">The GlobalID it was stored with, or null when it was stored without one (ADR-290).</param>
public readonly record struct AttachmentChange(AttachmentChangeKind Kind, int AttachmentId, long FeatureId, Guid? GlobalId);

/// <summary>The attachment changes a store can vouch for in a window.</summary>
/// <param name="Vouched">
/// False when the record of changes begins after the window did, so nothing can honestly be said about it — the caller
/// then sends no attachment changes rather than invent some.
/// </param>
/// <param name="Changes">The net changes, one per attachment.</param>
public sealed record AttachmentChanges(bool Vouched, IReadOnlyList<AttachmentChange> Changes)
{
    /// <summary>A record that cannot vouch for the window.</summary>
    public static AttachmentChanges Unvouched { get; } = new(false, []);

    /// <summary>A record that vouches for the window and holds nothing in it.</summary>
    public static AttachmentChanges None { get; } = new(true, []);
}

/// <summary>One row of an attachment table's change log.</summary>
/// <param name="Kind"><c>insert</c>, <c>update</c> or <c>delete</c>.</param>
/// <param name="AttachmentId">The attachment.</param>
/// <param name="FeatureId">Its feature.</param>
/// <param name="GlobalId">Its GlobalID, or null.</param>
public readonly record struct AttachmentLogEntry(string Kind, int AttachmentId, long FeatureId, Guid? GlobalId);

/// <summary>
/// Turns a change log into net changes. Pure, so it is tested without a database.
/// </summary>
public static class AttachmentChangeLog
{
    /// <summary>The net change of each attachment the entries name.</summary>
    /// <param name="entries">The log's entries in the window, oldest first.</param>
    /// <returns>One change per attachment that is different at the end of the window from the start; ordered by id.</returns>
    /// <remarks>
    /// <b>The first entry says whether it existed at the start</b> (an <c>insert</c> means it did not) and <b>the last says
    /// whether it exists at the end</b> (a <c>delete</c> means it does not). An attachment added and removed inside the
    /// window never reached anyone, and is no change; one written twice is one update. Nothing is guessed from times.
    /// </remarks>
    public static IReadOnlyList<AttachmentChange> Net(IEnumerable<AttachmentLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<AttachmentChange> net = [];

        foreach (IGrouping<int, AttachmentLogEntry> one in entries.GroupBy(e => e.AttachmentId).OrderBy(g => g.Key))
        {
            AttachmentLogEntry first = one.First();
            AttachmentLogEntry last = one.Last();
            bool existedAtStart = first.Kind != "insert";
            bool existsAtEnd = last.Kind != "delete";
            Guid? globalId = one.Select(e => e.GlobalId).LastOrDefault(g => g is not null);

            if (!existedAtStart && existsAtEnd)
            {
                net.Add(new AttachmentChange(AttachmentChangeKind.Insert, one.Key, last.FeatureId, globalId));
            }
            else if (existedAtStart && existsAtEnd)
            {
                net.Add(new AttachmentChange(AttachmentChangeKind.Update, one.Key, last.FeatureId, globalId));
            }
            else if (existedAtStart && !existsAtEnd)
            {
                net.Add(new AttachmentChange(AttachmentChangeKind.Delete, one.Key, last.FeatureId, globalId));
            }
        }

        return net;
    }
}

/// <summary>
/// What an attachment store offers a replica's synchronisation beyond <see cref="IAttachmentStore"/> — ADR-290:
/// attachments that keep the GlobalID a device gave them, and a record of changes to read a download from.
/// </summary>
/// <remarks>
/// <b>Optional, and a store that does not implement it is not wrong.</b> Without it a replica calls an attachment by a
/// GlobalID derived from its id and receives no attachment changes after it was created (D-399 and the providers that keep
/// no change record). The host asks <c>store is IAttachmentSync</c>.
/// </remarks>
public interface IAttachmentSync
{
    /// <summary>
    /// Makes the store able to keep GlobalIDs and a change record, when it is not yet — before a replica is created, so the
    /// record begins before the replica's first generation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task PrepareAsync(CancellationToken cancellationToken);

    /// <summary>Stores an attachment under the GlobalID its device gave it.</summary>
    /// <param name="featureId">The feature it belongs to.</param>
    /// <param name="name">The uploader's name for it.</param>
    /// <param name="contentType">What the bytes were determined to be.</param>
    /// <param name="declaredContentType">What the uploader claimed.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="globalId">The GlobalID to keep.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The attachment's id — the existing one when this GlobalID is already stored, so the same upload twice adds nothing.</returns>
    Task<int> AddAsync(
        long featureId,
        string name,
        string contentType,
        string? declaredContentType,
        Stream content,
        Guid globalId,
        CancellationToken cancellationToken);

    /// <summary>The attachments stored under these GlobalIDs.</summary>
    /// <param name="globalIds">The GlobalIDs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each GlobalID that is stored, and the attachment's id.</returns>
    Task<IReadOnlyDictionary<Guid, int>> IdsOfAsync(IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken);

    /// <summary>Which of these attachments exist, and the GlobalID each was stored with.</summary>
    /// <param name="attachmentIds">The ids.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each id that exists; the value is null for one stored without a GlobalID.</returns>
    Task<IReadOnlyDictionary<int, Guid?>> GlobalIdsAsync(IReadOnlyCollection<int> attachmentIds, CancellationToken cancellationToken);

    /// <summary>What changed in the attachments between two moments, from the store's own record.</summary>
    /// <param name="after">The start, exclusive: a replica's generation.</param>
    /// <param name="until">The end, inclusive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The net changes, or <see cref="AttachmentChanges.Unvouched"/>.</returns>
    Task<AttachmentChanges> ChangesAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken);
}
