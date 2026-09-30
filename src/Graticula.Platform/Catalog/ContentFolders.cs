using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>
/// One of a member's content folders — ADR-114. Not a service folder: it is not in any URL, and moving an item into it
/// changes nothing a client holds.
/// </summary>
/// <param name="Id">Its id, which is what an item names and what survives a rename.</param>
/// <param name="Owner">Whose folder it is.</param>
/// <param name="Title">What it is called, unique for its owner without regard to case.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Items">How many items are in it.</param>
public sealed record ContentFolder(Guid Id, Guid Owner, string Title, DateTimeOffset Created, int Items = 0);

/// <summary>What a folder write came to.</summary>
public enum ContentFolderWrite
{
    /// <summary>Written.</summary>
    Done,

    /// <summary>There is no such folder.</summary>
    Missing,

    /// <summary>The owner already has a folder of that title.</summary>
    Taken,

    /// <summary>It holds items, and a folder is deleted only empty.</summary>
    NotEmpty,
}

/// <summary>A member's content folders, and which one an item is in — ADR-114.</summary>
public interface IContentFolderStore
{
    /// <summary>A member's folders, by title, each with its item count.</summary>
    /// <param name="owner">The member.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The folders.</returns>
    Task<IReadOnlyList<ContentFolder>> ListAsync(Guid owner, CancellationToken cancellationToken);

    /// <summary>One folder, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The folder, or null.</returns>
    Task<ContentFolder?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Makes a folder, or returns null when the owner already has one of that title.</summary>
    /// <param name="owner">Whose.</param>
    /// <param name="title">Its title, already trimmed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The folder, or null.</returns>
    Task<ContentFolder?> CreateAsync(Guid owner, string title, CancellationToken cancellationToken);

    /// <summary>Renames a folder.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">The new title, already trimmed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Done, Missing or Taken.</returns>
    Task<ContentFolderWrite> RenameAsync(Guid id, string title, CancellationToken cancellationToken);

    /// <summary>Deletes a folder that holds nothing.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Done, Missing or NotEmpty.</returns>
    Task<ContentFolderWrite> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Puts a service in a folder, or at the root with null.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="folder">The folder, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the service exists.</returns>
    Task<bool> MoveServiceAsync(Guid serviceId, Guid? folder, CancellationToken cancellationToken);

    /// <summary>Puts a web map in a folder, or at the root with null.</summary>
    /// <param name="mapId">The map.</param>
    /// <param name="folder">The folder, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the map exists.</returns>
    Task<bool> MoveMapAsync(string mapId, Guid? folder, CancellationToken cancellationToken);
}
