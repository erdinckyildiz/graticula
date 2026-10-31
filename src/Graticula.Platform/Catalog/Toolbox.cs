using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

/// <summary>
/// A published Python toolbox — ADR-223 §5.7: an item whose source this server runs, fenced, one tool at a time, served
/// as a GPServer at <c>/rest/services/Tools/{name}/GPServer</c>.
/// </summary>
/// <param name="Id">The item id, 32 hex digits.</param>
/// <param name="Name">The name its service is addressed by, unique in the organisation.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Owner">Who published it.</param>
/// <param name="OwnerName">Their name.</param>
/// <param name="Sharing">Who may run it.</param>
/// <param name="FileName">The file it was published from.</param>
/// <param name="Tools">Its tools and their parameters as the fenced describe run read them, a JSON array.</param>
/// <param name="Created">When.</param>
/// <param name="SharedWith">The groups it is shared with.</param>
public sealed record Toolbox(
    string Id,
    string Name,
    string Title,
    Guid Owner,
    string OwnerName,
    SharingScope Sharing,
    string FileName,
    string Tools,
    DateTimeOffset Created,
    IReadOnlyList<Guid>? SharedWith = null);

/// <summary>Published toolboxes — ADR-223.</summary>
public interface IToolboxStore
{
    /// <summary>Every toolbox the caller's organisation holds; who may see each is the caller's to decide.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The toolboxes.</returns>
    Task<IReadOnlyList<Toolbox>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One toolbox by its service name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>It, or null.</returns>
    Task<Toolbox?> FindByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>One toolbox's source, read only to run it.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The source, or null.</returns>
    Task<string?> SourceAsync(string id, CancellationToken cancellationToken);

    /// <summary>Publishes a toolbox: its item, private to start, and its source and tools beside it.</summary>
    /// <param name="name">Its service name, already checked and free.</param>
    /// <param name="title">Its title.</param>
    /// <param name="owner">Who publishes it.</param>
    /// <param name="fileName">The file's name.</param>
    /// <param name="source">The Python source.</param>
    /// <param name="tools">What the describe run read, a JSON array.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The toolbox.</returns>
    Task<Toolbox> CreateAsync(
        string name, string title, Guid owner, string fileName, string source, string tools, CancellationToken cancellationToken);

    /// <summary>Changes who may run it.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    Task<bool> ShareAsync(string id, SharingScope sharing, CancellationToken cancellationToken);
}
