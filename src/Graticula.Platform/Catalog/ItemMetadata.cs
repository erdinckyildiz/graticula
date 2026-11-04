using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>An item's metadata document as kept: its bytes and when it was last written (ADR-267).</summary>
/// <param name="Content">The XML exactly as it was sent.</param>
/// <param name="Modified">When it was last written.</param>
public sealed record ItemMetadataDocument(byte[] Content, DateTimeOffset Modified);

/// <summary>
/// The metadata document an item carries — Portal's <c>info/metadata/metadata.xml</c>, in ArcGIS, ISO 19139 or FGDC form
/// (ADR-267). One per item at most; it goes with the item, into the recycle bin and back.
/// </summary>
public interface IItemMetadataStore
{
    /// <summary>The item's metadata, or null when it has none.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document, or null.</returns>
    Task<ItemMetadataDocument?> FindAsync(Guid item, CancellationToken cancellationToken);

    /// <summary>Which of these items carry metadata.</summary>
    /// <param name="items">The items.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ones that do.</returns>
    Task<IReadOnlySet<Guid>> WhichHaveAsync(IReadOnlyCollection<Guid> items, CancellationToken cancellationToken);

    /// <summary>Writes the item's metadata, replacing what it had.</summary>
    /// <param name="item">The item.</param>
    /// <param name="content">The XML, already checked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task WriteAsync(Guid item, byte[] content, CancellationToken cancellationToken);

    /// <summary>Removes the item's metadata.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was any.</returns>
    Task<bool> RemoveAsync(Guid item, CancellationToken cancellationToken);
}

/// <summary>One comment on an item, as Portal's <c>comments</c> lists it (ADR-267).</summary>
/// <param name="Id">Its id.</param>
/// <param name="Item">The item it is on.</param>
/// <param name="Author">The member who wrote it, by id, or null once nothing names them.</param>
/// <param name="AuthorName">Their username when they wrote it.</param>
/// <param name="Text">What they wrote, as they wrote it.</param>
/// <param name="Created">When.</param>
public sealed record ItemComment(Guid Id, Guid Item, Guid? Author, string AuthorName, string Text, DateTimeOffset Created);

/// <summary>The comments members leave on items — Portal's <c>addComment</c>, <c>comments</c> and <c>delete</c> (ADR-267).</summary>
public interface IItemCommentStore
{
    /// <summary>A page of an item's comments, oldest first, and how many there are.</summary>
    /// <param name="item">The item.</param>
    /// <param name="start">How many to skip.</param>
    /// <param name="count">How many at most.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page and the total.</returns>
    Task<(IReadOnlyList<ItemComment> Page, int Total)> ListAsync(Guid item, int start, int count, CancellationToken cancellationToken);

    /// <summary>One comment on an item, or null.</summary>
    /// <param name="item">The item.</param>
    /// <param name="comment">The comment's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The comment, or null.</returns>
    Task<ItemComment?> FindAsync(Guid item, Guid comment, CancellationToken cancellationToken);

    /// <summary>How many comments each of these items has; an item with none is absent.</summary>
    /// <param name="items">The items.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<IReadOnlyDictionary<Guid, int>> CountAsync(IReadOnlyCollection<Guid> items, CancellationToken cancellationToken);

    /// <summary>Adds a comment, unless the item already holds as many as one item may.</summary>
    /// <param name="item">The item.</param>
    /// <param name="author">Who wrote it.</param>
    /// <param name="authorName">Their username.</param>
    /// <param name="text">What they wrote.</param>
    /// <param name="limit">The most comments one item may hold.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The comment, or null when the item is full.</returns>
    Task<ItemComment?> AddAsync(Guid item, Guid author, string authorName, string text, int limit, CancellationToken cancellationToken);

    /// <summary>Changes a comment's text.</summary>
    /// <param name="item">The item.</param>
    /// <param name="comment">The comment's id.</param>
    /// <param name="text">The new text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was there.</returns>
    Task<bool> UpdateAsync(Guid item, Guid comment, string text, CancellationToken cancellationToken);

    /// <summary>Removes one comment.</summary>
    /// <param name="item">The item.</param>
    /// <param name="comment">The comment's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was there.</returns>
    Task<bool> RemoveAsync(Guid item, Guid comment, CancellationToken cancellationToken);
}
