using System;
using System.Collections.Generic;
using Graticula.Geometries;

namespace Graticula.Coverages;

/// <summary>
/// What a value in a band is, in the terms a renderer has to reason about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately coarse, and the reasoning is
/// [ADR-013](../../../docs/adr/ADR-013-feature-service-data-model.md)'s for
/// <c>FieldType</c>.</b> A raster's storage type has a dozen spellings across formats
/// and this server makes three decisions with it: how many bytes a sample occupies,
/// whether it is signed, and whether it is an integer. Anything finer would be a
/// vocabulary nobody reads.
/// </para>
/// <para>
/// <b>Named by width and signedness rather than after the CLR types.</b> The obvious
/// spellings — <c>Int16</c>, <c>Float32</c> — are what GDAL uses and what a reader of
/// this file would expect, and CA1720 refuses them because an enum member that shares
/// a type's name reads ambiguously from another language. These say the same thing in
/// the format's own terms.
/// </para>
/// </remarks>
public enum SampleKind
{
    /// <summary>Unsigned 8-bit, which is most imagery.</summary>
    Unsigned8 = 1,

    /// <summary>Signed 16-bit.</summary>
    Signed16 = 2,

    /// <summary>Unsigned 16-bit, which is most scientific imagery.</summary>
    Unsigned16 = 3,

    /// <summary>Signed 32-bit.</summary>
    Signed32 = 4,

    /// <summary>32-bit floating point, which is most continuous data.</summary>
    Real32 = 5,

    /// <summary>64-bit floating point.</summary>
    Real64 = 6,
}

/// <summary>
/// One band of a coverage, and what is known about its values.
/// </summary>
/// <param name="Index">Its position, from zero.</param>
/// <param name="Kind">What a sample is.</param>
/// <param name="NoData">
/// The value that means <em>nothing was measured here</em>, or null when the format
/// declares none.
/// </param>
/// <param name="Minimum">The smallest value, when the file records one.</param>
/// <param name="Maximum">The largest value, when the file records one.</param>
/// <remarks>
/// <b><c>NoData</c> is a rendering decision carried by the reader rather than made by
/// it.</b> A no-data pixel is not black and not white — it is absent, and what a map
/// does about that is cartography. The reader's whole job here is to make sure the
/// number survives the read so that the decision can be made further up.
/// </remarks>
public sealed record BandInfo(
    int Index,
    SampleKind Kind,
    double? NoData,
    double? Minimum,
    double? Maximum);

/// <summary>
/// One resolution of a coverage: the full image, or one of its overviews.
/// </summary>
/// <param name="Index">Zero is the full-resolution image; higher is coarser.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <remarks>
/// <b>The pyramid is why a COG can be served at all</b>, and choosing which level
/// answers a request is Tier 1 — see
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4. A
/// reader reports what levels exist; it does not decide which one a map wants.
/// </remarks>
public sealed record OverviewInfo(int Index, int Width, int Height);

/// <summary>
/// Everything about a coverage that can be known without reading its pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what registration stores and what the service document is built
/// from.</b> [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)
/// §3.3 registers imagery in place, so the file is opened at registration to fill
/// this in and then not again until somebody asks for pixels.
/// </para>
/// <para>
/// <b>The extent is in the coverage's own reference and is not reprojected here.</b>
/// A reader that reprojected would be making a decision — which transformation, at
/// what accuracy — that ADR-043 §3.4 puts on the other side of the line.
/// </para>
/// </remarks>
public sealed class CoverageInfo
{
    /// <summary>Describes a coverage.</summary>
    /// <param name="width">Pixels across, at full resolution.</param>
    /// <param name="height">Pixels down, at full resolution.</param>
    /// <param name="srid">The EPSG code of the coverage's own reference.</param>
    /// <param name="extent">Its bounds, in that reference.</param>
    /// <param name="bands">Its bands, in order.</param>
    /// <param name="overviews">Its resolutions, coarsest last, excluding the full image.</param>
    /// <param name="tileWidth">The width of one stored tile, or zero when striped.</param>
    /// <param name="tileHeight">The height of one stored tile, or zero when striped.</param>
    public CoverageInfo(
        int width,
        int height,
        int srid,
        Envelope extent,
        IReadOnlyList<BandInfo> bands,
        IReadOnlyList<OverviewInfo> overviews,
        int tileWidth,
        int tileHeight)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(overviews);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), "A coverage has a positive size in both directions.");
        }

        if (bands.Count == 0)
        {
            throw new ArgumentException("A coverage has at least one band.", nameof(bands));
        }

        Width = width;
        Height = height;
        Srid = srid;
        Extent = extent;
        Bands = bands;
        Overviews = overviews;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
    }

    /// <summary>Pixels across, at full resolution.</summary>
    public int Width { get; }

    /// <summary>Pixels down, at full resolution.</summary>
    public int Height { get; }

    /// <summary>The EPSG code of the coverage's own reference.</summary>
    public int Srid { get; }

    /// <summary>Its bounds, in its own reference.</summary>
    public Envelope Extent { get; }

    /// <summary>Its bands, in order.</summary>
    public IReadOnlyList<BandInfo> Bands { get; }

    /// <summary>Its coarser resolutions, excluding the full image.</summary>
    /// <remarks>
    /// <b>Empty is legal and is a case the corpus covers.</b> A GeoTIFF with no
    /// pyramid is a valid file that a COG writer would have improved, and a reader
    /// that assumed a pyramid existed would return nothing for it.
    /// </remarks>
    public IReadOnlyList<OverviewInfo> Overviews { get; }

    /// <summary>The width of one stored tile, or zero when the file is striped.</summary>
    public int TileWidth { get; }

    /// <summary>The height of one stored tile, or zero when the file is striped.</summary>
    public int TileHeight { get; }

    /// <summary>How many ground units one pixel spans, west to east.</summary>
    public double PixelWidth => (Extent.MaxX - Extent.MinX) / Width;

    /// <summary>How many ground units one pixel spans, north to south.</summary>
    public double PixelHeight => (Extent.MaxY - Extent.MinY) / Height;
}
