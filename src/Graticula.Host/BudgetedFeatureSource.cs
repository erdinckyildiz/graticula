using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// A feature source that holds a slot from <see cref="ConnectionBudget"/> while it works.
/// </summary>
/// <remarks>
/// <para>
/// <b>One decorator at one seam, rather than a gate inside eight provider classes.</b>
/// <see cref="LayerConnections.SourceFor"/> is the only place a read path gets a source, so wrapping
/// its answer bounds every query, count and description without the provider knowing a budget exists.
/// ADR-007 §4.8 asks for the bound; ADR-003's port rule is why it is not implemented inside the
/// provider.
/// </para>
/// <para>
/// <b>The slot is held for the whole read, not for the acquisition.</b> `ReadAsync` streams — the
/// connection is in use until the enumeration finishes or the caller walks away — so the lease is
/// taken before the first row and returned in a `finally` around the iteration. Releasing it at the
/// first row would bound *starting* queries rather than *running* them, which is not a bound at all.
/// </para>
/// <para>
/// <b><see cref="SchemaFor"/> takes no slot</b>, because it touches nothing: it is a pure function of
/// the query's field list. Charging it would consume the budget on work the database never sees.
/// </para>
/// </remarks>
internal sealed class BudgetedFeatureSource(
    IFeatureSource inner, ConnectionBudget budget, string source) : IFeatureSource
{
    /// <summary>
    /// What this wraps, for the one caller that needs the provider's own type.
    /// </summary>
    /// <remarks>
    /// <b>Because a decorator hides a concrete type, and something was type-testing for one.</b>
    /// `Program.ShapedQueryAsync` asks `source is not PostGisFeatureSource` before it can serve a
    /// count, an id list, an extent or statistics — those are the provider's own methods rather than
    /// `IFeatureSource`'s — and wrapping made that test fail for every layer, so every
    /// `returnCountOnly` on the server answered **501** the moment the budget was introduced. Found by
    /// probing a layer by hand rather than by reasoning about the change; the conformance suite had it
    /// too, mixed in with failures from a missing environment variable, which is how nearly it was
    /// missed.
    /// </remarks>
    public IFeatureSource Inner => inner;

    /// <summary>
    /// Takes this source's budget slot, for a caller that will then use <see cref="Inner"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A lease. Dispose it to give the slot back.</returns>
    /// <remarks>
    /// <b>Unwrapping must not mean escaping the bound.</b> A count is a database query like any other —
    /// `count(*)` over a filtered extent is one of the more expensive things this server issues — so
    /// the caller that reaches past this decorator takes the slot first. The alternative, exempting the
    /// shape queries, would leave a hole in the bound exactly where an ArcGIS client's first request
    /// goes.
    /// </remarks>
    public ValueTask<ConnectionBudget.Lease> LeaseAsync(CancellationToken cancellationToken) =>
        budget.EnterAsync(source, cancellationToken);

    /// <inheritdoc/>
    public FeatureSchema SchemaFor(FeatureQuery query) => inner.SchemaFor(query);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using ConnectionBudget.Lease lease =
            await budget.EnterAsync(source, cancellationToken).ConfigureAwait(false);

        await foreach (Feature feature in
                       inner.ReadAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return feature;
        }
    }

    /// <inheritdoc/>
    public async Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken)
    {
        using ConnectionBudget.Lease lease =
            await budget.EnterAsync(source, cancellationToken).ConfigureAwait(false);

        return await inner.DescribeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        using ConnectionBudget.Lease lease =
            await budget.EnterAsync(source, cancellationToken).ConfigureAwait(false);

        return await inner.CountAsync(query, cancellationToken).ConfigureAwait(false);
    }
}
