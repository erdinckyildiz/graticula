using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// <b>The server log on disk</b> — D-300: one JSON line per warning or error, a file a day under the state directory,
/// thirty days kept, and the most recent read back into <see cref="ServerLogBuffer"/> when the process starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file and not a fourth table</b>, which ADR-045 §5a decided against for this log: a flood of warnings is when a
/// write per line to the database would hurt most. Lines go through a bounded channel to one writer, so logging never
/// waits on the disk; when the channel is full a line is dropped rather than a request held, and the count of dropped
/// lines is written with the next one.
/// </para>
/// <para>
/// <b>Thirty days</b>, as the request and audit logs keep theirs (<c>LogRetention</c>); a file older is deleted when
/// the writer first writes on a new day.
/// </para>
/// </remarks>
internal sealed class ServerLogFiles : IDisposable
{
    /// <summary>How many days of files are kept.</summary>
    public const int Days = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly Channel<ServerLogEntry> _lines =
        Channel.CreateBounded<ServerLogEntry>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _writer;
    private long _dropped;

    /// <summary>Opens the log directory, creating it when it is not there.</summary>
    public ServerLogFiles(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>Queues one entry for its day's file.</summary>
    public void Write(ServerLogEntry entry)
    {
        if (!_lines.Writer.TryWrite(entry))
        {
            System.Threading.Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>The most recent entries on disk, oldest first, at most <paramref name="count"/>.</summary>
    public IReadOnlyList<(DateTimeOffset At, LogLevel Level, string Category, int EventId, string? EventName, string Message, string? Exception)> ReadRecent(int count)
    {
        List<(DateTimeOffset, LogLevel, string, int, string?, string, string?)> found = [];

        foreach (string file in Directory.EnumerateFiles(_directory, "server-*.jsonl").OrderDescending(StringComparer.Ordinal))
        {
            List<(DateTimeOffset, LogLevel, string, int, string?, string, string?)> day = [];

            foreach (string line in File.ReadLines(file))
            {
                try
                {
                    Line? read = JsonSerializer.Deserialize<Line>(line, Json);
                    if (read is not null)
                    {
                        day.Add((read.At, read.Level, read.Category ?? string.Empty, read.EventId, read.EventName,
                            read.Message ?? string.Empty, read.Exception));
                    }
                }
                catch (JsonException)
                {
                    // A line cut short by a crash is skipped, not a reason to lose the file.
                }
            }

            found.InsertRange(0, day);

            if (found.Count >= count)
            {
                break;
            }
        }

        return [.. found.Skip(Math.Max(0, found.Count - count))];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private async Task WriteAsync()
    {
        string? today = null;

        await foreach (ServerLogEntry entry in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                string day = entry.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != today)
                {
                    today = day;
                    Prune(entry.At);
                }

                long dropped = System.Threading.Interlocked.Exchange(ref _dropped, 0);
                Line line = new(entry.At, entry.Level, entry.Category, entry.EventId, entry.EventName,
                    dropped == 0 ? entry.Message : $"{entry.Message} ({dropped} earlier lines were dropped: the writer fell behind)",
                    entry.Exception);

                await File.AppendAllTextAsync(
                    Path.Combine(_directory, $"server-{day}.jsonl"),
                    JsonSerializer.Serialize(line, Json) + "\n").ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A full or read-only disk loses the line, never the request that logged it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        string oldest = $"server-{now.UtcDateTime.AddDays(-Days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl";

        foreach (string file in Directory.EnumerateFiles(_directory, "server-*.jsonl"))
        {
            if (string.CompareOrdinal(Path.GetFileName(file), oldest) < 0)
            {
                File.Delete(file);
            }
        }
    }

    private sealed record Line(
        DateTimeOffset At, LogLevel Level, string? Category, int EventId, string? EventName, string? Message, string? Exception);
}
