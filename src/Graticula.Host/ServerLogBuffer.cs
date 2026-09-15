using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// The warnings and errors this process has logged, kept in memory for <c>/admin/logs/server</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-15, after a review from an ArcGIS user's side.</b> ArcGIS Server Manager's
/// log page lists SEVERE and WARNING messages with their source; this server's Logs had the audit
/// trail, the requests and the studio viewer, and nowhere to read what the server itself had
/// complained about. That same day an attachment upload failed with a 503 whose cause — a foreign
/// key — was in the process's own output and nowhere an administrator could reach from the console.
/// </para>
/// <para>
/// <b>In memory, per process, and bounded — deliberately not a fourth table.</b> ADR-045 decided the
/// store keeps three logs and argued the write load of each. A server's warnings are most useful for
/// the last hours of the process that wrote them, and a flood of them is exactly when a write per
/// line to the database would hurt most. So this keeps the most recent <see cref="Capacity"/>, loses
/// them at restart, and says both where it is read. ADR-045 §5a records it.
/// </para>
/// <para>
/// <b>Warning and above only.</b> Information is the request log's and the framework's chatter; a
/// buffer of it would push out the one error somebody came to find.
/// </para>
/// </remarks>
internal sealed class ServerLogBuffer : ILoggerProvider
{
    /// <summary>How many entries are kept.</summary>
    public const int Capacity = 2_000;

    private readonly object _gate = new();
    private readonly Queue<ServerLogEntry> _entries = new();
    private readonly TimeProvider _time;
    private long _sequence;

    /// <summary>Creates the buffer.</summary>
    /// <param name="time">The clock.</param>
    public ServerLogBuffer(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>When this buffer began keeping entries, which is when the process started.</summary>
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    /// <summary>Entries newest first, filtered.</summary>
    /// <param name="minimum">The lowest level to return.</param>
    /// <param name="text">Text the message, category or exception must contain, or null.</param>
    /// <param name="from">The earliest moment, or null.</param>
    /// <param name="to">The latest moment, or null.</param>
    /// <param name="before">Only entries older than this cursor, or null.</param>
    /// <param name="limit">The most to return.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<ServerLogEntry> Read(
        LogLevel minimum, string? text, DateTimeOffset? from, DateTimeOffset? to, long? before, int limit)
    {
        ServerLogEntry[] copy;

        lock (_gate)
        {
            copy = [.. _entries];
        }

        return
        [
            .. copy.AsEnumerable()
                .Reverse()
                .Where(e => e.Level >= minimum)
                .Where(e => before is null || e.Cursor < before)
                .Where(e => from is null || e.At >= from)
                .Where(e => to is null || e.At <= to)
                .Where(e => text is null
                    || e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || e.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (e.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(Math.Clamp(limit, 1, 500)),
        ];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private void Add(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        ServerLogEntry entry = new(
            Interlocked.Increment(ref _sequence),
            _time.GetUtcNow(),
            level,
            category,
            eventId.Id,
            eventId.Name,
            message,
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}");

        lock (_gate)
        {
            _entries.Enqueue(entry);

            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    private sealed class Writer(ServerLogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            buffer.Add(logLevel, category, eventId, formatter(state, exception), exception);
        }
    }
}

/// <summary>One warning or error the process logged.</summary>
/// <param name="Cursor">Its place in the process's sequence, for paging back.</param>
/// <param name="At">When.</param>
/// <param name="Level">How serious.</param>
/// <param name="Category">What logged it.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="EventName">The event's name, when it has one.</param>
/// <param name="Message">The message.</param>
/// <param name="Exception">The exception's type and message, when there was one.</param>
internal sealed record ServerLogEntry(
    long Cursor,
    DateTimeOffset At,
    LogLevel Level,
    string Category,
    int EventId,
    string? EventName,
    string Message,
    string? Exception);
