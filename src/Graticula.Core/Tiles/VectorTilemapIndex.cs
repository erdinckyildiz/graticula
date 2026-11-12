using System;
using System.Collections.Generic;

namespace Graticula.Tiles;

/// <summary>
/// The quadtree a vector tile service's <c>tilemap</c> answers when it is asked for no block: which tiles exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format is Esri's, from the public REST API page <i>Vector Tilemap</i>.</b> <c>GET …/VectorTileServer/tilemap</c>
/// answers <c>{"index": [...]}</c>. The root, level 0, covers the whole extent, and <c>index</c> lists the root's four
/// children in the order north-west, north-east, south-west, south-east. Each is <c>1</c> (the tile exists and has no
/// children, so a client over-zooms it), <c>0</c> (no tile), an array of its own four children (the tile exists and they
/// are described) or <c>2</c> (the subtree is in a separate index file — never written here). The page's example is
/// <c>{"index":[[1,0,1,1],0,0,0]}</c>. A tile is addressed level, column, row on a 2^level grid; the quadrant order is
/// row first, so the north-east child of a tile at column c and row r is column 2c + 1, row 2r.
/// </para>
/// <para>
/// <b>Bounded, and that is a trade.</b> A service with data over a country has a pyramid of millions of tiles at its
/// deepest levels, and an answer that described them would be megabytes that a client must read before it draws
/// anything. So the expansion stops at the deepest level whose description fits <see cref="NodeBudget"/> entries and
/// writes <c>1</c> there for every tile that may hold data. A client then over-zooms that level, which renders correctly
/// because a vector tile scales; what is lost is the precision of the deep levels — a tile a level or two below the stop
/// may be empty and the index will not say so. <b>The block form, <c>tilemap/{level}/{row}/{column}/{width}/{height}</c>,
/// stays exact</b> and is what to ask when that precision matters.
/// </para>
/// <para>
/// <b>Only a halving grid has a quadtree.</b> <see cref="IsQuadtree"/> is false for a custom scheme whose levels do not
/// double, and nothing here describes one.
/// </para>
/// </remarks>
public static class VectorTilemapIndex
{
    /// <summary>
    /// The most entries — each a <c>0</c>, a <c>1</c> or an array — the live route's answer holds: 20,000, a few
    /// hundred kilobytes of JSON at most.
    /// </summary>
    /// <remarks>
    /// An entry count and not a size, because that is what the expansion can know before it writes. INFERRED to be small
    /// enough for Pro and Field Maps to read at once; Esri states no limit. A level whose full description would pass
    /// it is not described, and the level above is the one that ends in <c>1</c>.
    /// </remarks>
    public const int NodeBudget = 20_000;

    /// <summary>
    /// The most entries the index inside an exported package holds: 2,000,000.
    /// </summary>
    /// <remarks>
    /// A package is read from a file once and carries tiles at exactly the levels it was asked for, so cutting its index
    /// short would make a client over-zoom a coarser tile where the package holds a finer one. The export's own tile
    /// ceiling keeps a package well inside this; it is a guard on memory, not a design limit.
    /// </remarks>
    public const int PackageNodeBudget = 2_000_000;

    private static readonly object Nothing = 0;
    private static readonly object Leaf = 1;

    /// <summary>Whether a scheme's levels double, so that a tile has exactly four children.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>True when every level is 2^level tiles across.</returns>
    public static bool IsQuadtree(VectorTileScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        for (int level = 0; level <= scheme.MaxLevel; level++)
        {
            if (scheme.TilesAcross(level) != (1L << level))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The index of a tile pyramid, to the deepest level that fits the budget.</summary>
    /// <param name="maxLevel">The finest level that has tiles.</param>
    /// <param name="mayHold">Whether the tile at a level, column and row exists — may hold something. An ancestor of a tile that does must say so too.</param>
    /// <param name="nodeBudget">The most entries to write.</param>
    /// <returns>The value of <c>index</c>: the root's four children, north-west, north-east, south-west, south-east.</returns>
    public static object[] Build(int maxLevel, Func<int, int, int, bool> mayHold, int nodeBudget = NodeBudget)
    {
        ArgumentNullException.ThrowIfNull(mayHold);

        // The root has no level to hold children at.
        if (maxLevel < 1)
        {
            return [Nothing, Nothing, Nothing, Nothing];
        }

        // Find the deepest level whose description fits. Describing down to level L writes four entries for the root and
        // for every tile of levels 1 to L-1 that may hold data, so each level is counted from the one above it.
        long arrays = 1;
        List<(int X, int Y)> frontier = [(0, 0)];
        int stop = 1;

        for (int level = 1; level <= maxLevel; level++)
        {
            if (4 * arrays > nodeBudget && level > 1)
            {
                break;
            }

            stop = level;

            if (level == maxLevel)
            {
                break;
            }

            List<(int X, int Y)> next = [];

            foreach ((int x, int y) in frontier)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int cx = (2 * x) + dx;
                        int cy = (2 * y) + dy;

                        if (mayHold(level, cx, cy))
                        {
                            next.Add((cx, cy));
                        }
                    }
                }
            }

            arrays += next.Count;
            frontier = next;
        }

        return Children(0, 0, 0, stop, mayHold);
    }

    private static object[] Children(int level, int x, int y, int stop, Func<int, int, int, bool> mayHold)
    {
        object[] four = new object[4];
        int at = 0;

        // Row first, then column: north-west, north-east, south-west, south-east.
        for (int dy = 0; dy < 2; dy++)
        {
            for (int dx = 0; dx < 2; dx++)
            {
                int cx = (2 * x) + dx;
                int cy = (2 * y) + dy;
                int child = level + 1;

                four[at++] = !mayHold(child, cx, cy)
                    ? Nothing
                    : child >= stop ? Leaf : Children(child, cx, cy, stop, mayHold);
            }
        }

        return four;
    }
}
