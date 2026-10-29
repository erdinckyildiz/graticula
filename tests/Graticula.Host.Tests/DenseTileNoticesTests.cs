using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Graticula.Platform.Admin;
using Graticula.Tiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-267, owner decision 2026-10-29: a tile drawn with more features than a visible range is measured for is noted on
/// its layer, which suggests the range again; the range itself is never changed.
/// </summary>
/// <remarks>
/// <b>Against hand-encoded tiles</b>, because the parts are encoded by the database (<c>ST_AsMVT</c>) and the count has
/// to read what the specification says, not what one encoder happens to write. What the catalogue stores is
/// <c>Graticula.Platform.Postgres.Tests</c>'s; the route and the console are the conformance and console suites'.
/// </remarks>
public sealed class DenseTileNoticesTests
{
    private static readonly Guid Layer = Guid.NewGuid();
    private static readonly TileAddress Address = new(6, 37, 24);

    [Fact]
    public void FeaturesAreCountedAcrossLayersPastEveryOtherField()
    {
        byte[] tile = Tile(LayerMessage("a", 3), LayerMessage("b", 4));

        Assert.Equal(7, DenseTileNotices.CountFeatures(tile));
    }

    [Fact]
    public void ABufferCutShortCountsWhatItRead()
    {
        byte[] tile = Tile(LayerMessage("a", 5));

        // Cut inside the layer: its length now runs past the end, so nothing of it is trusted.
        Assert.Equal(0, DenseTileNotices.CountFeatures(tile.AsSpan(0, tile.Length - 3)));
        Assert.Equal(0, DenseTileNotices.CountFeatures([]));
    }

    [Fact]
    public async Task ATileWithinTheMeasureIsNotNoted()
    {
        List<DenseTile> notes = [];
        DenseTileNotices notices = Notices(notes);

        Assert.False(await notices.LookAsync(Layer, "buildings", Address, Tile(LayerMessage("buildings", 10_000)), default));
        Assert.Empty(notes);
    }

    [Fact]
    public async Task ADenseTileIsNotedOnceAndADenserOneAgain()
    {
        List<DenseTile> notes = [];
        DenseTileNotices notices = Notices(notes);
        byte[] dense = Tile(LayerMessage("buildings", 10_001));

        Assert.True(await notices.LookAsync(Layer, "buildings", Address, dense, default));
        Assert.False(await notices.LookAsync(Layer, "buildings", new TileAddress(6, 38, 24), dense, default));
        Assert.True(await notices.LookAsync(Layer, "buildings", Address, Tile(LayerMessage("buildings", 12_000)), default));

        Assert.Equal([10_001, 12_000], notes.ConvertAll(n => n.Features));
        Assert.Equal((6, 37, 24), (notes[0].Level, notes[0].X, notes[0].Y));
        Assert.False(notes[0].Kept);
    }

    [Fact]
    public async Task ARangeSetOrKeptIsForgottenSoTheNextDenseTileIsNoted()
    {
        List<DenseTile> notes = [];
        DenseTileNotices notices = Notices(notes);
        byte[] dense = Tile(LayerMessage("buildings", 10_500));

        await notices.LookAsync(Layer, "buildings", Address, dense, default);
        notices.Forget(Layer);
        await notices.LookAsync(Layer, "buildings", Address, dense, default);

        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public async Task ANoteThatCannotBeKeptLeavesTheTileServed()
    {
        DenseTileNotices notices = new(
            (_, _, _) => throw new InvalidOperationException("the catalogue is down"), NullLogger.Instance);

        Assert.False(await notices.LookAsync(Layer, "buildings", Address, Tile(LayerMessage("buildings", 10_001)), default));
    }

    private static DenseTileNotices Notices(List<DenseTile> notes) =>
        new((_, tile, _) =>
        {
            notes.Add(tile);
            return Task.FromResult(true);
        }, NullLogger.Instance);

    /// <summary>A tile: its layers as field 3.</summary>
    private static byte[] Tile(params byte[][] layers)
    {
        using MemoryStream tile = new();

        foreach (byte[] layer in layers)
        {
            Field(tile, 3, layer);
        }

        return tile.ToArray();
    }

    /// <summary>A layer as the specification writes it: version, name, features, a key and a value.</summary>
    private static byte[] LayerMessage(string name, int features)
    {
        using MemoryStream layer = new();

        Varint(layer, (15 << 3) | 0);
        Varint(layer, 2);
        Field(layer, 1, System.Text.Encoding.UTF8.GetBytes(name));

        for (int i = 0; i < features; i++)
        {
            // id, type POINT, geometry MoveTo(1) 1,1
            Field(layer, 2, [0x08, (byte)(1 + (i % 100)), 0x18, 0x01, 0x22, 0x03, 0x09, 0x02, 0x02]);
        }

        Field(layer, 3, "height"u8.ToArray());
        Field(layer, 4, [0x11, 0, 0, 0, 0, 0, 0, 0x24, 0x40]);
        Varint(layer, (5 << 3) | 0);
        Varint(layer, 4096);

        return layer.ToArray();
    }

    private static void Field(MemoryStream to, int field, byte[] body)
    {
        Varint(to, (ulong)((field << 3) | 2));
        Varint(to, (ulong)body.Length);
        to.Write(body);
    }

    private static void Varint(MemoryStream to, ulong value)
    {
        while (value >= 0x80)
        {
            to.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        to.WriteByte((byte)value);
    }
}
