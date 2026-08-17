using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A transformation that crossed a datum says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-32: the failure this guards against has no error, no log line and no
/// visual signature.</b> PROJ picks the pipeline; when the shift grids for the
/// accurate path are absent it falls back to a ballpark transformation
/// <em>without failing</em>, and the result is metres from where the data is.
/// The map looks right and is in the wrong place, on exactly the data — cadastral
/// and survey — where metres are legally significant.
/// </para>
/// <para>
/// <b>What is asserted is not the accuracy, because the accuracy is not
/// reachable.</b> <c>ST_Transform</c> does not say which pipeline it chose and
/// PROJ's operation database is not queryable from SQL (Q-100). What the two
/// references do say, in their own WKT, is whether a datum change was required
/// at all — and that is the line between a transformation that is exact by
/// construction and one that can silently be wrong.
/// </para>
/// </remarks>
public sealed class ProjectorDatumTests : PostgresFixture
{
    private PostGisProjector Projector() => new(DataSource);

    private static readonly IReadOnlyList<Geometry> OnePoint = [new Point(32.85, 39.93)];

    private async Task<ProjectionProvenance> ProvenanceAsync(int from, int to)
    {
        (_, ProjectionProvenance provenance) = await Projector()
            .ProjectAsync(OnePoint, from, to, CancellationToken.None);

        return provenance;
    }

    /// <summary>
    /// Web Mercator from WGS 84 is one datum, and is not flagged.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the assertion, and the one that keeps it useful.</b>
    /// A check that flagged everything would be ignored within a week, and the
    /// commonest transformation in the product — geographic to Web Mercator for
    /// a tile — is a closed formula on a single datum with nothing to warn
    /// about.
    /// </remarks>
    [Theory]
    [InlineData(4326, 3857)]
    [InlineData(3857, 4326)]
    [InlineData(4326, 4326)]
    public async Task A_transformation_within_one_datum_carries_no_caution(int from, int to)
    {
        ProjectionProvenance provenance = await ProvenanceAsync(from, to);

        Assert.False(provenance.DatumShift);
        Assert.Null(provenance.Caution);
    }

    /// <summary>
    /// WGS 84 to a national grid on its own datum is flagged, by name.
    /// </summary>
    /// <remarks>
    /// <b>EPSG:5254 is TUREF / TM30 and its datum is the Turkish National
    /// Reference Frame.</b> Going there from WGS 84 is a datum change whose
    /// accurate path needs a transformation PROJ may or may not have — which is
    /// the whole of D-32, and the reason this test names a real national grid
    /// rather than inventing a pair.
    /// </remarks>
    [Fact]
    public async Task A_transformation_across_datums_says_which_two()
    {
        ProjectionProvenance provenance = await ProvenanceAsync(4326, 5254);

        Assert.True(provenance.DatumShift);
        Assert.NotNull(provenance.Caution);

        // Both names, so a reader can judge the pair rather than being told
        // only that something happened.
        Assert.Contains("WGS_1984", provenance.Caution!, StringComparison.Ordinal);
        Assert.Contains(
            "Turkish_National_Reference_Frame", provenance.Caution!, StringComparison.Ordinal);

        // And what it means for them, in the words that matter.
        Assert.Contains("ballpark", provenance.Caution!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without failing", provenance.Caution!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reference the server cannot read is reported as unknown, not as safe.
    /// </summary>
    /// <remarks>
    /// <b>Null, not false.</b> "No datum change" about a reference nobody could
    /// read is the same silent wrongness the whole check exists to remove, and
    /// it would be worse for being stated confidently.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_reference_is_unknown_rather_than_safe()
    {
        // 999999 is not in spatial_ref_sys, so ST_Transform itself fails — which
        // is the right outcome and not what is under test here. The datum check
        // runs first and its answer is what this asserts, so the projection is
        // done with no geometry at all.
        (_, ProjectionProvenance provenance) = await Projector()
            .ProjectAsync([], 4326, 999_999, CancellationToken.None);

        Assert.Null(provenance.DatumShift);
        Assert.NotNull(provenance.Caution);
        Assert.Contains("could not read the datum", provenance.Caution!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine is still named, because the caution does not replace it.
    /// </summary>
    [Fact]
    public async Task The_engine_is_still_reported()
    {
        ProjectionProvenance provenance = await ProvenanceAsync(4326, 3857);

        Assert.Contains("PROJ", provenance.Engine, StringComparison.OrdinalIgnoreCase);

        // Still null, and still honestly so: the pipeline's stated accuracy is
        // in PROJ's operation database and not reachable from SQL.
        Assert.Null(provenance.Accuracy);
    }
}
