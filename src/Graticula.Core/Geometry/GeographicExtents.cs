using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Graticula.Geometries;

/// <summary>
/// Layer extents in WGS 84, for the documents that must publish one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, because two surfaces need the same answer and one of them
/// had concluded it was too expensive.</b> WMS has done this since its capabilities
/// document was written — <c>EX_GeographicBoundingBox</c> is mandatory there, so it had
/// to. WFS's <c>ows:WGS84BoundingBox</c> is optional, so that writer omitted the box for
/// every layer not already in 4326 and [Q-125](../../../docs/open-questions.md) recorded
/// the reason as <em>projecting is one round trip per layer</em>. It is not, and the
/// code that proves it was in the next assembly along.
/// </para>
/// <para>
/// <b>Batched by source CRS, which is what makes it cheap.</b>
/// <see cref="IProjector"/> takes a list, so a thousand layers over a handful of
/// references cost a handful of calls rather than a thousand. A deployment uses few
/// references and they do not change.
/// </para>
/// <para>
/// <b>Four corners, and it is an approximation.</b> A rectangle is not a rectangle
/// after projection, so the true geographic extent of a projected box can bulge past
/// its corners. Every WMS in existence does this and the error is smaller than the
/// extent it describes; projecting the boundary densely would be exact and would cost a
/// hundred times as much for a hint in a listing.
/// </para>
/// <para>
/// <b>A reference this deployment cannot transform costs that layer its box and
/// nothing else.</b> One unusual layer must not make the whole server look absent to
/// every client that asks what it has.
/// </para>
/// </remarks>
public static class GeographicExtents
{
    /// <summary>WGS 84, which both documents are defined in terms of.</summary>
    public const int Wgs84 = 4326;

    /// <summary>
    /// Projects each extent into WGS 84, in as few calls as there are references.
    /// </summary>
    /// <param name="projector">Where projection happens.</param>
    /// <param name="extents">Each layer's extent in its own CRS, with that CRS.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>
    /// One entry per input, in the same order: the extent in WGS 84, or null where the
    /// input was empty or its reference could not be transformed.
    /// </returns>
    public static async Task<IReadOnlyList<Envelope?>> InWgs84Async(
        IProjector projector,
        IReadOnlyList<(int Srid, Envelope? Extent)> extents,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(extents);

        Envelope?[] answer = new Envelope?[extents.Count];
        Dictionary<int, List<int>> bySrid = [];

        for (int i = 0; i < extents.Count; i++)
        {
            (int srid, Envelope? extent) = extents[i];

            if (extent is not { IsEmpty: false })
            {
                continue;
            }

            if (srid == Wgs84)
            {
                // Already there. Projecting 4326 to 4326 is a round trip to be told
                // what we sent.
                answer[i] = extent;
                continue;
            }

            if (!bySrid.TryGetValue(srid, out List<int>? group))
            {
                group = [];
                bySrid[srid] = group;
            }

            group.Add(i);
        }

        foreach ((int srid, List<int> indices) in bySrid)
        {
            List<Geometry> corners = new(indices.Count * 4);

            foreach (int index in indices)
            {
                Envelope box = extents[index].Extent!.Value;

                corners.Add(new Point(box.MinX, box.MinY));
                corners.Add(new Point(box.MaxX, box.MinY));
                corners.Add(new Point(box.MaxX, box.MaxY));
                corners.Add(new Point(box.MinX, box.MaxY));
            }

            IReadOnlyList<Geometry> projected;

            try
            {
                (projected, _) = await projector
                    .ProjectAsync(corners, srid, Wgs84, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }

            for (int i = 0; i < indices.Count; i++)
            {
                Envelope whole = Envelope.Empty;

                for (int corner = 0; corner < 4; corner++)
                {
                    int at = (i * 4) + corner;

                    if (at >= projected.Count)
                    {
                        break;
                    }

                    Envelope point = projected[at].Envelope;
                    whole = whole.IsEmpty ? point : whole.Union(point);
                }

                if (!whole.IsEmpty)
                {
                    answer[indices[i]] = whole;
                }
            }
        }

        return answer;
    }
}
