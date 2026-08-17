using System;
using System.Linq;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometries;

public sealed class XySequenceTests
{
    private static readonly double[] Triangle = [0, 0, 4, 0, 4, 3, 0, 0];

    [Fact]
    public void Empty_holds_nothing_and_does_not_throw()
    {
        XySequence empty = XySequence.Empty;

        Assert.Equal(0, empty.Count);
        Assert.True(empty.IsEmpty);
        Assert.True(empty.AsSpan().IsEmpty);
        Assert.Empty(empty.ToInterleavedArray());
    }

    [Fact]
    public void Wrap_reads_interleaved_ordinates()
    {
        XySequence sequence = XySequence.Wrap(Triangle);

        Assert.Equal(4, sequence.Count);
        Assert.Equal(4, sequence.X(1));
        Assert.Equal(0, sequence.Y(1));
        Assert.Equal(4, sequence.X(2));
        Assert.Equal(3, sequence.Y(2));
    }

    [Fact]
    public void Wrap_rejects_an_odd_length_buffer()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => XySequence.Wrap([1, 2, 3]));

        Assert.Contains("even length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Slice_is_a_view_and_copies_nothing()
    {
        // The property that makes multi-ring geometry free to walk: a slice must
        // read through to the original buffer, not clone it.
        double[] buffer = Triangle.ToArray();
        XySequence middle = XySequence.Wrap(buffer).Slice(1, 2);

        Assert.Equal(2, middle.Count);
        Assert.Equal(4, middle.X(0));

        buffer[2] = 99;

        Assert.Equal(99, middle.X(0));
    }

    [Fact]
    public void Slice_of_a_slice_composes()
    {
        XySequence inner = XySequence.Wrap(Triangle).Slice(1, 3).Slice(1, 2);

        Assert.Equal(2, inner.Count);
        Assert.Equal(4, inner.X(0));
        Assert.Equal(3, inner.Y(0));
        Assert.Equal(0, inner.X(1));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(3, 2)]
    public void Slice_rejects_a_range_outside_the_sequence(int start, int count)
    {
        XySequence sequence = XySequence.Wrap(Triangle);

        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Slice(start, count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Indexing_outside_the_sequence_throws(int index)
    {
        XySequence sequence = XySequence.Wrap(Triangle);

        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.X(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Y(index));
    }

    [Fact]
    public void AsSpan_covers_only_the_view()
    {
        ReadOnlySpan<double> span = XySequence.Wrap(Triangle).Slice(1, 2).AsSpan();

        Assert.Equal(4, span.Length);
        Assert.Equal([4, 0, 4, 3], span.ToArray());
    }

    [Fact]
    public void Equality_compares_coordinates_not_buffers()
    {
        XySequence left = XySequence.Wrap([1, 2, 3, 4]);
        XySequence right = XySequence.Wrap([9, 9, 1, 2, 3, 4]).Slice(1, 2);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Sequences_of_different_length_are_not_equal()
    {
        XySequence shorter = XySequence.Wrap([1, 2]);
        XySequence longer = XySequence.Wrap([1, 2, 3, 4]);

        Assert.NotEqual(shorter, longer);
        Assert.True(shorter != longer);
    }

    [Fact]
    public void A_default_sequence_behaves_as_empty()
    {
        // default(XySequence) reaches a null buffer. It must not be a landmine.
        XySequence uninitialised = default;

        Assert.True(uninitialised.IsEmpty);
        Assert.Equal(XySequence.Empty, uninitialised);
        Assert.Equal(0, uninitialised.GetHashCode());
        Assert.Throws<ArgumentOutOfRangeException>(() => uninitialised.X(0));
    }
}
