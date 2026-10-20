using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>Related rows for one origin feature.</summary>
/// <param name="ObjectId">Which feature they belong to.</param>
/// <param name="Records">Its related rows, as attribute maps.</param>
public sealed record RelatedGroup(long ObjectId, IReadOnlyList<IReadOnlyDictionary<string, object?>> Records);

/// <summary>
/// Follows a declared relationship — ADR-013 — in the database both layers are in, in one query for every object id
/// asked for.
/// </summary>
/// <remarks>
/// <b>An interface since 2026-10-20</b>, when Oracle layers were given relationships (ADR-205): until then the one
/// implementation was PostGIS's and the faces named it.
/// </remarks>
public interface IRelatedRecords
{
    /// <summary>Reads the related rows for a set of origin features.</summary>
    /// <param name="originKey">The join column on the origin side.</param>
    /// <param name="relatedKey">The join column on the related side.</param>
    /// <param name="objectIds">Which origin features.</param>
    /// <param name="fields">The related layer's columns, as the whitelist.</param>
    /// <param name="limit">The most rows to return in total.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One group per origin feature that had any.</returns>
    Task<IReadOnlyList<RelatedGroup>> QueryAsync(
        string originKey,
        string relatedKey,
        IReadOnlyList<long> objectIds,
        IReadOnlyList<FieldDescription> fields,
        int limit,
        CancellationToken cancellationToken);
}
