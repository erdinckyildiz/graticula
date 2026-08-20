using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// Decides which labels get drawn when they cannot all fit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Greedy, in draw order, and that choice is the whole algorithm.</b> Each
/// candidate is accepted if its box touches nothing already accepted, and rejected
/// otherwise. It is not optimal — a better placement exists for almost every dense
/// map — and it is deterministic, which matters more: the same request twice
/// produces the same image, and a client caching one is not caching a coin toss.
/// </para>
/// <para>
/// <b>What this does not do, stated rather than discovered.</b> There is no
/// alternative-position search, no curved placement along a line, no priority
/// between layers beyond draw order, and **no consistency between one request and
/// the next**. [ADR-004](../../../docs/adr/ADR-004-rendering-engine.md) §0
/// predicted that last one would return the day server-side rendering did, and it
/// has: a client tiling its WMS requests gets each tile placed independently, so a
/// name can appear on one side of a seam and not the other.
/// [Q-26](../../../docs/open-questions.md) reopens for it. The margin
/// <see cref="SymbologyPlan.Margin"/> adds narrows the seam and does not close it.
/// </para>
/// <para>
/// <b>Tier 1 and font-free.</b> Measuring text needs the rasteriser
/// (<see cref="IMapCanvas.MeasureLabel"/>); deciding what to do with the
/// measurement does not. Splitting them there is what lets this be tested with
/// rectangles and no font at all.
/// </para>
/// </remarks>
public sealed class LabelPlacer
{
    /// <summary>
    /// Pixels of clear space required between two labels.
    /// </summary>
    /// <remarks>
    /// <b>Two labels that merely fail to overlap still read as one word.</b> Two
    /// pixels is the smallest gap at which a reader sees two names, measured by
    /// looking rather than derived.
    /// </remarks>
    public const double Separation = 2;

    private readonly List<PixelBox> _taken = [];
    private readonly List<LabelCandidate> _candidates = [];

    /// <summary>How many labels have been offered.</summary>
    public int Offered => _candidates.Count;

    /// <summary>Offers a label for placement.</summary>
    /// <param name="candidate">The label.</param>
    public void Offer(LabelCandidate candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Text);

        _candidates.Add(candidate);
    }

    /// <summary>Forgets everything, so the placer can serve the next request.</summary>
    public void Reset()
    {
        _taken.Clear();
        _candidates.Clear();
    }

    /// <summary>
    /// Draws the labels that fit, in the order they were offered.
    /// </summary>
    /// <remarks>
    /// <b>Drawn here rather than returned, because the rejection needs the
    /// measurement and the measurement needs the canvas.</b> Returning a list would
    /// mean measuring twice or carrying the boxes back out, and the second is the
    /// port growing a type it does not need.
    /// </remarks>
    /// <param name="canvas">Where to draw.</param>
    /// <returns>How many were drawn.</returns>
    public int Draw(IMapCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        int drawn = 0;

        foreach (LabelCandidate candidate in _candidates)
        {
            PixelBox box = canvas
                .MeasureLabel(candidate.Text, candidate.Symbol, candidate.X, candidate.Y)
                .Padded(Separation);

            // Off the image entirely: not drawn, and not allowed to block anything
            // either. A label just outside the frame that reserved space inside it
            // would delete a label that is visible.
            if (box.IsOutside(canvas.Width, canvas.Height))
            {
                continue;
            }

            bool blocked = false;

            foreach (PixelBox taken in _taken)
            {
                if (box.Intersects(taken))
                {
                    blocked = true;
                    break;
                }
            }

            if (blocked)
            {
                continue;
            }

            _taken.Add(box);
            canvas.DrawLabel(candidate.Text, candidate.Symbol, candidate.X, candidate.Y);
            drawn++;
        }

        return drawn;
    }
}
