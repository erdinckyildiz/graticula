using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// A feature source that answers one query with every page of it, in order — for a file that holds a whole layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>For the Esri JSON export (ADR-106 §5.6), and only for it.</b> The query writer writes one query's answer, and a
/// query is at most <see cref="FeatureQuery.MaximumLimit"/> rows; a layer is more. Rather than teach the writer to
/// page, it is handed a source whose one read walks the pages, so the file's header, field list and rows come from
/// the same code as a <c>query?f=json</c> answer — the same types, aliases, object ids and dates.
/// </para>
/// <para>
/// <b>Stops one row past <paramref name="most"/></b>, so the caller can tell "exactly the cap" from "more than the
/// cap" and refuse the second rather than cut it silently.
/// </para>
/// </remarks>
/// <param name="inner">The layer's source.</param>
/// <param name="page">The query for one page, given its offset.</param>
/// <param name="most">The most rows the file may hold.</param>
internal sealed class WholeLayerSource(IFeatureSource inner, Func<int, FeatureQuery> page, long most) : IFeatureSource
{
    /// <inheritdoc/>
    public FeatureSchema SchemaFor(FeatureQuery query) => inner.SchemaFor(query);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long read = 0;

        for (int offset = 0; ; offset += FeatureQuery.MaximumLimit)
        {
            int rows = 0;

            await foreach (Feature feature in inner.ReadAsync(page(offset), cancellationToken).ConfigureAwait(false))
            {
                rows++;
                read++;
                yield return feature;

                if (read > most)
                {
                    yield break;
                }
            }

            if (rows < FeatureQuery.MaximumLimit)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc/>
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
        inner.DescribeAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
        inner.CountAsync(query, cancellationToken);

    /// <inheritdoc/>
    public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
        inner.CountUpToAsync(query, ceiling, cancellationToken);
}
