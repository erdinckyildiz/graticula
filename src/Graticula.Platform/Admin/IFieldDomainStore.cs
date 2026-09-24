using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;

namespace Graticula.Platform.Admin;

/// <summary>
/// The server's shared domains — ADR-087: named lists and ranges that fields on many layers point at.
/// </summary>
/// <remarks>
/// <para>
/// <b>By owner decision, 2026-09-23 and 2026-09-24.</b> A domain is shared, as a geodatabase shares it; its
/// owner and administrators edit it; one in use is not deleted; its name is unique across the server.
/// </para>
/// <para>
/// <b>The store keeps and counts; it does not judge.</b> Whether a domain fits the columns that use it needs
/// those columns' types, which the host has and this does not, for the reason
/// <see cref="IServerSettingStore"/> gives about a page size.
/// </para>
/// </remarks>
public interface IFieldDomainStore
{
    /// <summary>Every shared domain, by name, with where each is used.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The domains.</returns>
    Task<IReadOnlyList<SharedDomain>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One shared domain, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The domain.</returns>
    Task<SharedDomain?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The shared domain with this name, compared without case, or null.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The domain.</returns>
    Task<SharedDomain?> FindByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stores a new shared domain, or returns null when its name is taken.</summary>
    /// <param name="domain">The domain; its id, if any, is ignored.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored domain, or null.</returns>
    Task<SharedDomain?> CreateAsync(FieldDomain domain, Guid? owner, CancellationToken cancellationToken);

    /// <summary>Replaces what a shared domain says, name included; false when the name is another's.</summary>
    /// <param name="id">Which one.</param>
    /// <param name="domain">What it now says.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was stored.</returns>
    Task<bool> UpdateAsync(Guid id, FieldDomain domain, CancellationToken cancellationToken);

    /// <summary>Removes a shared domain that nothing uses.</summary>
    /// <param name="id">Which one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// Where it is used when it was not removed for that reason; empty when it was removed or did not exist.
    /// </returns>
    Task<IReadOnlyList<DomainUse>> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>A shared domain as stored.</summary>
/// <param name="Domain">What it says, carrying its id.</param>
/// <param name="Owner">Who owns it, or null when nobody does any more.</param>
/// <param name="OwnerName">The owner's name, for a screen.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Uses">The fields that point at it.</param>
public sealed record SharedDomain(
    FieldDomain Domain, Guid? Owner, string? OwnerName, DateTimeOffset UpdatedAt, IReadOnlyList<DomainUse> Uses)
{
    /// <summary>Its id.</summary>
    public Guid Id => Domain.Id ?? Guid.Empty;
}

/// <summary>A field that points at a shared domain.</summary>
/// <param name="LayerId">The layer.</param>
/// <param name="Layer">The layer's name.</param>
/// <param name="Column">The column.</param>
/// <param name="Subtype">The subtype whose domain it is for that column, or null for the column's own.</param>
public sealed record DomainUse(Guid LayerId, string Layer, string Column, long? Subtype);
