using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>A hosted table set aside with its item: where it was, and what it is called in the recycle schema.</summary>
/// <param name="Schema">The schema it came from.</param>
/// <param name="Original">Its name there.</param>
/// <param name="Recycled">Its name in <see cref="RecycleBins.Schema"/>.</param>
/// <param name="Kind">What it is — <c>table</c>, <c>view</c> or <c>function</c> — so it is moved back as what it is.</param>
public sealed record RecycledRelation(string Schema, string Original, string Recycled, string Kind);

/// <summary>A deleted item that can come back — ADR-203.</summary>
/// <param name="Id">The entry.</param>
/// <param name="ItemId">The item's own id, which it keeps when restored.</param>
/// <param name="Kind">What the item was: <c>service</c>, <c>web map</c>, <c>document</c>.</param>
/// <param name="Title">What it was called, for the list.</param>
/// <param name="Owner">Who owned it, or null.</param>
/// <param name="DeletedBy">Who deleted it, or null.</param>
/// <param name="DeletedAt">When.</param>
/// <param name="ExpiresAt">When it is deleted for good.</param>
/// <param name="Relations">The hosted tables, views and functions set aside with it.</param>
/// <param name="Rows">How many catalogue rows it holds.</param>
public sealed record RecycleEntry(
    Guid Id,
    Guid ItemId,
    string Kind,
    string Title,
    Guid? Owner,
    Guid? DeletedBy,
    DateTimeOffset DeletedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RecycledRelation> Relations,
    int Rows);

/// <summary>Constants of the recycle bin — ADR-203.</summary>
public static class RecycleBins
{
    /// <summary>The datastore schema a deleted item's hosted tables wait in.</summary>
    public const string Schema = "graticula_recycle";

    /// <summary>How long a deleted item waits before it is deleted for good, unless a deployment says otherwise.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);
}

/// <summary>
/// Deleted items, kept whole with their ids until they expire — ADR-203, answering Q-180 (owner decision 2026-10-18:
/// *"Satırları taşı"*).
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// Takes every catalogue row the root's delete would cascade to — found through the store's own foreign keys — and
    /// the rows it would set to null, keeps them under a new entry, and deletes the root, in one transaction.
    /// </summary>
    /// <param name="rootTable">The table the delete starts at: <c>service</c>, <c>web_map</c> or <c>item</c>.</param>
    /// <param name="rootId">The root row's id.</param>
    /// <param name="itemId">The item it is.</param>
    /// <param name="kind">The item's kind.</param>
    /// <param name="title">The item's title.</param>
    /// <param name="owner">Its owner.</param>
    /// <param name="deletedBy">Who deleted it.</param>
    /// <param name="expiresAt">When it is deleted for good.</param>
    /// <param name="relations">The hosted tables already set aside, recorded with it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entry, or null when the root row does not exist.</returns>
    Task<Guid?> RecycleAsync(
        string rootTable, object rootId, Guid itemId, string kind, string title, Guid? owner, Guid? deletedBy,
        DateTimeOffset expiresAt, IReadOnlyList<RecycledRelation> relations, CancellationToken cancellationToken);

    /// <summary>The entries, newest first.</summary>
    Task<IReadOnlyList<RecycleEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One entry, or null.</summary>
    Task<RecycleEntry?> FindAsync(Guid entry, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the rows back, parents first, with their ids, and removes the entry, in one transaction; null on success, or
    /// why not — a name taken since, a folder deleted since — with nothing changed.
    /// </summary>
    Task<string?> RestoreRowsAsync(Guid entry, CancellationToken cancellationToken);

    /// <summary>Forgets an entry and its rows; false when there is none.</summary>
    Task<bool> RemoveAsync(Guid entry, CancellationToken cancellationToken);
}
