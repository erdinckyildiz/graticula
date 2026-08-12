namespace GisBench;

/// <summary>
/// Web Mercator tile arithmetic and tile-space transformation.
///
/// This is Tier 1 hot-path code in the architecture's terms
/// (docs/build-vs-adopt-policy.md §4) — the primitives we own rather than
/// delegate. It is deliberately allocation-light: this runs thousands of times
/// per tile and A-019 is about whether that is affordable.
/// </summary>
public static class TileMath
{
    /// <summary>Web Mercator half-extent, metres.</summary>
    public const double WorldExtent = 20037508.342789244;

    /// <summary>MVT internal coordinate space. 4096 is the near-universal choice.</summary>
    public const int Extent = 4096;

    /// <summary>
    /// Buffer in tile units. Geometry is clipped to the tile plus this margin so
    /// that lines and polygon edges crossing the boundary render without seams.
    /// 64 matches what ST_AsMVTGeom is normally called with, which keeps
    /// endpoint B and endpoint C comparable.
    /// </summary>
    public const int Buffer = 64;

    public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    /// <summary>Tile envelope in EPSG:3857, matching PostGIS ST_TileEnvelope.</summary>
    public static Bounds TileEnvelope(int z, int x, int y)
    {
        double size = (WorldExtent * 2.0) / (1 << z);
        double minX = -WorldExtent + x * size;
        double maxY = WorldExtent - y * size;
        return new Bounds(minX, maxY - size, minX + size, maxY);
    }

    /// <summary>
    /// The envelope expanded by <see cref="Buffer"/> tile units, in map units.
    /// This is what we actually query and clip against.
    /// </summary>
    public static Bounds BufferedEnvelope(in Bounds b)
    {
        double perUnit = b.Width / Extent;
        double pad = Buffer * perUnit;
        return new Bounds(b.MinX - pad, b.MinY - pad, b.MaxX + pad, b.MaxY + pad);
    }

    /// <summary>
    /// Map coordinate to tile coordinate. MVT places the origin at the tile's
    /// top-left with y increasing downward, so y is inverted here.
    /// Values are integers by construction — this is the quantisation step, and
    /// it is lossy, which is why docs/geometry-crs-policy.md forbids treating a
    /// tile-derived geometry as a write source.
    /// </summary>
    public static (int X, int Y) ToTileSpace(double mapX, double mapY, in Bounds tile)
    {
        double sx = Extent / tile.Width;
        double sy = Extent / tile.Height;
        int tx = (int)Math.Round((mapX - tile.MinX) * sx);
        int ty = (int)Math.Round((tile.MaxY - mapY) * sy);
        return (tx, ty);
    }
}
