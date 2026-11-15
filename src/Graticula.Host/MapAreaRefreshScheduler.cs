using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Refreshes a map area's packages on the schedule its <c>packageRefreshSchedule</c> names — ADR-288 §11a, D-405.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server's existing way of running scheduled work</b>: a hosted service that looks once a minute, takes a lock so that
/// one node of a site does it (<see cref="SiteSingletons.MapAreaRefresh"/>, ADR-012), and acts on what is due — as the backup
/// schedule (<see cref="BackupScheduler"/>) and the sweeps do. No scheduler library is added: the cron is
/// <see cref="QuartzCron"/>, which reads the expressions an ArcGIS client writes.
/// </para>
/// <para>
/// <b>As the area's creator, with the capabilities they have now</b> (INFERRED). The packages' replicas are the creator's, as
/// they are when the creator presses refresh; a creator who has been disabled or removed, or who may no longer edit a service,
/// gets that service's package kept as it was and the reason noted, which is what a refresh by hand does. The documents the
/// files carry are written from the address the area was last reached at (<see cref="MapArea.Origin"/>), since nobody is
/// asking: an area made before migration 143 has none and is skipped until it is refreshed once by hand.
/// </para>
/// <para>
/// <b>Due is "a run of the cron has passed since the packages were last written"</b>, from the area's own refresh time, so a
/// refresh by hand postpones the next scheduled one, and an area never refreshed twice for one run. A refresh that throws is
/// not tried again for ten minutes.
/// </para>
/// </remarks>
internal sealed partial class MapAreaRefreshScheduler(
    IServiceProvider services, TimeProvider clock, ILogger<MapAreaRefreshScheduler> log, SiteSingletons? site = null) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, DateTimeOffset> _attempted = [];

    /// <summary>Whether an area's schedule has come round since its packages were last written (or last tried).</summary>
    /// <param name="area">The area.</param>
    /// <param name="now">The moment.</param>
    /// <param name="attempted">When a refresh was last tried and failed in this process, or null.</param>
    /// <returns>True when a run of the cron lies after the last refresh and not after <paramref name="now"/>, and no try failed in the last ten minutes.</returns>
    internal static bool IsDue(MapArea area, DateTimeOffset now, DateTimeOffset? attempted = null)
    {
        ArgumentNullException.ThrowIfNull(area);

        if (!area.ScheduledUpdates || !QuartzCron.TryParse(area.PackageRefreshSchedule, out QuartzCron cron, out _))
        {
            return false;
        }

        if (attempted is { } tried && now - tried < RetryAfter)
        {
            return false;
        }

        return cron.NextAfter(area.Refreshed) is { } next && next <= now;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (site is null || await site.LeadsAsync(SiteSingletons.MapAreaRefresh, stoppingToken).ConfigureAwait(false))
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A failed pass is logged and the next minute tried.
            catch (Exception failed)
#pragma warning restore CA1031
            {
                Log.PassFailed(log, failed);
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

    /// <summary>One pass: every scheduled area that is due is refreshed, one after another.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>How many areas were refreshed.</returns>
    internal async Task<int> RunOnceAsync(CancellationToken cancellation)
    {
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();
        DateTimeOffset now = clock.GetUtcNow();
        int refreshed = 0;

        // A failure is remembered for ten minutes, and then forgotten, so the memory is a count of areas that failed lately.
        foreach (Guid stale in _attempted.Where(pair => now - pair.Value >= RetryAfter).Select(pair => pair.Key).ToList())
        {
            _attempted.Remove(stale);
        }

        foreach ((MapArea area, Guid tenant) in await store.ScheduledAsync(cancellation).ConfigureAwait(false))
        {
            if (!IsDue(area, now, _attempted.TryGetValue(area.Id, out DateTimeOffset tried) ? tried : null))
            {
                continue;
            }

            try
            {
                if (await RefreshOneAsync(area, tenant, cancellation).ConfigureAwait(false))
                {
                    refreshed++;
                    _attempted.Remove(area.Id);
                }
                else
                {
                    _attempted[area.Id] = now;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One area's failure does not stop the others.
            catch (Exception failed)
#pragma warning restore CA1031
            {
                _attempted[area.Id] = now;
                Log.RefreshFailed(log, area.Id, area.Title, failed);
            }
        }

        return refreshed;
    }

    private async Task<bool> RefreshOneAsync(MapArea area, Guid tenant, CancellationToken cancellation)
    {
        using IDisposable job = Graticula.Platform.Tenancy.TenantScope.EnterJob(tenant);
        using IServiceScope scope = services.CreateScope();
        IServiceProvider provided = scope.ServiceProvider;

        if (string.IsNullOrWhiteSpace(area.Origin))
        {
            Log.NoAddress(log, area.Id, area.Title);
            return false;
        }

        (Principal Principal, PasswordHash? Credential)? creator = await provided.GetRequiredService<IIdentityStore>()
            .FindForLoginAsync(area.CreatorName, cancellation).ConfigureAwait(false);

        if (creator is not { } found || found.Principal.IsDisabled)
        {
            Log.NoCreator(log, area.Id, area.Title, area.CreatorName);
            return false;
        }

        RequestPrincipal caller = await services.GetRequiredService<Authentication>().ForAsync(found.Principal, cancellation).ConfigureAwait(false);
        PackagingCaller packaging = PackagingCaller.Background(provided, caller, area.Origin);

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging.RefreshAsync(packaging, area, cancellation).ConfigureAwait(false);

        if (packaged is null)
        {
            Log.Refused(log, area.Id, area.Title, refusal!.Message);
            return false;
        }

        Log.Refreshed(log, area.Id, area.Title, packaged.Packages.Count, packaged.Skipped.Count);
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1478, Level = LogLevel.Information, Message = "Map area {Area} ('{Title}') was refreshed on its schedule: {Packages} packages, {Skipped} left as they were.")]
        public static partial void Refreshed(ILogger logger, Guid area, string title, int packages, int skipped);

        [LoggerMessage(EventId = 1479, Level = LogLevel.Warning, Message = "Map area {Area} ('{Title}') could not be refreshed on its schedule: {Why}")]
        public static partial void Refused(ILogger logger, Guid area, string title, string why);

        [LoggerMessage(EventId = 1480, Level = LogLevel.Warning, Message = "Map area {Area} ('{Title}') failed to refresh on its schedule; the next try is in ten minutes.")]
        public static partial void RefreshFailed(ILogger logger, Guid area, string title, Exception failed);

        [LoggerMessage(EventId = 1481, Level = LogLevel.Warning, Message = "Map area {Area} ('{Title}') is scheduled but has no recorded address to write its packages from; refresh it once by hand.")]
        public static partial void NoAddress(ILogger logger, Guid area, string title);

        [LoggerMessage(EventId = 1482, Level = LogLevel.Warning, Message = "Map area {Area} ('{Title}') is scheduled, and its creator '{Creator}' is gone or disabled, so it is not refreshed.")]
        public static partial void NoCreator(ILogger logger, Guid area, string title, string creator);

        [LoggerMessage(EventId = 1483, Level = LogLevel.Warning, Message = "The map area refresh pass failed; the next minute will try again.")]
        public static partial void PassFailed(ILogger logger, Exception failed);
    }
}
