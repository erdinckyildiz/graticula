using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// The projector, refusing instantly while the database behind it is unreachable.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-127](../../docs/architecture-debt.md)'s first axis, second half.</b> Once the listing
/// itself survived an outage, what a capabilities document still cost was measured: WFS answered
/// in 6.0 s and WMS in 8.0 s with the store down, and neither wait was the listing. It was this
/// — <c>EX_GeographicBoundingBox</c> is mandatory on a WMS 1.3.0 named layer and every layer not
/// already in WGS 84 needs a round trip to get one, so a document lists one projection call per
/// distinct spatial reference and each one waits out a connect that nothing answers.
/// </para>
/// <para>
/// <b>Nothing about the answer changes, only the waiting.</b> Every caller of
/// <see cref="IProjector"/> on the capabilities path already catches a failure and leaves that
/// layer without a geographic extent, on the argument that one unusual layer must not make the
/// whole server look absent. This arrives at the same place immediately.
/// </para>
/// <para>
/// <b>A decorator in the host rather than a check inside
/// <c>PostGisProjector</c>.</b> The breaker is Tier 1 and the projector is a Tier 2 adapter
/// (<c>CLAUDE.md</c> §4); a provider that knew about the server's circuit breaker would be the
/// port interface leaking in the direction it exists to prevent.
/// </para>
/// <para>
/// <b>Its own key, and that is deliberate rather than a shortcut.</b> The projection pool is the
/// datastore pool, which hosted layers also use, but the string a layer carries and the string
/// this pool was built from are not guaranteed to be the same text — and a breaker key is text.
/// Rather than assert an equality nothing enforces, this reports its own failures under its own
/// name: the first projection during an outage pays one connect, and every one after it is
/// refused until the cooling window lets a single prober through. That is the same shape
/// <see cref="SourceBreaker"/> gives every other source, one connect later.
/// </para>
/// </remarks>
/// <param name="inner">The real projector.</param>
/// <param name="breaker">The breaker.</param>
internal sealed class BreakingProjector(IProjector inner, SourceBreaker breaker) : IProjector
{
    /// <summary>What this projector calls itself to the breaker.</summary>
    /// <remarks>
    /// <b>A name no connection string can be.</b> The same device
    /// <see cref="SourceBreaker.PlatformStore"/> uses, and for the same reason: the key space is
    /// connection strings, and a leading NUL is not one.
    /// </remarks>
    public const string Source = "projection";

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)>
        ProjectAsync(
            IReadOnlyList<Geometry> geometries,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            throw new SourceUnreachableException(
                "The database that performs coordinate transformations is unreachable. This "
                + "request was refused without waiting for it.");
        }

        try
        {
            (IReadOnlyList<Geometry> projected, ProjectionProvenance provenance) =
                await inner.ProjectAsync(geometries, fromSrid, toSrid, cancellationToken)
                    .ConfigureAwait(false);

            breaker.Succeeded(Source);

            return (projected, provenance);
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            // Reported in the filter so nothing is swallowed: `Failed` answers false for a
            // database that replied — an unknown SRID must not trip anything — and a false
            // filter leaves the exception exactly as it was.
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Forwarded, and forgetting to forward it is what the interface stopped allowing.</b>
    /// This was added as a default returning null and every implementer inherited it — including
    /// this one, which decorates the projector that can. The server then refused every reference
    /// definition PostGIS had just accepted, with a message blaming PROJ. The default is gone;
    /// the compiler asks now.
    /// </remarks>
    public async Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
        IReadOnlyList<Geometry> geometries,
        int fromSrid,
        string definition,
        CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            throw new SourceUnreachableException(
                "The database that performs coordinate transformations is unreachable. This "
                + "request was refused without waiting for it.");
        }

        try
        {
            IReadOnlyList<Geometry>? moved = await inner
                .ProjectToDefinitionAsync(geometries, fromSrid, definition, cancellationToken)
                .ConfigureAwait(false);

            breaker.Succeeded(Source);

            return moved;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Guarded like a projection, because it is one with a simplifier after it</b> — and it is
    /// on a map's tile path, where a four-second wait per tile is the outage D-127 measured.
    /// </remarks>
    public async Task<IReadOnlyList<Geometry>> GeneralizeAsync(
        IReadOnlyList<Geometry> geometries,
        int fromSrid,
        int toSrid,
        double tolerance,
        CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            throw new SourceUnreachableException(
                "The database that performs coordinate transformations is unreachable. This "
                + "request was refused without waiting for it.");
        }

        try
        {
            IReadOnlyList<Geometry> simplified = await inner
                .GeneralizeAsync(geometries, fromSrid, toSrid, tolerance, cancellationToken)
                .ConfigureAwait(false);

            breaker.Succeeded(Source);

            return simplified;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Guarded too, because this is the cheaper question asked on the same socket.</b> A
    /// deployment asking *do you know EPSG:2154* during an outage waits precisely as long as one
    /// asking for a transformation, and the caller's fallback for *no* and for *cannot say* is
    /// the same refusal.
    /// </remarks>
    public async Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            throw new SourceUnreachableException(
                "The database that performs coordinate transformations is unreachable. This "
                + "request was refused without waiting for it.");
        }

        try
        {
            bool knows = await inner.KnowsAsync(srid, cancellationToken).ConfigureAwait(false);

            breaker.Succeeded(Source);

            return knows;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Guarded the same way, and empty for the same reason null is above.</b> A screen that
    /// cannot name a code shows the code, which is where it was before the list existed — so an
    /// outage costs a name rather than the ability to publish.
    /// </remarks>
    public async Task<IReadOnlyList<Graticula.Geometries.KnownReference>> ReferencesAsync(
        CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            return [];
        }

        try
        {
            IReadOnlyList<Graticula.Geometries.KnownReference> all =
                await inner.ReferencesAsync(cancellationToken).ConfigureAwait(false);

            breaker.Succeeded(Source);

            return all;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            return [];
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Not guarded, and that is the difference between this and the other three.</b>
    /// A refusal here means *this deployment cannot say what the reference holds*, and the
    /// caller's answer to that is already **do not clamp** — the same answer an older PostGIS
    /// gives. Throwing would turn a filter this server cannot narrow into a request it
    /// refuses, which is a worse answer to the same uncertainty, and it would do it on the
    /// path [D-165](../../docs/architecture-debt.md) exists to make safer.
    /// </remarks>
    public async Task<Graticula.Geometries.Envelope?> DomainOfAsync(
        int srid, CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            return null;
        }

        try
        {
            Graticula.Geometries.Envelope? domain =
                await inner.DomainOfAsync(srid, cancellationToken).ConfigureAwait(false);

            breaker.Succeeded(Source);

            return domain;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            // <b>Recorded as a failure and answered as an absence.</b> The breaker should
            // learn the source is unwell; the caller should not learn a bounding box.
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Guarded like the other two, and its caller cares more than they do.</b>
    /// [Q-141](../../docs/open-questions.md)'s notice is taken on the request path, so an
    /// unguarded call here would put the four-second wait D-127 measured back on every
    /// FeatureServer query that names an <c>outSR</c> — on the path whose whole purpose is
    /// to be fast. The caller treats a refusal as *not known yet* and records nothing, which
    /// is right: an outage is not evidence about a datum, and the next request asks again.
    /// </remarks>
    public async Task<ProjectionProvenance> DescribeAsync(
        int fromSrid, int toSrid, CancellationToken cancellationToken)
    {
        if (breaker.IsOpen(Source))
        {
            throw new SourceUnreachableException(
                "The database that performs coordinate transformations is unreachable. This "
                + "request was refused without waiting for it.");
        }

        try
        {
            ProjectionProvenance provenance = await inner
                .DescribeAsync(fromSrid, toSrid, cancellationToken)
                .ConfigureAwait(false);

            breaker.Succeeded(Source);

            return provenance;
        }
        catch (Exception failure) when (breaker.Failed(Source, failure))
        {
            throw;
        }
    }
}
