using System;
using Graticula.Api.Wms;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The remembered time extent is forgotten with everything else about a layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-160](../../docs/architecture-debt.md): a cache with no eviction, keyed by a
/// value that is never reused.</b> It lived in <c>WmsEndpoints</c> as a <c>static</c>
/// dictionary, read and written and never removed from. The five-minute lifetime answers
/// <em>may I trust this</em>; nothing was asking <em>should this still be here</em>. A
/// republished layer gets a new id — and [D-34](../../docs/architecture-debt.md) makes
/// republishing the ordinary way to correct a name — so the count grew with the number of
/// publications a deployment had ever made, and the entries for layers that no longer
/// exist were unreachable and immortal.
/// </para>
/// <para>
/// <b>Small, permanent, and exactly the shape [Q-64](../../docs/open-questions.md)
/// says is hard to see.</b> A hundred bytes an entry is not what exhausts a server; what
/// it does is make the heap grow with no corresponding load, which is the one signal that
/// question wants to use to tell a leak from a warm cache.
/// </para>
/// </remarks>
public sealed class RememberedTimeExtentTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static TimeDimension Extent(int year) =>
        new("observed_at", new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), null);

    [Fact]
    public void A_remembered_extent_comes_back_until_it_is_stale()
    {
        ServiceContexts contexts = Build();
        Guid layer = Guid.NewGuid();
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        contexts.RememberTime(layer, Extent(2024), at);

        Assert.NotNull(contexts.RememberedTime(layer, Lifetime, at.AddMinutes(4)));

        // Past the lifetime it is measured again rather than trusted — which is what the
        // cache was written for and is not what D-160 was about.
        Assert.Null(contexts.RememberedTime(layer, Lifetime, at.AddMinutes(6)));
    }

    [Fact]
    public void Forgetting_a_layer_forgets_its_time_extent()
    {
        // <b>The whole of D-160 in one assertion.</b> `Forget` is called by the unpublish
        // and refresh paths; before this the extent was in another class and stayed.
        ServiceContexts contexts = Build();
        Guid layer = Guid.NewGuid();
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        contexts.RememberTime(layer, Extent(2024), at);
        Assert.Equal(1, contexts.TimeCount);

        contexts.Forget(Layer(layer));

        Assert.Equal(0, contexts.TimeCount);
        Assert.Null(contexts.RememberedTime(layer, Lifetime, at));
    }

    [Fact]
    public void Publishing_the_same_table_again_does_not_leave_the_old_entry_behind()
    {
        // <b>The republish case, which is the one that made this unbounded.</b> D-34: a
        // rename is an unpublish and a publish, and the new layer has a new id. Ten
        // corrections used to leave ten entries; the count now follows what exists.
        ServiceContexts contexts = Build();
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        Guid previous = Guid.Empty;

        for (int correction = 0; correction < 10; correction++)
        {
            if (previous != Guid.Empty)
            {
                contexts.Forget(Layer(previous));
            }

            Guid now = Guid.NewGuid();
            contexts.RememberTime(now, Extent(2024 + correction), at);
            previous = now;
        }

        Assert.Equal(1, contexts.TimeCount);
    }

    [Fact]
    public void Forgetting_everything_forgets_the_extents_too()
    {
        ServiceContexts contexts = Build();
        DateTimeOffset at = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        contexts.RememberTime(Guid.NewGuid(), Extent(2024), at);
        contexts.RememberTime(Guid.NewGuid(), Extent(2025), at);

        Assert.Equal(2, contexts.TimeCount);

        contexts.Forget(null);

        Assert.Equal(0, contexts.TimeCount);
    }

    /// <summary>
    /// A context set with no data source behind it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here describes a table.</b> These tests are about what is remembered
    /// and forgotten, not about reading a shape, so the source factory is never called
    /// and a stub that throws would be as good as one that works.
    /// </remarks>
    private static ServiceContexts Build() => new(new NoSources(), TimeProvider.System);

    private sealed class NoSources : IServiceSources
    {
        public IFeatureSource SourceFor(PublishedLayer layer) =>
            throw new NotSupportedException("These tests never read a shape.");
    }

    /// <summary>A published layer with the given id, which is all `Forget` reads here.</summary>
    private static PublishedLayer Layer(Guid id) =>
        new(
            id,
            new LayerDefinition("probe", "hosted", "probe", "shape", 3857, "id", "objectid", false),
            "datastore",
            "Host=nowhere",
            GeometryKind.Polygon,
            null,
            SharingScope.Private,
            ServiceStatus.Started);
}
