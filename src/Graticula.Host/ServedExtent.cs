using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// An extent moved into the reference a service is served in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A document that reports the wrong reference is worse than one that reports none.</b>
/// [ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5c lets a service
/// name the reference it answers in; the layer document's <c>extent.spatialReference</c> comes
/// from the table. Measured 2026-09-05 with the query honouring the service and the document
/// left alone: <b>document 3857, query 4326, same layer</b>. A client reads the contract and is
/// handed something else, which is why the serving half was held back until this existed —
/// [D-229](../../docs/architecture-debt.md).
/// </para>
/// <para>
/// <b>Relabelling is not an option, and that is the whole reason this file is here.</b> The
/// numbers in an extent belong to the reference they were measured in; 3857 metres called 4326
/// is a document that is wrong in a way no client can detect. The box is moved, then labelled.
/// </para>
/// <para>
/// <b>A grid, not two corners.</b> A reprojected rectangle is not a rectangle, and at
/// continental widths the corners alone put the edges kilometres inside the data — the same
/// reasoning the raster face uses and the same helper.
/// <see cref="CoverageWarp.ControlPoints" /> samples the box; the answer is what encloses the
/// samples.
/// </para>
/// <para>
/// <b>Null rather than a wrong box.</b> PROJ answers out-of-range coordinates with infinities
/// rather than raising, and every comparison with one passes — an unchecked result is a
/// document confidently reporting an extent nothing is inside. The caller keeps the stored
/// extent and its own reference when this answers null, so the document says what it can prove.
/// </para>
/// </remarks>
internal static class ServedExtent
{
    /// <summary>How many samples a side.</summary>
    /// <remarks>
    /// <b>Enough to catch the bulge, few enough to be one round trip.</b> An extent is four
    /// numbers and the question is only where its edges land, so the raster face's density buys
    /// nothing here.
    /// </remarks>
    private const int Steps = 3;

    /// <summary>The same ground, in another reference.</summary>
    /// <param name="extent">The box, measured in <paramref name="from" />.</param>
    /// <param name="from">The reference it is measured in.</param>
    /// <param name="to">The reference wanted.</param>
    /// <param name="projector">The projector every other face uses.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The box in <paramref name="to" />, or null when it cannot be put there.</returns>
    public static async Task<Envelope?> InAsync(
        Envelope? extent,
        int from,
        int to,
        IProjector projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projector);

        if (extent is not { } box || from == to)
        {
            return extent;
        }

        Point[] outline = CoverageWarp.ControlPoints(box, Steps, Steps, Steps);

        (IReadOnlyList<Geometry> moved, _) = await projector
            .ProjectAsync(outline, from, to, cancellationToken)
            .ConfigureAwait(false);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (Geometry each in moved)
        {
            if (each is not Point point)
            {
                continue;
            }

            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return double.IsFinite(minX) && double.IsFinite(minY)
            && double.IsFinite(maxX) && double.IsFinite(maxY)
            && maxX > minX && maxY > minY
                ? new Envelope(minX, minY, maxX, maxY)
                : null;
    }
}
