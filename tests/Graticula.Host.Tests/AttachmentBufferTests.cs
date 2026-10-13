using System;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The bound on attachment bytes held in memory — owner decision 2026-10-13, ADR-013 condition 2.
/// </summary>
public sealed class AttachmentBufferTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void An_attachment_over_the_size_is_streamed_and_one_under_it_is_buffered()
    {
        AttachmentBuffer buffer = new(fileBytes: 8 * MB, totalBytes: 128 * MB);

        Assert.Null(buffer.TryReserve(8 * MB + 1));

        using IDisposable? room = buffer.TryReserve(8 * MB);

        Assert.NotNull(room);
        Assert.Equal(8 * MB, buffer.Held);
    }

    [Fact]
    public void The_total_is_never_passed_and_room_given_back_is_room_again()
    {
        AttachmentBuffer buffer = new(fileBytes: 8 * MB, totalBytes: 20 * MB);

        IDisposable? first = buffer.TryReserve(8 * MB);
        IDisposable? second = buffer.TryReserve(8 * MB);

        Assert.NotNull(first);
        Assert.NotNull(second);

        // 24 MB would pass 20: this one streams.
        Assert.Null(buffer.TryReserve(8 * MB));
        Assert.Equal(16 * MB, buffer.Held);

        first!.Dispose();
        first.Dispose();   // twice gives back once

        Assert.Equal(8 * MB, buffer.Held);
        Assert.NotNull(buffer.TryReserve(8 * MB));
    }

    [Fact]
    public void Zero_turns_buffering_off_and_an_empty_attachment_is_not_buffered()
    {
        Assert.Null(new AttachmentBuffer(fileBytes: 0, totalBytes: 128 * MB).TryReserve(1));
        Assert.Null(new AttachmentBuffer(fileBytes: 8 * MB, totalBytes: 128 * MB).TryReserve(0));
    }
}
