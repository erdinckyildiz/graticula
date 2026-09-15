using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The server's own warnings and errors are kept, newest first and bounded, and information is not.
/// </summary>
/// <remarks>
/// Written 2026-09-15 with <c>/admin/logs/server</c> — ADR-045 §5a.
/// </remarks>
public sealed class ServerLogBufferTests
{
    private static void Write(ILogger log, LogLevel level, string message, Exception? exception = null) =>
        log.Log(level, default, message, exception, (text, _) => text);

    [Fact]
    public void Warnings_and_errors_are_kept_newest_first_and_information_is_not()
    {
        ServerLogBuffer buffer = new(TimeProvider.System);
        ILogger log = buffer.CreateLogger("tiles");

        Write(log, LogLevel.Information, "fine");
        Write(log, LogLevel.Warning, "slow tile");
        Write(log, LogLevel.Error, "tile failed", new InvalidOperationException("broken"));

        IReadOnlyList<ServerLogEntry> all = buffer.Read(LogLevel.Warning, null, null, null, null, 100);

        Assert.Equal(["tile failed", "slow tile"], all.Select(e => e.Message));
        Assert.Equal("InvalidOperationException: broken", all[0].Exception);
        Assert.Equal("tiles", all[0].Category);

        Assert.Equal(["tile failed"], buffer.Read(LogLevel.Error, null, null, null, null, 100).Select(e => e.Message));
        Assert.Equal(["slow tile"], buffer.Read(LogLevel.Warning, "slow", null, null, null, 100).Select(e => e.Message));
        Assert.Equal(["slow tile"], buffer.Read(LogLevel.Warning, null, null, null, all[0].Cursor, 100).Select(e => e.Message));
    }

    [Fact]
    public void It_keeps_no_more_than_its_capacity_and_drops_the_oldest()
    {
        ServerLogBuffer buffer = new(TimeProvider.System);
        ILogger log = buffer.CreateLogger("flood");

        for (int i = 0; i < ServerLogBuffer.Capacity + 10; i++)
        {
            Write(log, LogLevel.Warning, $"warning {i}");
        }

        IReadOnlyList<ServerLogEntry> newest = buffer.Read(LogLevel.Warning, null, null, null, null, 500);

        Assert.Equal($"warning {ServerLogBuffer.Capacity + 9}", newest[0].Message);
        Assert.Empty(buffer.Read(LogLevel.Warning, null, null, null, 11, 500));
    }
}
