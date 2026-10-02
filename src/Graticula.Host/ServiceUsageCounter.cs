using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Counts the requests each service answers — ADR-135, Portal's item usage, so an administrator can see what is used and
/// find what nobody uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted in memory and written once a minute</b>, so a request costs an interlocked add and nothing else; the
/// write is one statement for every service counted since the last. A server stopped between two writes loses at most
/// a minute of counts, which is the price of keeping the database off the request path — a count, not an audit.
/// </para>
/// <para>
/// <b>What is read is kept a minute too</b>, so a content listing or a portal search reads a snapshot rather than
/// summing the table on every request.
/// </para>
/// </remarks>
internal sealed partial class ServiceUsageCounter : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most services counted in one minute. Many faces answer a missing service with a 200 and an error envelope,
    /// so the name in a path is the client's: without a ceiling a client could grow the table a minute holds without
    /// bound. Far above the 100–1,000 services this server is sized for; a name past it is not counted that minute.
    /// </summary>
    private const int MaximumServicesAMinute = 10_000;

    /// <summary>The faces whose requests count as a service's use.</summary>
    private static readonly HashSet<string> Faces = new(StringComparer.OrdinalIgnoreCase)
    {
        "FeatureServer", "MapServer", "ImageServer", "VectorTileServer",
    };

    private readonly IServiceUsageStore _store;
    private readonly ILogger<ServiceUsageCounter> _log;
    private ConcurrentDictionary<string, Tally> _counts = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<Guid, ServiceUse> _snapshot = new Dictionary<Guid, ServiceUse>();

    /// <summary>A counter writing to a store.</summary>
    /// <param name="store">Where the counts are kept.</param>
    /// <param name="log">Where a failed write is said.</param>
    public ServiceUsageCounter(IServiceUsageStore store, ILogger<ServiceUsageCounter> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>The first day this server counted anything — what a service's "no requests" is measured from.</summary>
    public DateOnly? Since { get; private set; }

    /// <summary>The last use read, by service id; empty until the first read.</summary>
    public IReadOnlyDictionary<Guid, ServiceUse> Snapshot => _snapshot;

    /// <summary>One service's use from the snapshot, or none.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>Its use, or null.</returns>
    public ServiceUse? Of(Guid serviceId) => _snapshot.TryGetValue(serviceId, out ServiceUse? use) ? use : null;

    /// <summary>Counts a request when its path is a service's face: <c>/rest/services/[folder/]name/Face…</c>.</summary>
    /// <param name="path">The request's path.</param>
    public void CountPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(parts, p => p.Equals("services", StringComparison.OrdinalIgnoreCase));

        if (at < 1 || !parts[at - 1].Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // `name/Face` or `folder/name/Face`.
        if (at + 2 < parts.Length && Faces.Contains(parts[at + 2]))
        {
            Count(null, parts[at + 1]);
        }
        else if (at + 3 < parts.Length && Faces.Contains(parts[at + 3]))
        {
            Count(parts[at + 1], parts[at + 2]);
        }
    }

    private void Count(string? folder, string name)
    {
        string key = $"{folder?.ToLowerInvariant()}/{name.ToLowerInvariant()}";
        ConcurrentDictionary<string, Tally> counts = _counts;
        if (!counts.TryGetValue(key, out Tally? tally))
        {
            if (counts.Count >= MaximumServicesAMinute)
            {
                return;
            }

            tally = counts.GetOrAdd(key, _ => new Tally(folder, name));
        }

        Interlocked.Increment(ref tally.Requests);
    }

    /// <summary>Writes what has been counted, then reads the use back.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task FlushAsync(CancellationToken cancellation)
    {
        ConcurrentDictionary<string, Tally> taken = Interlocked.Exchange(ref _counts, new(StringComparer.Ordinal));
        ServiceCount[] batch = [.. taken.Values.Where(t => t.Requests > 0).Select(t => new ServiceCount(t.Folder, t.Name, t.Requests))];
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            await _store.AddAsync(batch, today, cancellation).ConfigureAwait(false);
            _snapshot = await _store.ReadAsync(today, cancellation).ConfigureAwait(false);
            Since = await _store.FirstDayAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The counts go back to be written next time rather than being lost with a failed write.
            foreach ((string key, Tally tally) in taken)
            {
                Tally into = _counts.GetOrAdd(key, _ => new Tally(tally.Folder, tally.Name));
                Interlocked.Add(ref into.Requests, tally.Requests);
            }

            LogWriteFailed(e);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        try
        {
            await FlushAsync(stoppingToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping: the last minute's counts are written below.
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 2350,
        Level = LogLevel.Warning,
        Message = "Service usage could not be written; the counts are kept and written again in a minute.")]
    private partial void LogWriteFailed(Exception exception);

    private sealed class Tally(string? folder, string name)
    {
        public string? Folder { get; } = folder;

        public string Name { get; } = name;

        public long Requests;
    }
}
