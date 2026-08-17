using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometries;

public sealed class EnvelopeTests
{
    [Fact]
    public void Reversed_bounds_are_ordered()
    {
        Envelope envelope = new(10, 20, 0, 5);

        Assert.Equal(0, envelope.MinX);
        Assert.Equal(5, envelope.MinY);
        Assert.Equal(10, envelope.MaxX);
        Assert.Equal(20, envelope.MaxY);
    }

    [Fact]
    public void Empty_is_not_a_point_at_the_origin()
    {
        // default(Envelope) has all-zero bounds. It must not read as a valid
        // envelope containing (0,0), because that would make trivial-accept
        // silently wrong for anything near the origin.
        Assert.True(Envelope.Empty.IsEmpty);
        Assert.False(Envelope.Empty.Intersects(new Envelope(0, 0, 1, 1)));
        Assert.False(Envelope.Empty.Contains(new Envelope(0, 0, 0, 0)));
        Assert.NotEqual(new Envelope(0, 0, 0, 0), Envelope.Empty);
    }

    [Fact]
    public void Union_with_empty_is_the_identity()
    {
        Envelope envelope = new(1, 2, 3, 4);

        Assert.Equal(envelope, envelope.Union(Envelope.Empty));
        Assert.Equal(envelope, Envelope.Empty.Union(envelope));
        Assert.True(Envelope.Empty.Union(Envelope.Empty).IsEmpty);
    }

    [Fact]
    public void Union_covers_both()
    {
        Envelope union = new Envelope(0, 0, 1, 1).Union(new Envelope(5, -2, 6, 0));

        Assert.Equal(new Envelope(0, -2, 6, 1), union);
    }

    [Theory]
    [InlineData(2, 2, 3, 3, true)]     // fully inside
    [InlineData(0, 0, 10, 10, true)]   // identical
    [InlineData(10, 10, 12, 12, true)] // touching at a corner
    [InlineData(11, 11, 12, 12, false)]
    [InlineData(-5, -5, -1, -1, false)]
    public void Intersects_includes_edges(double minX, double minY, double maxX, double maxY, bool expected)
    {
        Envelope tile = new(0, 0, 10, 10);

        Assert.Equal(expected, tile.Intersects(new Envelope(minX, minY, maxX, maxY)));
    }

    [Theory]
    [InlineData(2, 2, 3, 3, true)]
    [InlineData(0, 0, 10, 10, true)]   // edges count as contained
    [InlineData(0, 0, 11, 10, false)]
    [InlineData(-1, 0, 5, 5, false)]
    public void Contains_is_the_trivial_accept_test(double minX, double minY, double maxX, double maxY, bool expected)
    {
        // benchmarks/mvt-generation finding 2: most features in a dense tile are
        // wholly inside it, where the right answer is this comparison and no
        // clipping at all. That is where the 63x came from.
        Envelope tile = new(0, 0, 10, 10);

        Assert.Equal(expected, tile.Contains(new Envelope(minX, minY, maxX, maxY)));
    }

    [Fact]
    public void Of_a_sequence_finds_the_extremes()
    {
        Envelope envelope = Envelope.Of(XySequence.Wrap([5, 5, -1, 9, 3, -4]));

        Assert.Equal(new Envelope(-1, -4, 5, 9), envelope);
    }

    [Fact]
    public void Of_an_empty_sequence_is_empty()
    {
        Assert.True(Envelope.Of(XySequence.Empty).IsEmpty);
    }

    [Fact]
    public void Expand_grows_all_four_sides()
    {
        Envelope expanded = new Envelope(0, 0, 10, 10).Expand(64);

        Assert.Equal(new Envelope(-64, -64, 74, 74), expanded);
        Assert.True(Envelope.Empty.Expand(64).IsEmpty);
    }

    [Fact]
    public void Width_and_height_are_zero_for_a_degenerate_envelope()
    {
        Envelope vertical = new(5, 0, 5, 10);

        Assert.Equal(0, vertical.Width);
        Assert.Equal(10, vertical.Height);
        Assert.Equal(0, Envelope.Empty.Width);
    }
}
