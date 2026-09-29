using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// How many responses each service has been answered with a stale tile, and the log line that says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-010 §5.1a asks for a header and a metric, and this is the metric's half — owner decision
/// 2026-09-29.</b> <c>X-Tile-Cache: STALE</c> tells the client; nobody reads a client's headers on the operator's
/// behalf, and a map quietly drawn from yesterday's tiles through an outage is exactly what an operator has to be
/// able to find out after the fact. The count is on the service's cache read-back
/// (<c>GET /admin/services/{name}/cache</c>), beside the quota's evictions.
/// </para>
/// <para>
/// <b>A log line at most once a minute per service</b>, because stale serving is an outage's steady state: a
/// line per tile would be hundreds a second from one map, which buries the outage it is reporting. The count
/// keeps every one.
/// </para>
/// <para>
/// <b>Counted per response, not per layer part</b> — one tile a map asked for is one stale answer, however many
/// of the service's layers stood in for it. Since this process started: a restart begins at zero, and says so by
/// having no <c>lastServed</c>.
/// </para>
/// </remarks>
internal sealed class StaleTileNotices
{
    /// <summary>How long a service's log line stays quiet after it is said.</summary>
    internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Served> _served = new();

    private readonly TimeProvider _clock;

    /// <summary>Creates the counter.</summary>
    /// <param name="clock">The clock the quiet period is measured on.</param>
    public StaleTileNotices(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Counts one stale response, and logs when the service's quiet period is over.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="serviceName">Its qualified name, for the log.</param>
    /// <param name="why">What refused the build — the refusal's own sentence.</param>
    /// <param name="log">Where the line goes.</param>
    public void Note(Guid serviceId, string serviceName, string why, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        DateTimeOffset now = _clock.GetUtcNow();
        bool say = false;

        Served after = _served.AddOrUpdate(
            serviceId,
            _ => new Served(1, now, now),
            (_, before) => new Served(before.Count + 1, now, before.Said));

        // Said when this call made the entry, or when the last line is a minute old. Claimed by swapping the
        // stamp, so two responses crossing the minute do not both log.
        if (after.Count == 1)
        {
            say = true;
        }
        else if (now - after.Said >= Quiet
                 && _served.TryUpdate(serviceId, after with { Said = now }, after))
        {
            say = true;
        }

        if (say)
        {
            Log.TilesServedStale(log, serviceName, why, after.Count);
        }
    }

    /// <summary>What a service's read-back says: how many stale responses, and the last.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>The count and the last time, or zero and null.</returns>
    public (long Count, DateTimeOffset? Last) Of(Guid serviceId) =>
        _served.TryGetValue(serviceId, out Served served) ? (served.Count, served.Last) : (0, null);

    /// <summary>One service's count.</summary>
    /// <param name="Count">Stale responses since this process started.</param>
    /// <param name="Last">When the last was served.</param>
    /// <param name="Said">When the log last said so.</param>
    private readonly record struct Served(long Count, DateTimeOffset Last, DateTimeOffset Said);
}
