namespace Graticula.Platform.Catalog;

/// <summary>What changing one item's owner came to — Portal's *Change owner* for one item (2026-10-01).</summary>
public enum OwnerChange
{
    /// <summary>The item now belongs to the member named.</summary>
    Changed,

    /// <summary>No such item.</summary>
    NoItem,

    /// <summary>No member of that name to receive it.</summary>
    NoMember,
}
