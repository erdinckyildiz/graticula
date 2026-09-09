using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Render.Skia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The composition preview bounds a layer by how much geometry it reads, not only by rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-148](../../docs/open-questions.md), answered by measurement on 2026-09-09.</b>
/// [benchmarks/publish-scale](../../benchmarks/publish-scale/RESULTS.md) discharged ADR-057
/// condition 6 and left one thing open: the preview's cost is in vertices and its only bound
/// counted rows, so the layer that needed bounding was the one the bound did nothing about. The
/// worst layer in the corpus — 16,000 rows of 501 vertices — drew in <b>1,287 ms</b> with the
/// 4,000-row ceiling applied to it.
/// </para>
/// <para>
/// <b>The rule is <c>min(ceiling, budget ÷ avg_width)</c>, and the statistic is the planner's
/// own.</b> At a 2 MB budget that layer draws in <b>82 ms</b>, every dense layer lands in
/// 67–82 ms whatever its row count, and no simple layer moves — a 16,000-row 5-vertex layer
/// draws in 24.1 ms with the budget and without it.
/// </para>
/// <para>
/// <b>Three of these are arithmetic and the fourth is the wiring, which is the one that could
/// rot quietly.</b> Two bounds now cut a layer short and both mean the same thing to whoever is
/// looking at the picture, so both have to feed the one signal that says so. A version of this
/// change that computed a per-layer limit and then compared what was drawn against the shared
/// ceiling would draw correctly and report nothing, which is the state ADR-057 condition 7
/// exists to have ended.
/// </para>
/// <para>
/// <b>The widths here are measured rather than invented.</b> 136, 856 and 8,056 bytes are what
/// <c>pg_stats.avg_width</c> reports for the corpus's 5-, 50- and 500-vertex polygons, read back
/// out of the same PostgreSQL the benchmark ran against.
/// </para>
/// </remarks>
public sealed class APreviewIsBoundedByBytesNotRowsTests
{
    /// <summary>The preview's row ceiling, which is what the budget is measured against.</summary>
    private const int Ceiling = 4000;

    /// <summary>What one 500-vertex polygon of the corpus occupies, from `pg_stats`.</summary>
    private const int DensePolygonBytes = 8056;

    /// <summary>And one 5-vertex polygon.</summary>
    private const int SimplePolygonBytes = 136;

    /// <summary>
    /// A dense layer is held to what the budget buys, not to the row ceiling.
    /// </summary>
    /// <remarks>
    /// <b>260 is the whole point and it is a long way under 4,000.</b> 2 MB divided by 8,056
    /// bytes is what turns 1,287 ms into 82 ms; a rule that produced something near the ceiling
    /// would be a rule that changed nothing about the case it was written for.
    /// </remarks>
    [Fact]
    public void The_budget_binds_on_a_dense_layer()
    {
        int limit = CompositionPreview.RowLimit(
            Ceiling,
            CompositionPreview.DefaultGeometryBudgetBytes,
            new GeometryWidth(16000, DensePolygonBytes, 132145152));

        Assert.Equal(260, limit);
    }

    /// <summary>
    /// A simple layer is drawn exactly as it was, because the ceiling still binds first.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the budget safe to apply to every layer rather than to some.</b> 2 MB
    /// of 136-byte polygons is over fifteen thousand of them, so the arithmetic never reaches the
    /// case it was not written for. A budget that quietly lowered simple layers too would trade a
    /// picture the operator can trust for a problem those layers do not have — 16,000 rows of 5
    /// vertices already draw in 24.1 ms.
    /// </remarks>
    [Fact]
    public void The_row_ceiling_still_binds_on_a_simple_layer()
    {
        int limit = CompositionPreview.RowLimit(
            Ceiling,
            CompositionPreview.DefaultGeometryBudgetBytes,
            new GeometryWidth(16000, SimplePolygonBytes, 3842048));

        Assert.Equal(Ceiling, limit);
    }

    /// <summary>
    /// A table nothing has analysed or indexed is drawn, not refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>reltuples</c> is −1 there, and that is a real state rather than a corner.</b> Checked
    /// against PostgreSQL 16.4: a table that has had neither <c>CREATE INDEX</c> nor
    /// <c>ANALYZE</c> reports −1 — not a bad estimate, no estimate — and carries no
    /// <c>pg_stats</c> row for any column. That is exactly the freshly imported layer somebody is
    /// most likely to be previewing.
    /// </para>
    /// <para>
    /// <b>So the branch has to draw.</b> Refusing would refuse the commonest case; guessing a
    /// width would bound the one layer nothing is known about by a number nothing supports. The
    /// row ceiling is still a bound and is what this server always did.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_layer_with_no_statistics_at_all_is_drawn_to_the_row_ceiling()
    {
        int limit = CompositionPreview.RowLimit(
            Ceiling,
            CompositionPreview.DefaultGeometryBudgetBytes,
            new GeometryWidth(-1, null, 114688));

        Assert.Equal(Ceiling, limit);
    }

    /// <summary>
    /// A statistic that exists and says zero is an absence, and it is normalised into one
    /// before <c>RowLimit</c> ever sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This began as a falsification that found nothing, and it is kept because what it
    /// established is worth keeping.</b> <c>RowLimit</c> guards <c>bytes &lt;= 0</c> before
    /// dividing, and weakening that guard to <c>bytes &lt;= -1</c> leaves every test green —
    /// which looked like an untested branch and a division by zero waiting for a layer whose
    /// geometry column <c>ANALYZE</c> sampled and found entirely null.
    /// </para>
    /// <para>
    /// <b>It is not, and the reason is one line up.</b>
    /// <see cref="GeometryWidth.PerFeatureBytes"/> returns the column average only when it
    /// is <c>&gt; 0</c>, falls to the relation size only when both operands are positive,
    /// and returns <c>null</c> for anything that would round below one byte a feature. It
    /// can therefore answer <c>null</c> or at least <c>1</c> and nothing between, so
    /// <c>RowLimit</c>'s guard is unreachable through this type.
    /// </para>
    /// <para>
    /// <b>So this asserts the normalisation rather than the guard</b> — that a zero average
    /// reaches the ceiling by being turned into <em>no statistic</em>, which is the third
    /// absence beside <c>reltuples = -1</c> and a source that keeps none. The guard stays as
    /// defence for a caller that builds the width itself, and this remark is the record that
    /// it is defence rather than a live branch, so nobody deletes it as dead code or writes a
    /// second test for a state that cannot occur.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_width_of_zero_is_drawn_to_the_row_ceiling_rather_than_dividing_by_it()
    {
        int limit = CompositionPreview.RowLimit(
            Ceiling,
            CompositionPreview.DefaultGeometryBudgetBytes,
            new GeometryWidth(0, 0, 8192));

        Assert.Equal(Ceiling, limit);
    }

    /// <summary>
    /// A source that keeps no statistics at all is drawn to the row ceiling too.
    /// </summary>
    /// <remarks>
    /// <b>Null is a different absence from a table with no rows analysed, and it arrives by a
    /// different door.</b> Only PostGIS answers <see cref="IGeometryStatistics"/> today; a
    /// provider that does not, or a source the statistic could not be read from, hands back
    /// nothing at all. Both absences mean the same thing here, and asserting them separately is
    /// what keeps a future null from finding an unwritten branch.
    /// </remarks>
    [Fact]
    public void A_source_that_keeps_no_statistics_is_drawn_to_the_row_ceiling()
    {
        Assert.Equal(
            Ceiling,
            CompositionPreview.RowLimit(
                Ceiling, CompositionPreview.DefaultGeometryBudgetBytes, null));
    }

    /// <summary>
    /// An indexed but unanalysed table is bounded by the relation's size instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>CREATE INDEX</c> sets the row count and no column statistics whatever</b>, which is
    /// the state a layer sits in between being imported and being analysed. Measured: a 500-row
    /// table read −1 rows before the index, 500 rows and no <c>avg_width</c> after it, and
    /// <c>avg_width</c> only after <c>ANALYZE</c>.
    /// </para>
    /// <para>
    /// <b>The fallback is inflated and that is the direction to be wrong in.</b> It counts
    /// indexes and every other column — 278 bytes a row here against a true 120 — so it bounds
    /// more tightly than the truth rather than less. What it does do is separate the classes:
    /// 240–426 bytes a row for the corpus's simple tables against 8,259–8,421 for the dense
    /// ones, which is the distinction the budget is being asked to make.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_indexed_but_unanalysed_table_is_measured_by_its_relation_size()
    {
        GeometryWidth width = new(500, null, 139264);

        Assert.Equal(278, width.PerFeatureBytes);
    }

    /// <summary>
    /// A budget of zero is the behaviour of every build before this one.
    /// </summary>
    /// <remarks>
    /// <b>Because a false refusal is a real cost and somebody may not want to pay it.</b> The
    /// gate withholds features that would have been free whenever a table's large geometries
    /// happen to sit outside the window the query reads — measured at 1,861 features on one such
    /// layer — so an operator who would rather wait than see part of a layer can say so.
    /// </remarks>
    [Fact]
    public void A_budget_of_zero_leaves_the_row_ceiling_alone()
    {
        Assert.Equal(
            Ceiling,
            CompositionPreview.RowLimit(
                Ceiling, 0, new GeometryWidth(16000, DensePolygonBytes, 132145152)));
    }

    /// <summary>
    /// A layer whose average feature outweighs the whole budget still draws one.
    /// </summary>
    /// <remarks>
    /// <b>Zero features and an empty table are the same picture, and they are not the same
    /// thing.</b> One feature plus the notice over the map is a truthful drawing; a blank frame
    /// with a notice is a drawing of nothing that claims to be a sample of something.
    /// </remarks>
    [Fact]
    public void A_feature_wider_than_the_budget_still_draws_one()
    {
        Assert.Equal(
            1,
            CompositionPreview.RowLimit(
                Ceiling, 1024, new GeometryWidth(10, 4 * 1024 * 1024, 41943040)));
    }

    /// <summary>
    /// The budget is a setting, defaulting to 2 MB, read under either product name.
    /// </summary>
    /// <remarks>
    /// <b>ADR-032 §5: the old configuration section is still read.</b> A budget that only
    /// answered to the new name would silently take its default on a deployment that had set it
    /// — which for this setting means silently drawing more than the operator asked for.
    /// </remarks>
    [Fact]
    public void The_geometry_budget_is_a_setting_with_a_two_megabyte_default()
    {
        Assert.Equal(2L * 1024 * 1024, Settings([]).PreviewGeometryBudgetBytes);

        Assert.Equal(
            8L * 1024 * 1024,
            Settings(new() { ["Graticula:PreviewGeometryBudgetMB"] = "8" })
                .PreviewGeometryBudgetBytes);

        Assert.Equal(
            8L * 1024 * 1024,
            Settings(new() { ["GisServer:PreviewGeometryBudgetMB"] = "8" })
                .PreviewGeometryBudgetBytes);

        // Off, which is what a deployment that would rather wait than sample asks for.
        Assert.Equal(
            0,
            Settings(new() { ["Graticula:PreviewGeometryBudgetMB"] = "0" })
                .PreviewGeometryBudgetBytes);
    }

    /// <summary>
    /// A layer the budget cut short is named in the sampling signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wiring, and it is the half arithmetic cannot check.</b> The draw asks the source for
    /// the per-layer limit rather than the shared ceiling — asserted, because a limit computed and
    /// not passed on is a change that measures well and does nothing — and what came back is
    /// compared against that same limit. Comparing against the ceiling instead would draw exactly
    /// this picture and call it complete.
    /// </para>
    /// <para>
    /// <b>One signal for two reasons — CLAUDE.md §2.</b> The row ceiling and the byte budget are
    /// two reasons for one observable fact: this drawing may be part of the layer. A second header
    /// for the second reason would make the screen ask which bound bit, which is a question about
    /// this server rather than about the operator's composition.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_layer_the_budget_cut_short_is_reported_as_a_sample()
    {
        Corpus corpus = new(new GeometryWidth(16000, DensePolygonBytes, 132145152), 16000);

        List<string> sampled = await DrawAsync(corpus);

        Assert.Equal(260, corpus.Asked);

        Assert.Equal(["dense"], sampled);
    }

    /// <summary>
    /// A layer neither bound cut short is not named, and neither is a bound that did not bite.
    /// </summary>
    /// <remarks>
    /// <b>A notice that appears when it should not is one an operator learns to read past.</b>
    /// The simple layer here holds 300 features against a limit of 4,000 — the budget does not
    /// lower it and the ceiling is nowhere near — so the honest report is nothing at all.
    /// </remarks>
    [Fact]
    public async Task A_layer_that_was_drawn_whole_is_not_reported()
    {
        Corpus corpus = new(new GeometryWidth(300, SimplePolygonBytes, 106496), 300);

        List<string> sampled = await DrawAsync(corpus);

        Assert.Equal(Ceiling, corpus.Asked);

        Assert.Empty(sampled);
    }

    // ---------- fixtures ----------

    private static HostSettings Settings(Dictionary<string, string?> values)
    {
        values["Graticula:PlatformStore"] = "Host=localhost;Database=gis";

        // 32 zero bytes, base64: valid AES-256 and obviously not a real key.
        values["Graticula:SecretKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        return HostSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    /// <summary>Draws one layer of a composition and reports what was sampled.</summary>
    private static async Task<List<string>> DrawAsync(Corpus corpus)
    {
        ServiceContexts contexts = new(corpus, new FakeTimeProvider());

        Envelope extent = new(0, 0, 1000, 1000);
        PixelTransform transform = new(extent, 64, 64);

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        MapRenderer renderer = new(canvas, transform, geographic: false);
        renderer.Clear(Rgba.Transparent);

        return await CompositionPreview.DrawAsync(
            contexts,
            renderer,
            transform,
            [Layer("dense")],
            3857,
            Ceiling,
            CompositionPreview.DefaultGeometryBudgetBytes,
            CancellationToken.None);
    }

    private static PublishedLayer Layer(string name) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition(name, "public", name, "geom", 3857, "id", "id", isHosted: false),
            "preview",
            "Host=nowhere",
            GeometryKind.Polygon,
            owner: null,
            SharingScope.Private,
            ServiceStatus.Started);

    /// <summary>
    /// A layer of a known width holding a known number of features.
    /// </summary>
    /// <remarks>
    /// <b>It answers the limit it was given, which is what a real source does.</b> The number the
    /// draw reports back is what the query returned, so a fake that ignored the limit would make
    /// every layer look complete and the assertion about sampling would pass for the wrong
    /// reason.
    /// </remarks>
    private sealed class Corpus(GeometryWidth width, int available) : IServiceSources
    {
        /// <summary>The limit the drawing actually asked the source for.</summary>
        public int? Asked { get; private set; }

        /// <summary>What the planner would say about this layer's geometries.</summary>
        public GeometryWidth Width => width;

        /// <summary>How many features the table actually holds.</summary>
        public int Available => available;

        public IFeatureSource SourceFor(PublishedLayer layer) => new Source(this);

        private sealed class Source(Corpus owner) : IFeatureSource, IGeometryStatistics
        {
            public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
                Task.FromResult(new LayerDescription([], new Envelope(0, 0, 1000, 1000)));

            public Task<GeometryWidth?> GeometryWidthAsync(CancellationToken cancellationToken) =>
                Task.FromResult<GeometryWidth?>(owner.Width);

            public FeatureSchema SchemaFor(FeatureQuery query) => FeatureSchema.Empty;

            public async IAsyncEnumerable<Feature> ReadAsync(
                FeatureQuery query,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                owner.Asked = query.Limit;

                int howMany = Math.Min(query.Limit, owner.Available);

                for (int i = 0; i < howMany; i++)
                {
                    yield return new Feature(
                        (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Square(i % 100),
                        FeatureSchema.Empty,
                        []);
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }

            public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<long> CountUpToAsync(
                FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            private static Polygon Square(int at)
            {
                double x = at * 10;

                return new Polygon(new LinearRing(XySequence.Wrap(
                [
                    x, x,
                    x + 5, x,
                    x + 5, x + 5,
                    x, x + 5,
                    x, x,
                ])));
            }
        }
    }
}
