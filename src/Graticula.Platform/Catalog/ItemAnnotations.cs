using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>One content category: a node in the organisation's tree — ADR-202.</summary>
/// <param name="Id">The category.</param>
/// <param name="ParentId">Its parent, or null at the top.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Items">How many items are in it.</param>
public sealed record ItemCategory(Guid Id, Guid? ParentId, string Name, int Items);

/// <summary>What is said about every item at once, for the portal's documents — ADR-202.</summary>
/// <param name="Licenses">Each item's terms of use, where it has some.</param>
/// <param name="Categories">Each item's categories, by id.</param>
/// <param name="Tree">The organisation's category tree.</param>
public sealed record ItemAnnotationSnapshot(
    IReadOnlyDictionary<Guid, string> Licenses,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Categories,
    IReadOnlyList<ItemCategory> Tree);

/// <summary>
/// Terms of use, content categories and favourites — what ArcGIS's portal keeps beside an item and this one did not
/// (the ArcGIS veteran's review, V-96) — ADR-202.
/// </summary>
public interface IItemAnnotationStore
{
    /// <summary>Every item's terms and categories, and the tree.</summary>
    Task<ItemAnnotationSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>An item's owner, or null when there is no such item; the owner itself may be null.</summary>
    Task<(Guid? Owner, string Kind)?> FindAsync(Guid item, CancellationToken cancellationToken);

    /// <summary>Replaces an item's terms of use; null clears them.</summary>
    Task<bool> SetLicenseAsync(Guid item, string? license, CancellationToken cancellationToken);

    /// <summary>Replaces an item's categories; false when the item or a category does not exist.</summary>
    Task<bool> SetCategoriesAsync(Guid item, IReadOnlyList<Guid> categories, CancellationToken cancellationToken);

    /// <summary>Makes a category under a parent, or at the top; null when the parent is missing or the name taken there.</summary>
    Task<ItemCategory?> CreateCategoryAsync(Guid? parent, string name, CancellationToken cancellationToken);

    /// <summary>Renames a category; false when missing or the name is taken beside it.</summary>
    Task<bool> RenameCategoryAsync(Guid id, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a category, its children and their assignments; false when missing.</summary>
    Task<bool> DeleteCategoryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Marks or unmarks an item as one of a principal's favourites; false when the item does not exist.</summary>
    Task<bool> SetFavoriteAsync(Guid principal, Guid item, bool favorite, CancellationToken cancellationToken);

    /// <summary>A principal's favourites, newest first, with each item's kind.</summary>
    Task<IReadOnlyList<(Guid Item, string Kind, DateTimeOffset Added)>> FavoritesAsync(
        Guid principal, CancellationToken cancellationToken);
}
