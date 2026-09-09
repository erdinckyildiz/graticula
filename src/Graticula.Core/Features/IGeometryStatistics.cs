using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>
/// How much geometry a layer holds per feature, as the source's own statistics have it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An estimate the source already keeps, never a measurement taken on demand.</b> Reading the
/// real widths means reading the geometries, which is the cost the caller is trying to bound —
/// so a caller that cannot get an estimate cheaply must go on without one rather than pay for a
/// better one.
/// </para>
/// <para>
/// <b>Every field can be absent, and the absent case is the ordinary one.</b> A table that has
/// had neither <c>CREATE INDEX</c> nor <c>ANALYZE</c> has no row count and no width — which is
/// precisely the freshly loaded layer somebody is most likely to be looking at. See
/// <see cref="PerFeatureBytes"/> for what is answered then.
/// </para>
/// </remarks>
/// <param name="Rows">
/// How many rows the source believes are there, or a negative number when it has never counted.
/// </param>
/// <param name="AverageBytes">
/// The average size of one feature's geometry, or null when nothing has measured it. Raw and
/// uncompressed: it is what the database reads before it can do anything with the shape, which
/// is the number a cost bound wants.
/// </param>
/// <param name="RelationBytes">
/// What the whole relation occupies, indexes and every other column included. Only ever a
/// fallback — see <see cref="PerFeatureBytes"/>.
/// </param>
public readonly record struct GeometryWidth(float Rows, int? AverageBytes, long RelationBytes)
{
    /// <summary>
    /// The best per-feature width this statistic supports, or null when it supports none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The column's own average first, because it is the only figure that describes the
    /// geometry.</b> Measured 2026-09-09 against a generated corpus: exact on uniform data —
    /// 136, 856 and 8,056 bytes for 5-, 50- and 500-vertex polygons — within 0.1% under 100:1
    /// skew, and within −8.8%…+9.7% across eleven analyses of a 120,000-row layer where one row
    /// in a thousand carries 5,000 vertices. That last spread is the sample size showing
    /// through and is the error bar any threshold built on this has to clear.
    /// </para>
    /// <para>
    /// <b>The relation's size over its row count second, and it is a fallback rather than a
    /// second reading.</b> It counts indexes and every other column, so it is inflated —
    /// measured at 1.05× on a dense table and 2.2–3.1× on small simple ones. What it does do is
    /// separate the classes: 240–426 bytes a row for the 5-vertex tables against 8,259–8,421 for
    /// the 500-vertex ones. It exists because <c>CREATE INDEX</c> sets a row count without
    /// setting any column statistics at all, which is a real and common state for a layer that
    /// has just been imported.
    /// </para>
    /// <para>
    /// <b>And null third, which is not a failure.</b> A caller bounding work by this must have
    /// somewhere to go when there is no statistic, and the honest place is whatever it would
    /// have done before the statistic existed.
    /// </para>
    /// </remarks>
    public int? PerFeatureBytes
    {
        get
        {
            if (AverageBytes is > 0)
            {
                return AverageBytes;
            }

            if (Rows <= 0 || RelationBytes <= 0)
            {
                return null;
            }

            double each = RelationBytes / (double)Rows;

            return each >= 1 ? (int)Math.Min(each, int.MaxValue) : null;
        }
    }
}

/// <summary>
/// A source that can say how large its geometries are without reading them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate interface, not a member of <see cref="IFeatureSource"/>, and that follows
/// <see cref="IFeatureVersions"/>'s reasoning exactly.</b> Whether a source keeps statistics is a
/// property of the source: PostgreSQL analyses every column it stores and a file format keeps
/// nothing of the kind. Putting this on the main interface would oblige every future provider to
/// answer a question it may have no answer to, and the honest answers — a null, or a throw — are
/// both worse than not being asked.
/// </para>
/// <para>
/// <b>What it is for.</b> A row count is not a unit of cost. One 500-vertex polygon outweighs
/// sixty 5-vertex ones, and a bound expressed in rows lets the expensive layer through while
/// holding the cheap one back. This is the number that makes a byte bound possible.
/// </para>
/// </remarks>
public interface IGeometryStatistics
{
    /// <summary>
    /// What the source believes about this layer's geometry column.
    /// </summary>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The statistic, or null when the relation itself could not be found.</returns>
    Task<GeometryWidth?> GeometryWidthAsync(CancellationToken cancellationToken);
}
