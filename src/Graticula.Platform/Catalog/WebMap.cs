using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

/// <summary>
/// A saved web map — ADR-079: an ArcGIS Web Map document, with an owner and a sharing scope.
/// </summary>
/// <remarks>
/// <b>The document is carried as text and never modelled.</b> The server's only rules about it are
/// that it is a JSON object and not larger than <see cref="WebMaps.MaximumDocumentBytes"/>; what a
/// layer or a basemap is, the viewer reads. A type here would be a second description of a public
/// format, and the first field ArcGIS Pro wrote that the type did not name would be lost on save.
/// </remarks>
/// <param name="Id">32 lower-case hexadecimal characters, which is an ArcGIS item id's shape.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Snippet">A line saying what it is for, or null.</param>
/// <param name="Owner">Who owns it.</param>
/// <param name="OwnerName">The owner's account name.</param>
/// <param name="Sharing">Private, organisation or public; never group (ADR-079 condition 4).</param>
/// <param name="Document">The Web Map JSON, or null in a listing, which does not read it.</param>
/// <param name="Created">When it was first saved.</param>
/// <param name="Modified">When it was last saved.</param>
public sealed record WebMap(
    string Id,
    string Title,
    string? Snippet,
    Guid Owner,
    string OwnerName,
    SharingScope Sharing,
    string? Document,
    DateTimeOffset Created,
    DateTimeOffset Modified);

/// <summary>The rules a saved web map is held to, in one place for the store and the endpoints.</summary>
public static class WebMaps
{
    /// <summary>The largest document accepted: 1 MB of UTF-8 (ADR-079 §5.2).</summary>
    /// <remarks>
    /// <b>A bound, not a measurement of real maps.</b> A map of a dozen layers with filters is a few
    /// kilobytes; one saved by Pro with pop-up definitions for every layer can be a few hundred. What
    /// the bound prevents is the catalogue becoming somebody's file store.
    /// </remarks>
    public const int MaximumDocumentBytes = 1024 * 1024;

    /// <summary>The longest title accepted.</summary>
    public const int MaximumTitleLength = 250;

    /// <summary>The longest snippet accepted — ArcGIS's own limit for an item's summary.</summary>
    public const int MaximumSnippetLength = 2048;

    /// <summary>Whether a string is shaped like a web map's id.</summary>
    /// <param name="id">The candidate.</param>
    /// <returns>True for 32 lower-case hexadecimal characters.</returns>
    public static bool IsId(string? id)
    {
        if (id is not { Length: 32 })
        {
            return false;
        }

        foreach (char c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a scope is one a web map may have.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>False for <see cref="SharingScope.Group"/>.</returns>
    public static bool Allows(SharingScope scope) =>
        scope is SharingScope.Private or SharingScope.Organization or SharingScope.Public;
}

/// <summary>
/// Where saved web maps are kept — ADR-079, migration 52.
/// </summary>
/// <remarks>
/// <b>No authorisation here.</b> The store answers what exists; who may read or change it is
/// <see cref="LayerAccess"/>'s decision, made by the caller that knows who is asking — the same split
/// every other catalogue in this product keeps.
/// </remarks>
public interface IWebMapStore
{
    /// <summary>Every saved map, without its document, newest change first.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The maps.</returns>
    Task<IReadOnlyList<WebMap>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One map with its document, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The map.</returns>
    Task<WebMap?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Saves a new map under a fresh id.</summary>
    /// <param name="title">Its title.</param>
    /// <param name="snippet">Its snippet, or null.</param>
    /// <param name="owner">Its owner.</param>
    /// <param name="sharing">Its scope.</param>
    /// <param name="document">The Web Map JSON, already validated as an object.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The map as stored, with its document.</returns>
    Task<WebMap> CreateAsync(
        string title,
        string? snippet,
        Guid owner,
        SharingScope sharing,
        string document,
        CancellationToken cancellationToken);

    /// <summary>Replaces a map's title, snippet, scope and document; null when there is no such map.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">The new title.</param>
    /// <param name="snippet">The new snippet, or null.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="document">The new document.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The map as stored.</returns>
    Task<WebMap?> UpdateAsync(
        string id,
        string title,
        string? snippet,
        SharingScope sharing,
        string document,
        CancellationToken cancellationToken);

    /// <summary>Removes a map; false when there was none.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether a row went.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
