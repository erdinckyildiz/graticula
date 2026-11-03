using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>One file an item keeps beside itself, without its bytes (ADR-264).</summary>
/// <param name="Path">Its path within the item: a prefix, a slash, a name — <c>config/config.json</c>.</param>
/// <param name="MediaType">What it is.</param>
/// <param name="Size">How many bytes.</param>
/// <param name="Access">Who may read it: <c>inherit</c>, as the item; <c>private</c>, its owner and administrators.</param>
/// <param name="Created">When it was first added.</param>
/// <param name="Modified">When it was last written.</param>
public sealed record ItemResource(string Path, string MediaType, long Size, string Access, DateTimeOffset Created, DateTimeOffset Modified);

/// <summary>What a write of a resource did.</summary>
public enum ItemResourceWrite
{
    /// <summary>It was added.</summary>
    Added,

    /// <summary>It replaced the file at its path.</summary>
    Replaced,

    /// <summary>Nothing: an add found a file at the path, and an add does not replace.</summary>
    AlreadyThere,

    /// <summary>Nothing: an update found no file at the path to replace.</summary>
    NotThere,

    /// <summary>Nothing: the item's resources would pass what one item may hold.</summary>
    TooLarge,
}

/// <summary>
/// The files an item keeps beside itself — an Experience Builder app's configuration, a Dashboard's images — as Portal's
/// item resources (ADR-264). Any kind of item may have them; they go with the item.
/// </summary>
public interface IItemResourceStore
{
    /// <summary>A page of an item's resources, by path.</summary>
    /// <param name="item">The item.</param>
    /// <param name="start">How many to skip.</param>
    /// <param name="count">How many at most.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page and how many there are.</returns>
    Task<(IReadOnlyList<ItemResource> Page, int Total)> ListAsync(Guid item, int start, int count, CancellationToken cancellationToken);

    /// <summary>One resource and its bytes, or null.</summary>
    /// <param name="item">The item.</param>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The resource and its content.</returns>
    Task<(ItemResource Resource, byte[] Content)?> FindAsync(Guid item, string path, CancellationToken cancellationToken);

    /// <summary>Adds a resource, or replaces one, within what one item may hold in all.</summary>
    /// <param name="item">The item.</param>
    /// <param name="path">Its path.</param>
    /// <param name="mediaType">What it is.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="access"><c>inherit</c> or <c>private</c>.</param>
    /// <param name="replace">False to add only where nothing is; true to replace only where something is.</param>
    /// <param name="limit">The most bytes the item's resources may hold together after the write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was done.</returns>
    Task<ItemResourceWrite> WriteAsync(
        Guid item, string path, string mediaType, byte[] content, string access, bool replace, long limit, CancellationToken cancellationToken);

    /// <summary>Removes one resource, or every one when <paramref name="path"/> is null.</summary>
    /// <param name="item">The item.</param>
    /// <param name="path">Its path, or null for all.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many were removed.</returns>
    Task<int> RemoveAsync(Guid item, string? path, CancellationToken cancellationToken);
}
