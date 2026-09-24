using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// An archive's domains, stored once as shared domains — ADR-087.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point of sharing is this path.</b> A geodatabase's domains are workspace objects its classes point at,
/// so an archive whose fifty classes use one <c>Material</c> now lands one <c>Material</c> and fifty fields
/// pointing at it — and a second archive using the same <c>Material</c>, word for word, points at the same one.
/// </para>
/// <para>
/// <b>A name already taken by other values is numbered, not refused</b>, because nobody typed it: an import that
/// failed over a name would lose a layer for a word. <c>Material (2)</c> is what a client then sees, and the job
/// report says so. The admin surface refuses the same case instead, where a person did type it.
/// </para>
/// </remarks>
internal static class SharedDomainImport
{
    /// <summary>The most numbered names tried before giving up — far past any real archive.</summary>
    private const int MostTries = 1_000;

    /// <summary>The overrides with each domain replaced by the shared domain it lands as.</summary>
    /// <param name="overrides">The overrides an import made.</param>
    /// <param name="store">The shared domains.</param>
    /// <param name="owner">Who imported the archive, who owns what it creates.</param>
    /// <param name="renamed">Where a sentence is added for each domain stored under another name.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The overrides, every domain carrying its id.</returns>
    public static async Task<List<FieldOverride>> ShareAsync(
        IEnumerable<FieldOverride> overrides,
        IFieldDomainStore store,
        Guid? owner,
        List<string> renamed,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(renamed);

        Dictionary<string, FieldDomain> landed = new(StringComparer.OrdinalIgnoreCase);

        async Task<FieldDomain> One(FieldDomain domain)
        {
            if (landed.TryGetValue(domain.Name, out FieldDomain? already) && already.SameAs(domain.Named(already.Name)))
            {
                return already;
            }

            for (int n = 1; n <= MostTries; n++)
            {
                string name = n == 1 ? domain.Name : string.Create(CultureInfo.InvariantCulture, $"{domain.Name} ({n})");

                SharedDomain? found = await store.FindByNameAsync(name, cancellation).ConfigureAwait(false)
                    ?? await store.CreateAsync(domain.Named(name), owner, cancellation).ConfigureAwait(false)
                    ?? await store.FindByNameAsync(name, cancellation).ConfigureAwait(false);

                if (found is not null && found.Domain.SameAs(domain.Named(found.Domain.Name)))
                {
                    if (n > 1)
                    {
                        renamed.Add($"the domain '{domain.Name}' is stored as '{found.Domain.Name}': a shared domain "
                            + "of that name already says something else.");
                    }

                    landed[domain.Name] = found.Domain;
                    return found.Domain;
                }
            }

            throw new InvalidOperationException($"No free name for the domain '{domain.Name}' after {MostTries} tries.");
        }

        List<FieldOverride> shared = [];

        foreach (FieldOverride says in overrides)
        {
            FieldDomain? domain = says.Domain is { } own ? await One(own).ConfigureAwait(false) : null;
            LayerSubtypes? subtypes = says.Subtypes;

            if (subtypes is not null)
            {
                List<Subtype> types = new(subtypes.Types.Count);

                foreach (Subtype type in subtypes.Types)
                {
                    Dictionary<string, FieldDomain> domains = new(StringComparer.Ordinal);

                    foreach ((string column, FieldDomain given) in type.Domains)
                    {
                        domains[column] = await One(given).ConfigureAwait(false);
                    }

                    types.Add(type with { Domains = domains });
                }

                subtypes = subtypes with { Types = types };
            }

            shared.Add(says with { Domain = domain, Subtypes = subtypes });
        }

        return shared;
    }
}
