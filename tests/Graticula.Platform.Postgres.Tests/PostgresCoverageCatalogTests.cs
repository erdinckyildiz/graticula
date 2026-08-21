using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Registering a coverage, and the three things an administrator does to one after.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the conformance suite, deliberately.</b> These mutate a
/// service's state, and a conformance class that reconfigures a live service while
/// another walks the catalogue is what [D-75](../../docs/architecture-debt.md) spent
/// three days being about. The face's own behaviour is checked from outside without
/// writing anything; the writes are checked here, against a store this suite owns.
/// </para>
/// <para>
/// <b>What is under test is the pair of questions an administrator asks.</b> Can I
/// switch this off, and can I take it away — and, for the second, does taking it away
/// leave the file where it was, because
/// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.3 registers
/// imagery in place and that promise is only worth something if removal honours it.
/// </para>
/// </remarks>
public sealed class PostgresCoverageCatalogTests : PostgresFixture
{
    private static CoverageInfo Info() => new(
        64,
        48,
        4326,
        new Envelope(30, 39, 31, 40),
        [new BandInfo(0, SampleKind.Unsigned8, null, null, null)],
        [new OverviewInfo(1, 32, 24)],
        256,
        256);

    private async Task<(PostgresCoverageCatalog Catalog, string Name)> ReadyAsync()
    {
        // The fixture hands out an empty schema; the migrations are what put a
        // `service` and a `coverage` table in it.
        await MigrateAsync();

        PostgresCoverageCatalog catalog = new(DataSource);

        string name = $"coverage_{Guid.NewGuid():N}"[..24];

        await catalog.RegisterAsync(
            null, name, $"C:/nowhere/{name}.tif", Info(), null, CancellationToken.None);

        return (catalog, name);
    }

    [Fact]
    public async Task A_registration_reads_back_as_what_was_registered()
    {
        (PostgresCoverageCatalog catalog, string name) = await ReadyAsync();

        PublishedCoverage? found =
            await catalog.FindAsync(null, name, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(4326, found!.Info.Srid);
        Assert.Equal(64, found.Info.Width);
        Assert.Equal(48, found.Info.Height);
        Assert.Single(found.Info.Bands);

        // <b>The pyramid is stored as a count and rebuilt by halving.</b> That is an
        // assumption `PostgresCoverageCatalog.Read` states in its own remarks, and this
        // is the assertion that would fail if a format ever arrived whose overviews are
        // not halvings.
        Assert.Single(found.Info.Overviews);
        Assert.Equal(32, found.Info.Overviews[0].Width);

        // Private and started, which is what registration writes and what the
        // registration response says out loud.
        Assert.Equal(SharingScope.Private, found.Sharing);
        Assert.Equal(ServiceStatus.Started, found.Status);
    }

    [Fact]
    public async Task The_same_file_cannot_be_registered_twice()
    {
        // Two services answering identically that diverge the moment one is restyled —
        // the shape D-61 recorded for a setting living on the wrong object. A
        // deployment wanting two views of one raster wants two rendering rules on one
        // coverage, which is a different feature.
        await MigrateAsync();

        PostgresCoverageCatalog catalog = new(DataSource);

        string path = $"C:/nowhere/{Guid.NewGuid():N}.tif";

        await catalog.RegisterAsync(
            null, $"first_{Guid.NewGuid():N}"[..20], path, Info(), null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => catalog.RegisterAsync(
            null, $"second_{Guid.NewGuid():N}"[..20], path, Info(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_and_starting_change_what_the_face_will_serve()
    {
        (PostgresCoverageCatalog catalog, string name) = await ReadyAsync();

        Assert.True(await catalog.SetStatusAsync(
            null, name, ServiceStatus.Stopped, CancellationToken.None));

        PublishedCoverage? stopped = await catalog.FindAsync(null, name, CancellationToken.None);

        Assert.Equal(ServiceStatus.Stopped, stopped!.Status);

        Assert.True(await catalog.SetStatusAsync(
            null, name, ServiceStatus.Started, CancellationToken.None));

        PublishedCoverage? started = await catalog.FindAsync(null, name, CancellationToken.None);

        Assert.Equal(ServiceStatus.Started, started!.Status);
    }

    [Fact]
    public async Task Changing_the_status_of_something_that_is_not_a_coverage_does_nothing()
    {
        // The update joins `coverage`, so this route cannot reach a feature service that
        // happens to share a name. An endpoint that administers coverages should refuse
        // anything else rather than quietly succeed against it.
        await MigrateAsync();

        PostgresCoverageCatalog catalog = new(DataSource);

        Assert.False(await catalog.SetStatusAsync(
            null, $"absent_{Guid.NewGuid():N}"[..20], ServiceStatus.Stopped,
            CancellationToken.None));
    }

    [Fact]
    public async Task Removing_a_coverage_takes_its_service_with_it()
    {
        (PostgresCoverageCatalog catalog, string name) = await ReadyAsync();

        Assert.True(await catalog.RemoveAsync(null, name, CancellationToken.None));
        Assert.Null(await catalog.FindAsync(null, name, CancellationToken.None));

        // <b>And the service is gone too, not left behind as an empty one.</b> A
        // leftover container is what D-48 is about and what the empty-service sweep
        // exists to clear up; leaving one here would create the mess the sweep then
        // has to be trusted not to delete wrongly.
        PostgresLayerCatalog layers =
            new(DataSource, new Graticula.Platform.Secrets.SecretProtector(1, new byte[32]));

        Assert.Null(await layers.FindServiceAsync(null, name, CancellationToken.None));

        // Idempotent: a second removal reports that there was nothing to remove rather
        // than succeeding about nothing.
        Assert.False(await catalog.RemoveAsync(null, name, CancellationToken.None));
    }

    [Fact]
    public async Task Every_registered_coverage_appears_in_the_listing()
    {
        (PostgresCoverageCatalog catalog, string name) = await ReadyAsync();

        IReadOnlyList<PublishedCoverage> all =
            await catalog.ListAsync(CancellationToken.None);

        Assert.Contains(all, c => string.Equals(c.Name, name, StringComparison.Ordinal));
    }
}
