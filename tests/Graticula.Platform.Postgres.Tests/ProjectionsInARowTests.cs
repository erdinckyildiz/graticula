using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// One projector projects again and again without failing every other time.
/// </summary>
/// <remarks>
/// <b>Written 2026-10-12, when every second GeometryServer <c>project</c> on the local fixture answered 503.</b> On
/// PostGIS 3.6 with PROJ 8.2.1 — the OSGeo Windows bundle — <c>postgis_proj_version()</c> leaves an error in the
/// backend's PROJ context and the next <c>ST_Transform</c> there fails with <em>Unknown error (code 4096)</em>. The
/// projector asked for the version after every projection, on the pooled connection the next projection then took.
/// On a PROJ without the fault this passes either way; on one with it, it failed on the second projection.
/// </remarks>
public sealed class ProjectionsInARowTests : PostgresFixture
{
    [Fact]
    public async Task Ten_projections_in_a_row_all_answer()
    {
        PostGisProjector projector = new(DataSource);
        IReadOnlyList<Geometry> point = [new Point(3_228_265, 5_012_341)];

        for (int i = 0; i < 10; i++)
        {
            (IReadOnlyList<Geometry> projected, _) =
                await projector.ProjectAsync(point, 3857, 4326, CancellationToken.None);

            Point back = Assert.IsType<Point>(projected[0]);
            Assert.InRange(back.X, 28.99, 29.01);
        }
    }
}
