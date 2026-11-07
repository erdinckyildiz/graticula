using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Makes a scene layer again on its own when the layer it was made from has changed since — ADR-277, by owner decision
/// ADR-272 §5.7. Enterprise's scheduled *Rebuild cache*.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only where its owner asked</b> (<c>autoRebuild</c> in the scene layer's source), <b>once per site</b> (ADR-012's
/// singleton sweeps), <b>in every organisation</b>: each tenant is entered as a job enters the tenant it was started in
/// (ADR-204), so what it reads and writes is that tenant's.
/// </para>
/// <para>
/// <b>Whose rebuild it is.</b> A rebuild by hand is its manager's; this one has no request. It is made only when the
/// layer it reads is the scene layer owner's own, or is shared with everyone — so it never reads, on the owner's
/// behalf, a layer the owner might no longer be allowed to read. Anything else is recorded as needing a rebuild by hand.
/// <c>INFERRED</c>, for the owner to confirm: ArcGIS runs a scheduled rebuild as the item's owner.
/// </para>
/// <para>
/// <b>What it says.</b> Each attempt is kept on the item (<see cref="AdminEndpoints.AutomaticPath"/>) — when, made or
/// not, and why not — and Studio's page of the scene layer shows it, as Enterprise shows a rebuild job's status.
/// </para>
/// </remarks>
internal sealed partial class SceneRebuilds(
    IServiceProvider services, ILogger<SceneRebuilds> logger, Microsoft.Extensions.Configuration.IConfiguration configuration,
    SiteSingletons? site = null) : BackgroundService
{
    /// <summary>How often the sweep looks. A rebuild is minutes behind an edit at most, which is what Enterprise's
    /// shortest schedule offers too.</summary>
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits a little: a node just started is not the moment to rebuild the city.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Interval(30)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (site is null || await site.LeadsAsync(SiteSingletons.SceneRebuilds, stoppingToken).ConfigureAwait(false))
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A failed pass is logged and the next one tried; the server keeps serving.
            catch (Exception failed)
#pragma warning restore CA1031
            {
                Log.SweepFailed(logger, failed);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Interval(Every.TotalSeconds)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The interval, or <c>Graticula:SceneRebuildSeconds</c> when a deployment sets one — CI's server sets five, so a
    /// test sees a rebuild in seconds rather than minutes.
    /// </summary>
    private double Interval(double usual) =>
        double.TryParse(configuration["Graticula:SceneRebuildSeconds"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double asked) && asked > 0 ? asked : usual;

    /// <summary>One pass over every organisation; answers how many scene layers were made again.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellation)
    {
        TenantDirectory? tenants = services.GetService<TenantDirectory>();
        IReadOnlyList<Guid?> each = Graticula.Platform.Tenancy.TenantScope.MultiTenant && tenants is not null
            ? [.. tenants.All.Select(t => (Guid?)t.Id)]
            : [null];
        int made = 0;

        foreach (Guid? tenant in each)
        {
            using (Graticula.Platform.Tenancy.TenantScope.EnterJob(tenant))
            {
                made += await SweepTenantAsync(cancellation).ConfigureAwait(false);
            }
        }

        return made;
    }

    private async Task<int> SweepTenantAsync(CancellationToken cancellation)
    {
        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        CatalogFallback catalog = services.GetRequiredService<CatalogFallback>();
        IReadOnlyList<PublishedService>? published = null;
        int made = 0;

        foreach (Document document in await documents.ListAsync(cancellation).ConfigureAwait(false))
        {
            if (!Documents.IsScenePackage(document.Kind) || document.ContentFile is not null
                || await AdminEndpoints.ReadSourceAsync(services, document.Id, cancellation).ConfigureAwait(false) is not { AutoRebuild: true } source)
            {
                continue;
            }

            published ??= (await catalog.ListServicesAsync(cancellation).ConfigureAwait(false)).Services ?? [];
            PublishedService? service = published.FirstOrDefault(s => s.Id == source.ServiceId);
            PublishedLayer? layer = service?.Layers.ElementAtOrDefault(source.Layer);
            DateTimeOffset? changed = layer is null ? null : Latest(layer.DataChangedAt, layer.SchemaChangedAt);

            if (service is null || layer is null)
            {
                await SayOnceAsync(document, source, "The layer it is made from no longer exists, so it cannot be made again.", cancellation).ConfigureAwait(false);
                continue;
            }

            if (changed is not { } after || after <= source.Built)
            {
                continue;
            }

            if (service.Owner != document.Owner && service.Sharing != SharingScope.Public)
            {
                await SayOnceAsync(document, source,
                    "The layer changed, and it is not this scene layer owner's own or shared with everyone, so it is not read on their behalf. Rebuild it by hand.",
                    cancellation).ConfigureAwait(false);
                continue;
            }

            AdminEndpoints.SceneAutomatic said = await AdminEndpoints.RebuildFromSourceAsync(services, document, source, service, cancellation)
                .ConfigureAwait(false);
            await AdminEndpoints.WriteAutomaticAsync(services, document.Id, said, cancellation).ConfigureAwait(false);

            if (said.Made)
            {
                made++;
                Log.Rebuilt(logger, document.Title, said.Features ?? 0);
            }
            else
            {
                Log.NotRebuilt(logger, document.Title, said.Why ?? string.Empty);
            }
        }

        return made;
    }

    /// <summary>Records why a scene layer was not made again — once per reason and build, not every five minutes.</summary>
    private async Task SayOnceAsync(Document document, SceneSource source, string why, CancellationToken cancellation)
    {
        if (await services.GetRequiredService<IItemResourceStore>()
                .FindAsync(Guid.Parse(document.Id), AdminEndpoints.AutomaticPath, cancellation).ConfigureAwait(false) is { } kept
            && System.Text.Json.JsonSerializer.Deserialize<AdminEndpoints.SceneAutomatic>(kept.Content, Json) is { Made: false } said
            && said.Why == why && said.At >= source.Built)
        {
            return;
        }

        await AdminEndpoints.WriteAutomaticAsync(services, document.Id, new AdminEndpoints.SceneAutomatic(DateTimeOffset.UtcNow, false, why, null), cancellation)
            .ConfigureAwait(false);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y ? (x > y ? x : y) : a ?? b;

    private static partial class Log
    {
        [LoggerMessage(EventId = 1450, Level = LogLevel.Information, Message = "Scene layer '{Title}' was made again from its changed layer ({Features} features).")]
        public static partial void Rebuilt(ILogger logger, string title, int features);

        [LoggerMessage(EventId = 1451, Level = LogLevel.Warning, Message = "Scene layer '{Title}' was not made again: {Why}")]
        public static partial void NotRebuilt(ILogger logger, string title, string why);

        [LoggerMessage(EventId = 1452, Level = LogLevel.Warning, Message = "The scene layer rebuild sweep failed; the next pass will try again.")]
        public static partial void SweepFailed(ILogger logger, Exception failed);
    }
}
