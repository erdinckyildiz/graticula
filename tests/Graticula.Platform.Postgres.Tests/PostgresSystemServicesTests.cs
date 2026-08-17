using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A service with no layers behind it, and the two things an operator sets on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the owner asked a question the code could not answer.</b> Looking at
/// the geometry service's row on 2026-08-17: *"geometry server'in, startı stop'u, timeout'u vs si
/// yok mu?"* — hasn't it got a start, a stop, a timeout? It had sharing and nothing else, and the
/// console was drawing a <c>started</c> pill that was a literal in the markup.
/// </para>
/// <para>
/// <b>Sharing and status are asserted together, on purpose.</b> They are the two facts on this
/// row and they must move independently: a stop that also narrowed the audience would be a
/// different operation from the one an operator asked for, and D-24 is the register entry about
/// two facts moving when one was meant to.
/// </para>
/// </remarks>
public sealed class PostgresSystemServicesTests : PostgresFixture
{
    /// <summary>The geometry service is there after a migration, started and organisation-wide.</summary>
    /// <remarks>
    /// <b>Started is the seeded default and that is a decision.</b> The service has answered since
    /// it shipped, so a migration that left it stopped would take a working endpoint away from
    /// every existing deployment as a side effect of adding a column.
    /// </remarks>
    [Fact]
    public async Task A_migrated_store_has_a_started_geometry_service()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        SystemService geometry =
            (await services.FindAsync("Geometry", CancellationToken.None))!.Value;

        Assert.Equal("GeometryServer", geometry.Kind);
        Assert.Equal("Utilities", geometry.Folder);
        Assert.Equal(SharingScope.Organization, geometry.Sharing);
        Assert.Equal(ServiceStatus.Started, geometry.Status);
    }

    /// <summary>Stopping reports what it was, and stopping twice says so.</summary>
    /// <remarks>
    /// <b>The previous status rather than a bool</b>, for the same reason the layer setter returns
    /// one: *it was already stopped* is a different answer from *it is stopped now*, and an
    /// operator acting on an incident needs to know which of those their request was.
    /// </remarks>
    [Fact]
    public async Task Stopping_reports_the_status_it_replaced()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        Assert.Equal(
            ServiceStatus.Started,
            await services.SetStatusAsync("Geometry", ServiceStatus.Stopped, CancellationToken.None));

        Assert.Equal(
            ServiceStatus.Stopped,
            (await services.FindAsync("Geometry", CancellationToken.None))!.Value.Status);

        // Again, which is the case an operator hits when two people are working an incident.
        Assert.Equal(
            ServiceStatus.Stopped,
            await services.SetStatusAsync("Geometry", ServiceStatus.Stopped, CancellationToken.None));

        Assert.Equal(
            ServiceStatus.Stopped,
            await services.SetStatusAsync("Geometry", ServiceStatus.Started, CancellationToken.None));
    }

    /// <summary>A stop leaves the audience alone, and a sharing change leaves the status alone.</summary>
    /// <remarks>
    /// <b>The invariant the endpoint's own note promises.</b> It tells an operator that *starting
    /// it restores exactly the audience it had*, and that sentence is only true if the two setters
    /// touch different columns. Asserted in both directions because one direction passing proves
    /// nothing about the other.
    /// </remarks>
    [Fact]
    public async Task Status_and_sharing_move_independently()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        await services.SetSharingAsync("Geometry", SharingScope.Public, CancellationToken.None);
        await services.SetStatusAsync("Geometry", ServiceStatus.Stopped, CancellationToken.None);

        SystemService stopped =
            (await services.FindAsync("Geometry", CancellationToken.None))!.Value;

        Assert.Equal(SharingScope.Public, stopped.Sharing);
        Assert.Equal(ServiceStatus.Stopped, stopped.Status);

        await services.SetSharingAsync("Geometry", SharingScope.Private, CancellationToken.None);

        SystemService narrowed =
            (await services.FindAsync("Geometry", CancellationToken.None))!.Value;

        Assert.Equal(SharingScope.Private, narrowed.Sharing);

        Assert.Equal(
            ServiceStatus.Stopped,
            narrowed.Status);
    }

    /// <summary>Setting the status of something that is not there says so.</summary>
    /// <remarks>
    /// Null rather than an exception, so the endpoint above can answer 404 with the name in it
    /// instead of a 500 — the difference between a typo an operator can fix and a fault they
    /// report.
    /// </remarks>
    [Fact]
    public async Task An_absent_service_reports_no_previous_status()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        Assert.Null(
            await services.SetStatusAsync(
                "NoSuchService", ServiceStatus.Stopped, CancellationToken.None));
    }

    /// <summary>The listing carries the status too, since that is what a console row reads.</summary>
    [Fact]
    public async Task The_listing_carries_the_status()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        await services.SetStatusAsync("Geometry", ServiceStatus.Stopped, CancellationToken.None);

        SystemService listed =
            Assert.Single(await services.ListAsync(CancellationToken.None));

        Assert.Equal("Geometry", listed.Name);

        Assert.Equal(
            ServiceStatus.Stopped,
            listed.Status);
    }

    /// <summary>The bounds round-trip, and null puts the default back.</summary>
    /// <remarks>
    /// <para>
    /// <b>Three states, not two.</b> Null means *nobody has said* and the configured default
    /// answers; a number means an administrator chose it; and for the pre-flight, **zero is a third
    /// answer** — no pre-flight at all — which is why it cannot be stored as null. Collapsing the
    /// first two would make a fresh install indistinguishable from one where somebody chose the
    /// default deliberately.
    /// </para>
    /// <para>
    /// The clear is asserted because it is the operation an administrator most needs and the one
    /// most easily written as *ignore nulls*: without it, getting back to the server's default
    /// means looking it up and typing a copy that stops tracking the setting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Bounds_round_trip_and_null_restores_the_default()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        Assert.Null((await services.FindAsync("Geometry", CancellationToken.None))!.Value.DeadlineSeconds);

        Assert.True(await services.SetBoundsAsync("Geometry", 45, 100_000, CancellationToken.None));

        SystemService set = (await services.FindAsync("Geometry", CancellationToken.None))!.Value;
        Assert.Equal(45, set.DeadlineSeconds);
        Assert.Equal(100_000, set.PreflightPairs);

        // Zero is a value, not an absence: it means run no pre-flight.
        await services.SetBoundsAsync("Geometry", 45, 0, CancellationToken.None);
        Assert.Equal(0, (await services.FindAsync("Geometry", CancellationToken.None))!.Value.PreflightPairs);

        await services.SetBoundsAsync("Geometry", null, null, CancellationToken.None);

        SystemService cleared = (await services.FindAsync("Geometry", CancellationToken.None))!.Value;
        Assert.Null(cleared.DeadlineSeconds);

        Assert.Null(
            cleared.PreflightPairs);
    }

    /// <summary>The bounds are separate from the status, so setting one does not move the other.</summary>
    /// <remarks>
    /// The third pair of facts on this row, and the third time it is worth asserting: D-24 is this
    /// repository's record of two facts moving when one was meant to, and it has cost twice.
    /// </remarks>
    [Fact]
    public async Task Setting_bounds_leaves_the_status_and_the_sharing_alone()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        await services.SetStatusAsync("Geometry", ServiceStatus.Stopped, CancellationToken.None);
        await services.SetSharingAsync("Geometry", SharingScope.Public, CancellationToken.None);
        await services.SetBoundsAsync("Geometry", 20, null, CancellationToken.None);

        SystemService service =
            (await services.FindAsync("Geometry", CancellationToken.None))!.Value;

        Assert.Equal(20, service.DeadlineSeconds);
        Assert.Equal(ServiceStatus.Stopped, service.Status);

        Assert.Equal(
            SharingScope.Public,
            service.Sharing);
    }

    /// <summary>Setting the bounds of something absent says so rather than silently doing nothing.</summary>
    [Fact]
    public async Task An_absent_service_cannot_have_bounds_set()
    {
        await MigrateAsync();

        PostgresSystemServices services = new(DataSource);

        Assert.False(
            await services.SetBoundsAsync("NoSuchService", 10, null, CancellationToken.None));
    }
}
