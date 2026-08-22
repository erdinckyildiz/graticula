using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Keeps the request and studio logs from growing without end.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cap is thirty days, and it is a number rather than a default nobody chose.</b>
/// [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// condition 3 asks for the figure to be stated. Thirty days is long enough to contain the
/// question an operator actually asks — *what happened when that client complained last
/// week* — and short enough that a request-rate table on the same store as the data stays a
/// table rather than becoming the data.
/// </para>
/// <para>
/// <b>The audit trail is not swept.</b> *Who deleted that service last quarter* is the
/// question it exists for, and a retention window that forgets it would make the trail
/// decorative. Only the two logs that grow at request rate are capped, which is also why
/// this class does not take a policy: there is one window, for the two logs that need one.
/// </para>
/// <para>
/// <b>Hourly, not nightly.</b> A nightly sweep means the table's high-water mark is a day of
/// traffic rather than an hour of it, and the point of the cap is the high-water mark.
/// </para>
/// </remarks>
internal sealed partial class LogRetention : BackgroundService
{
    /// <summary>How long a request or studio entry is kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly ILogReader _logs;
    private readonly ILogger<LogRetention> _logger;

    /// <summary>Creates the sweeper.</summary>
    /// <param name="logs">The log store.</param>
    /// <param name="logger">Where to say what was swept.</param>
    public LogRetention(ILogReader logs, ILogger<LogRetention> logger)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(logger);

        _logs = logs;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // <b>Once at startup, then hourly.</b> A server that has been down for a month
        // should not wait an hour to notice the month of rows it is holding.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long swept = await _logs.SweepAsync(Keep, stoppingToken).ConfigureAwait(false);

                if (swept > 0)
                {
                    Log.Swept(_logger, swept, (int)Keep.TotalDays);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031
            catch (Exception failed)
#pragma warning restore CA1031
            {
                // <b>Caught broadly and kept running, because the alternative is worse than
                // a broad catch.</b> A sweeper that dies on one bad hour stops capping the
                // table for the lifetime of the process, and nothing would say so until the
                // disk filled. The failure is reported and the next hour tries again.
                Log.SweepFailed(_logger, failed);
            }

            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1042,
            Level = LogLevel.Information,
            Message = "Swept {Rows} log entries older than {Days} days.")]
        public static partial void Swept(ILogger logger, long rows, int days);

        [LoggerMessage(
            EventId = 1043,
            Level = LogLevel.Warning,
            Message = "The log sweep failed; the next one is in an hour.")]
        public static partial void SweepFailed(ILogger logger, Exception failed);
    }
}
