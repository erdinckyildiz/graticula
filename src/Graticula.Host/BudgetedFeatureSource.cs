using System;
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
    IFeatureSource inner,
    ConnectionBudget budget,
    string source,
    SourceBreaker breaker) : IFeatureSource
{
    /// <summary>
    /// Refuses at once when this source failed a moment ago, and reports what happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-131](../../docs/architecture-debt.md): a refusal during an outage cost 8.0
    /// seconds, six times out of six.</b> Two blackholed connects at four seconds each —
    /// authentication resolves a principal before every route, then the endpoint reads.
    /// Each of those refusals occupies a connection for its whole four seconds, so the
    /// outage's cost grows with traffic instead of staying flat.
    /// </para>
    /// <para>
    /// <b>Checked before the budget rather than after.</b> A source that cannot answer
    /// should not take a permit on its way to failing: it would hold one for four seconds
    /// and, with ADR-046's queue bound, would fill the queue with requests that are
    /// certain to fail. Refusing first is what turns the outage from a queue collapse into
    /// a flat cost.
    /// </para>
    /// </remarks>
    private async ValueTask<T> GuardedAsync<T>(
        Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(source))
        {
            throw new SourceUnreachableException();
        }

        try
        {
            T answer = await work(cancellationToken).ConfigureAwait(false);
            breaker.Succeeded(source);

            return answer;
        }
        catch (Exception failure) when (breaker.Failed(source, failure))
        {
            // <b>Reported in the filter, so nothing is swallowed.</b> `Failed` returns
            // false for a database that answered — a bad filter must not take a service
            // down — and the filter returning false leaves the exception to whoever
            // handles it, unchanged either way.
            throw;
        }
    }

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
    public ValueTask<ConnectionBudget.Lease> LeaseAsync(CancellationToken cancellationToken)
    {
        // <b>The breaker applies to the unwrapped path too.</b> A caller reaching past
        // this decorator for the provider's own methods is still asking the same database,
        // and exempting it would leave the hole exactly where an ArcGIS client's first
        // request goes — the same reasoning this method's remarks give for the budget.
        if (breaker.IsOpen(source))
        {
            throw new SourceUnreachableException();
        }

        return budget.EnterAsync(source, cancellationToken);
    }

    /// <summary>
    /// Reports to the breaker what happened on the unwrapped path.
    /// </summary>
    /// <param name="failure">What went wrong, or null when the work succeeded.</param>
    /// <returns>Whether a failure tripped the breaker.</returns>
    /// <remarks>
    /// <b>Because <see cref="Inner"/> exists, and everything reached through it is
    /// invisible to this decorator.</b> `ShapedQueryAsync` unwraps to call the provider's
    /// own count, id-list, extent and statistics methods, and those are exactly the
    /// requests an ArcGIS client makes first. Without this, a `returnCountOnly` during an
    /// outage failed against the source and told the breaker nothing — measured: eight
    /// serial refusals at 8.0 seconds each with the breaker already in place, because only
    /// the authentication half of the cost was being reported.
    /// </remarks>
    public bool Observe(Exception? failure)
    {
        if (failure is null)
        {
            breaker.Succeeded(source);
            return false;
        }

        return breaker.Failed(source, failure);
    }

    /// <inheritdoc/>
    public FeatureSchema SchemaFor(FeatureQuery query) => inner.SchemaFor(query);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // <b>Not wrapped in GuardedAsync, because an iterator cannot be.</b> A `yield
        // return` inside a `try` with a `catch` is a compiler error, and rewriting this as
        // a buffered read to get one would undo the streaming the whole face is built on.
        // So the check is explicit and the failure is reported by the first `MoveNext`
        // throwing to the caller, where the exception middleware sees it — the breaker
        // misses that one trip and catches the next request's, which costs one slow
        // request rather than a design.
        if (breaker.IsOpen(source))
        {
            throw new SourceUnreachableException();
        }

        using ConnectionBudget.Lease lease =
            await budget.EnterAsync(source, cancellationToken).ConfigureAwait(false);

        await foreach (Feature feature in
                       inner.ReadAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return feature;
        }

        breaker.Succeeded(source);
    }

    /// <inheritdoc/>
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
        GuardedAsync(
            async token =>
            {
                using ConnectionBudget.Lease lease =
                    await budget.EnterAsync(source, token).ConfigureAwait(false);

                return await inner.DescribeAsync(token).ConfigureAwait(false);
            },
            cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
        GuardedAsync(
            async token =>
            {
                using ConnectionBudget.Lease lease =
                    await budget.EnterAsync(source, token).ConfigureAwait(false);

                return await inner.CountAsync(query, token).ConfigureAwait(false);
            },
            cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task<long> CountUpToAsync(
        FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
        GuardedAsync(
            async token =>
            {
                using ConnectionBudget.Lease lease =
                    await budget.EnterAsync(source, token).ConfigureAwait(false);

                return await inner.CountUpToAsync(query, ceiling, token).ConfigureAwait(false);
            },
            cancellationToken).AsTask();
}
