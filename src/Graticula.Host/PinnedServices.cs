using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Keeps the services an administrator pinned warm — ADR-218, owner decision 2026-10-22: *"belki ben hep sıcak tutmak
/// istiyorum belirli servisleri"*. ADR-007 §4.12's pinned context, built without the auto-pinning and affinity it was
/// designed beside.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a cold service costs here, measured before this was written</b> (ADR-218 §1): its table's shape expires
/// after <see cref="ServiceContexts.Lifetime"/> and is read again, about 10 ms; its data source's pool closes a
/// connection idle for five minutes and opens one on the next request, about 70 ms; and after a restart the first
/// read pays the process's own warm-up, about 340 ms. ArcGIS's answer is a process kept running per service; the
/// costs here are a description and a connection, so that is what is kept.
/// </para>
/// <para>
/// <b>Renewed before it expires, not looked up again.</b> Every <see cref="Interval"/> the shape of each pinned layer
/// is described afresh and replaces the entry (<see cref="ServiceContexts.RenewAsync"/>), so a request never meets an
/// expired one; the describe runs on the layer's own pool, so the connection it uses never sits idle long enough to
/// be closed. The first time a layer is warmed in this process one feature is also read, which is what the first
/// request after a restart was paying for.
/// </para>
/// <para>
/// <b>Bounded, because if everything is pinned nothing is</b> (ADR-007 §4.12). <see cref="Limit"/> is checked when a
/// service is pinned, not here: this warms whatever the table says.
/// </para>
/// </remarks>
internal sealed partial class PinnedServices : BackgroundService
{
    /// <summary>How often each pinned layer is renewed: two thirds of the shape's lifetime, so it never lapses.</summary>
    public static readonly TimeSpan Interval = ServiceContexts.Lifetime * 2 / 3;

    private readonly NpgsqlDataSource _store;
    private readonly PostgresLayerCatalog _layers;
    private readonly ServiceContexts _contexts;
    private readonly TimeProvider _clock;
    private readonly ILogger<PinnedServices> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    // One per pinned service; a service unpinned is removed on the next round.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _warmed = new();

    // One per layer of a pinned service; removed with its service.
    private readonly ConcurrentDictionary<Guid, Guid> _read = new();

    public PinnedServices(
        NpgsqlDataSource store,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        TimeProvider clock,
        int limit,
        ILogger<PinnedServices> logger)
    {
        _store = store;
        _layers = layers;
        _contexts = contexts;
        _clock = clock;
        Limit = limit;
        _logger = logger;
    }

    /// <summary>The most services this deployment keeps pinned at once — <c>Graticula:PinnedServiceLimit</c>.</summary>
    public int Limit { get; }

    /// <summary>When this process last finished warming a service, or null while it has not.</summary>
    public DateTimeOffset? WarmedAt(Guid service) => _warmed.TryGetValue(service, out DateTimeOffset at) ? at : null;

    /// <summary>Starts a round now rather than at the next interval — after a pin, so it is warm within seconds.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another caller woke it first; one round is enough.
            }
        }
    }

    /// <summary>Whether a service is pinned.</summary>
    public async Task<bool> IsPinnedAsync(Guid service, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = _store.CreateCommand("select pinned from service where id = @id");
        command.Parameters.AddWithValue("id", service);
        return await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is true;
    }

    /// <summary>How many services are pinned across the whole deployment.</summary>
    /// <remarks>
    /// <b>Read on the platform store's own pool, which no organisation's floor narrows.</b> The limit is the
    /// deployment's — every organisation's pins are warmed by the same process — so it is counted across all of them.
    /// </remarks>
    public async Task<int> CountAsync(CancellationToken cancellation)
    {
        await using NpgsqlCommand command = _store.CreateCommand("select count(*) from service where pinned");
        return (int)(long)(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
    }

    /// <summary>The pinned services one organisation may be told about, as <c>folder/name</c>; all of them with none.</summary>
    /// <remarks>
    /// <b>Only its own.</b> The count is the deployment's, but the names are content (ADR-204): an organisation's
    /// administrator refused a pin is told how many are pinned elsewhere, not what — found by the screen's review.
    /// </remarks>
    public async Task<IReadOnlyList<string>> PinnedAsync(Guid? tenant, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = _store.CreateCommand(
            "select coalesce(folder || '/', '') || name from service where pinned "
            + "and (@tenant::uuid is null or tenant_id = @tenant::uuid) order by 1");
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)tenant ?? DBNull.Value });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        List<string> names = [];
        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Pins or unpins a service; false when there is no such row.</summary>
    public async Task<bool> SetAsync(Guid service, bool pinned, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = _store.CreateCommand("update service set pinned = @pinned where id = @id");
        command.Parameters.AddWithValue("id", service);
        command.Parameters.AddWithValue("pinned", pinned);
        bool found = await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false) > 0;

        if (found)
        {
            Wake();
        }

        return found;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WarmAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The store may be unreachable; the next round tries again, and a request still warms what it asks.
                LogRoundFailed(e);
            }

            try
            {
                await _wake.WaitAsync(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WarmAsync(CancellationToken cancellation)
    {
        HashSet<Guid> pinned = [];

        await using (NpgsqlCommand command = _store.CreateCommand("select id from service where pinned"))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                pinned.Add(reader.GetGuid(0));
            }
        }

        foreach (Guid gone in _warmed.Keys.Where(id => !pinned.Contains(id)).ToList())
        {
            _warmed.TryRemove(gone, out _);
        }

        foreach (KeyValuePair<Guid, Guid> layer in _read.Where(l => !pinned.Contains(l.Value)).ToList())
        {
            _read.TryRemove(layer.Key, out _);
        }

        if (pinned.Count == 0)
        {
            return;
        }

        (var all, _) = await _layers.ListWhatCanBeReadAsync(cancellation).ConfigureAwait(false);
        Dictionary<Guid, bool> warmed = [];

        foreach (PublishedLayer layer in all.Where(l => pinned.Contains(l.ServiceId)))
        {
            try
            {
                await _contexts.RenewAsync(layer).ConfigureAwait(false);

                if (_read.TryAdd(layer.Id, layer.ServiceId))
                {
                    (IFeatureSource source, _) = await _contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

                    await foreach (Feature _ in source.ReadAsync(new FeatureQuery(1), cancellation).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                warmed[layer.ServiceId] = warmed.GetValueOrDefault(layer.ServiceId, true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One unreachable layer leaves its service cold until it answers; the others are still warmed.
                _read.TryRemove(layer.Id, out _);
                warmed[layer.ServiceId] = false;
                LogLayerFailed(layer.Definition.Name, e.Message);
            }
        }

        DateTimeOffset now = _clock.GetUtcNow();

        foreach ((Guid service, bool every) in warmed)
        {
            if (every)
            {
                _warmed[service] = now;
            }
        }
    }

    [LoggerMessage(EventId = 1420, Level = LogLevel.Warning, Message = "Could not keep pinned layer {Layer} warm: {Reason}")]
    private partial void LogLayerFailed(string layer, string reason);

    [LoggerMessage(EventId = 1421, Level = LogLevel.Warning, Message = "Could not read which services are pinned.")]
    private partial void LogRoundFailed(Exception exception);
}
