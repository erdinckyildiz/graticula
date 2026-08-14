using System;

namespace GisServer.Tiles;

/// <summary>
/// One tile in the Web Mercator pyramid.
/// </summary>
/// <param name="Z">Zoom level, 0 at the whole world.</param>
/// <param name="X">Column, west to east.</param>
/// <param name="Y">Row, <b>north to south</b>.</param>
/// <remarks>
/// <para>
/// <b>XYZ, not TMS.</b> Row 0 is the north edge. The two schemes differ only by
/// a flipped Y and produce a plausible-looking map that is upside down, which is
/// why the axis direction is written into the parameter documentation rather
/// than assumed.
/// </para>
/// <para>
/// <b>The URL order is a separate question from the struct order.</b> The
/// ArcGIS VectorTileServer path is <c>tile/{z}/{y}/{x}.pbf</c> — row before
/// column — while almost every other tile URL in the world is <c>{z}/{x}/{y}</c>.
/// This type keeps the conventional field order and the endpoint does the
/// swapping, in one place, where it can be seen.
/// </para>
/// </remarks>
public readonly record struct TileAddress(int Z, int X, int Y)
{
    /// <summary>Deepest zoom served.</summary>
    /// <remarks>
    /// <b>22 because that is where Web Mercator stops being useful</b>, not
    /// because anything breaks above it: a z22 tile is roughly 9 metres across
    /// at the equator, below the positional accuracy of most data anybody
    /// publishes. Serving deeper invites a client to seed a pyramid whose leaf
    /// count grows fourfold per level for no additional detail.
    /// </remarks>
    public const int MaxZoom = 22;

    /// <summary>Whether this address exists in the pyramid at all.</summary>
    /// <remarks>
    /// Checked before anything reaches the database. An out-of-range address is
    /// a client error with a cheap answer, and letting it through means
    /// <c>ST_TileEnvelope</c> raises a database error that surfaces as a 500 —
    /// the server blaming itself for the caller's arithmetic.
    /// </remarks>
    public bool IsValid
    {
        get
        {
            if (Z is < 0 or > MaxZoom)
            {
                return false;
            }

            long side = 1L << Z;
            return X >= 0 && X < side && Y >= 0 && Y < side;
        }
    }

    /// <summary>Why this address is not valid, for a message a caller can act on.</summary>
    /// <returns>The reason, or null when the address is fine.</returns>
    public string? Rejection()
    {
        if (Z is < 0 or > MaxZoom)
        {
            return $"Zoom {Z} is outside 0–{MaxZoom}.";
        }

        long side = 1L << Z;

        if (X < 0 || X >= side || Y < 0 || Y >= side)
        {
            return $"Tile {X},{Y} is outside the {side}×{side} grid at zoom {Z}.";
        }

        return null;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Z}/{X}/{Y}");
}
