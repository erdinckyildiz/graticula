using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>A feature service of a map that an area was not packaged for, and why.</summary>
/// <param name="Service">The service, as the map names it.</param>
/// <param name="Reason">Why it was left out.</param>
internal sealed record SkippedService(string Service, string Reason);

/// <summary>What packaging a map area made.</summary>
/// <param name="Area">The area.</param>
/// <param name="Packages">Its packages, one per feature service.</param>
/// <param name="Skipped">The services that were left out.</param>
internal sealed record PackagedArea(MapArea Area, IReadOnlyList<MapAreaPackage> Packages, IReadOnlyList<SkippedService> Skipped);

/// <summary>
/// Packaging a preplanned map area — ADR-288 §11a: one registered <c>perLayer</c> replica per sync-enabled feature service
/// of a map, written once as a mobile geodatabase and kept as an item beside the area's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each package is <see cref="OfflinePackager"/>'s replica</b>, owned by whoever asked, and what the file holds is what
/// <c>createReplica</c> would have written for the same layers and extent. The documents the file carries are built for the
/// caller who asks, from the request they hold.
/// </para>
/// <para>
/// <b>All or nothing for a new area.</b> A package that fails to write takes the area's other packages, replicas and files
/// with it, so a client is never offered an area that holds some of what the map needs. A refresh is the same per package
/// but keeps what it could not replace: the old package stays whole until the new one is written, then the old replica is
/// unregistered and the old file removed.
/// </para>
/// </remarks>
internal static class MapAreaPackaging
{
    /// <summary>A feature service as a map names it: its qualified name, its address, and the layers the map uses of it.</summary>
    /// <param name="Name">The qualified name (<c>folder/name</c>).</param>
    /// <param name="Address">The FeatureServer address the map states, without a layer id.</param>
    /// <param name="Layers">The layer ids the map uses; empty when it names the service alone.</param>
    internal sealed record MapService(string Name, string Address, IReadOnlyList<int> Layers);

    /// <summary>
    /// The feature services a map's layer addresses name, with the layers it uses of each — pure, from the addresses
    /// <see cref="IWebMapStore.FeatureServiceUrlsAsync"/> reads out of the document.
    /// </summary>
    /// <param name="urls">The addresses.</param>
    /// <returns>The services, in the order they first appear.</returns>
    public static IReadOnlyList<MapService> ServicesOf(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        List<string> order = [];
        Dictionary<string, (string Address, SortedSet<int> Layers)> found = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in urls)
        {
            string url = raw;
            int query = url.IndexOfAny(['?', '#']);

            if (query >= 0)
            {
                url = url[..query];
            }

            int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
            int end = url.IndexOf("/FeatureServer", StringComparison.OrdinalIgnoreCase);

            if (at < 0 || end <= at)
            {
                continue;
            }

            string name = url[(at + "/rest/services/".Length)..end];
            string address = url[..(end + "/FeatureServer".Length)];
            string rest = url[(end + "/FeatureServer".Length)..].Trim('/');

            if (!found.TryGetValue(name, out (string Address, SortedSet<int> Layers) entry))
            {
                entry = (address, []);
                found[name] = entry;
                order.Add(name);
            }

            if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int layer))
            {
                entry.Layers.Add(layer);
            }
        }

        return [.. order.Select(n => new MapService(n, found[n].Address, [.. found[n].Layers]))];
    }

    /// <summary>The fields of a <c>createReplica</c> request for one service's package, as the reader takes them.</summary>
    /// <param name="replicaName">The replica's name.</param>
    /// <param name="layers">The layers.</param>
    /// <param name="area">The area.</param>
    /// <param name="layerQueries">The layer queries as JSON text, or null for every layer's default.</param>
    /// <returns>A lookup by parameter name.</returns>
    public static Func<string, string?> RequestFields(string replicaName, IEnumerable<int> layers, MapArea area, string? layerQueries = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(area);

        string envelope = JsonSerializer.Serialize(new
        {
            xmin = area.XMin,
            ymin = area.YMin,
            xmax = area.XMax,
            ymax = area.YMax,
            spatialReference = new { wkid = area.Wkid },
        });

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dataFormat"] = "sqlite",
            ["syncModel"] = "perLayer",
            ["replicaName"] = replicaName,
            ["layers"] = string.Join(',', layers),
            ["geometry"] = envelope,
            ["geometryType"] = "esriGeometryEnvelope",
            ["inSR"] = area.Wkid.ToString(CultureInfo.InvariantCulture),
            ["returnAttachments"] = "true",
            ["attachmentsSyncDirection"] = "bidirectional",
            ["async"] = "false",
        };

        if (layerQueries is not null)
        {
            fields["layerQueries"] = layerQueries;
        }

        return name => fields.TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>The qualified name of the feature service an address names, or null when it names none.</summary>
    /// <param name="address">A FeatureServer address.</param>
    /// <returns>The name.</returns>
    public static string? ServiceNameOf(string address)
    {
        IReadOnlyList<MapService> found = ServicesOf([address]);
        return found.Count > 0 ? found[0].Name : null;
    }

    /// <summary>The service address a package states in its properties, or empty.</summary>
    /// <param name="package">The package.</param>
    /// <returns>The address.</returns>
    public static string PackageUrlOf(MapAreaPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return PropertyOf(package.Properties, "url") ?? string.Empty;
    }

    /// <summary>A package item's title: the area's, and the service it was taken from.</summary>
    /// <param name="areaTitle">The area's title.</param>
    /// <param name="serviceAddress">The service's address as the package states it.</param>
    /// <returns>The title.</returns>
    public static string TitleOf(string areaTitle, string serviceAddress)
    {
        string name = ServiceNameOf(serviceAddress) ?? "package";
        int slash = name.LastIndexOf('/');
        return $"{areaTitle} - {(slash >= 0 ? name[(slash + 1)..] : name)}";
    }

    /// <summary>The name a package's replica is given, as the one read on Enterprise is: <c>SDS_FS_</c> and a time.</summary>
    /// <param name="generation">The moment of the copy.</param>
    /// <returns>The name.</returns>
    public static string ReplicaNameAt(DateTimeOffset generation) =>
        "SDS_FS_" + generation.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------ create

    /// <summary>
    /// Makes an area of a map and packages it: one replica per sync-enabled feature service the map holds.
    /// </summary>
    /// <param name="context">The request, whose caller owns the replicas and whose documents the files carry.</param>
    /// <param name="map">The map.</param>
    /// <param name="title">The area's title.</param>
    /// <param name="extent">The area's extent, in <paramref name="wkid"/>.</param>
    /// <param name="wkid">The extent's reference.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The area, or why not.</returns>
    public static async Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> CreateAsync(
        HttpContext context, WebMap map, string title, Envelope extent, int wkid, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(map);

        IServiceProvider services = context.RequestServices;
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();
        IWebMapStore maps = services.GetRequiredService<IWebMapStore>();

        if (services.GetService<IMobileGeodatabaseWriter>() is not { } writer)
        {
            return (null, new PackageRefusal(501,
                "This server has no mobile geodatabase writer installed, so it cannot write the file a package is (ADR-288 §6). "
                + "Nothing was created."));
        }

        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        IReadOnlyDictionary<string, IReadOnlyList<string>> urls =
            await maps.FeatureServiceUrlsAsync([map.Id], cancellation).ConfigureAwait(false);
        IReadOnlyList<MapService> named = ServicesOf(urls.TryGetValue(map.Id, out IReadOnlyList<string>? held) ? held : []);
        List<SkippedService> skipped = [];
        List<(PublishedService Service, MapService Named)> eligible = [];
        IReadOnlyList<PublishedService> published = await PublishedAsync(services, cancellation).ConfigureAwait(false);

        foreach (MapService one in named)
        {
            (PublishedService? service, string? why) = await AdmitAsync(context, published, one, cancellation).ConfigureAwait(false);

            if (service is null)
            {
                skipped.Add(new SkippedService(one.Name, why!));
            }
            else
            {
                eligible.Add((service, one));
            }
        }

        if (named.Count == 0)
        {
            return (null, new PackageRefusal(409, "This map holds no feature layer, so there is nothing to take offline."));
        }

        if (eligible.Count == 0)
        {
            return (null, new PackageRefusal(409,
                "None of the map's feature services can be taken offline: "
                + string.Join(" ", skipped.Select(s => $"{s.Service}: {s.Reason}")) + " Nothing was created."));
        }

        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        DateTimeOffset now = Truncated(clock.GetUtcNow());
        MapArea area = new(
            Guid.NewGuid(), map.Id, title, extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, wkid, MapAreas.Complete, now, now,
            caller.Principal.Id, caller.Principal.Name);

        if (!await store.CreateAsync(area, cancellation).ConfigureAwait(false))
        {
            return (null, new PackageRefusal(404, $"The map '{map.Id}' no longer exists."));
        }

        List<MapAreaPackage> made = [];

        try
        {
            foreach ((PublishedService service, MapService one) in eligible)
            {
                (MapAreaPackage? package, PackageRefusal? refusal) = await WriteAsync(
                    context, writer, area, service, one, existing: null, cancellation).ConfigureAwait(false);

                if (package is null)
                {
                    await DiscardAsync(services, made, cancellation).ConfigureAwait(false);
                    await store.DeleteAsync(area.Id, CancellationToken.None).ConfigureAwait(false);
                    return (null, new PackageRefusal(refusal!.Status, $"{one.Name}: {refusal.Message} Nothing was created."));
                }

                made.Add(package);
                await store.AddPackageAsync(package, TitleOf(title, one.Address.Length > 0 ? one.Address : $"/rest/services/{one.Name}/FeatureServer"), cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            await DiscardAsync(services, made, CancellationToken.None).ConfigureAwait(false);
            await store.DeleteAsync(area.Id, CancellationToken.None).ConfigureAwait(false);

            return (null, new PackageRefusal(500, $"The area could not be packaged: {e.Message} Nothing was created."));
        }

        return (new PackagedArea(area, made, skipped), null);
    }

    // ------------------------------------------------------------------------------------------------ refresh

    /// <summary>
    /// Writes each package of an area again from the layers as they are now, keeping the package items: the new replica
    /// is registered and the new file written before the old replica is unregistered and the old file removed.
    /// </summary>
    /// <param name="context">The request, whose caller owns the new replicas.</param>
    /// <param name="area">The area.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The area as refreshed, or why not.</returns>
    public static async Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> RefreshAsync(
        HttpContext context, MapArea area, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(area);

        IServiceProvider services = context.RequestServices;
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();

        if (services.GetService<IMobileGeodatabaseWriter>() is not { } writer)
        {
            return (null, new PackageRefusal(501,
                "This server has no mobile geodatabase writer installed, so it cannot write the file a package is (ADR-288 §6). "
                + "Nothing was changed."));
        }

        IReadOnlyList<MapAreaPackage> existing = await store.PackagesAsync([area.Id], cancellation).ConfigureAwait(false);
        IReadOnlyList<PublishedService> published = await PublishedAsync(services, cancellation).ConfigureAwait(false);
        List<MapAreaPackage> now = [];
        List<SkippedService> skipped = [];

        foreach (MapAreaPackage old in existing)
        {
            PublishedService? service = published.FirstOrDefault(s => s.Id == old.ServiceId);
            string url = PropertyOf(old.Properties, "url") ?? string.Empty;
            string name = service?.QualifiedName ?? old.ServiceId.ToString("D");

            if (service is null)
            {
                skipped.Add(new SkippedService(name, "The service is no longer published, so its package was kept as it was."));
                now.Add(old);
                continue;
            }

            MapService one = new(service.QualifiedName, url, LayersOf(old.Properties));
            (PublishedService? admitted, string? why) = await AdmitAsync(context, published, one, cancellation).ConfigureAwait(false);

            if (admitted is null)
            {
                skipped.Add(new SkippedService(name, why! + " Its package was kept as it was."));
                now.Add(old);
                continue;
            }

            (MapAreaPackage? package, PackageRefusal? refusal) = await WriteAsync(
                context, writer, area, admitted, one, old, cancellation).ConfigureAwait(false);

            if (package is null)
            {
                skipped.Add(new SkippedService(name, refusal!.Message + " Its package was kept as it was."));
                now.Add(old);
                continue;
            }

            DateTimeOffset when = package.Refreshed;
            await store.ReplacePackageAsync(package, when, cancellation).ConfigureAwait(false);

            // The old copy goes only after the row names the new one: a failure between leaves two replicas, never none.
            await DiscardAsync(services, [old], CancellationToken.None).ConfigureAwait(false);
            now.Add(package);
        }

        DateTimeOffset refreshed = Truncated(services.GetRequiredService<TimeProvider>().GetUtcNow());
        await store.SetStatusAsync(area.Id, MapAreas.Complete, refreshed, cancellation).ConfigureAwait(false);

        return (new PackagedArea(area with { Status = MapAreas.Complete, Refreshed = refreshed }, now, skipped), null);
    }

    // ------------------------------------------------------------------------------------------------ one package

    /// <summary>
    /// Plans, writes and registers one service's package for an area, and describes it. A new package gets a new id; a
    /// refreshed one keeps <paramref name="existing"/>'s.
    /// </summary>
    private static async Task<(MapAreaPackage? Package, PackageRefusal? Refusal)> WriteAsync(
        HttpContext context, IMobileGeodatabaseWriter writer, MapArea area, PublishedService service, MapService named,
        MapAreaPackage? existing, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        MapAreaPackageFiles files = services.GetRequiredService<MapAreaPackageFiles>();
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        DateTimeOffset generation = Truncated(services.GetRequiredService<TimeProvider>().GetUtcNow());

        // The layers the map uses of the service, or all of them when the map names the service alone.
        IEnumerable<int> wanted = named.Layers.Count > 0 ? named.Layers : service.LayersAndTables.Select(l => l.LayerIndex);
        int[] layers = [.. wanted.Where(id => service.LayerOrTable(id) is not null).Distinct().Order()];

        if (layers.Length == 0)
        {
            return (null, new PackageRefusal(409, "None of the layers the map uses is a layer or table of this service."));
        }

        Func<string, string?> fields = RequestFields(
            ReplicaNameAt(generation), layers, area, existing is null ? null : PropertyOf(existing.Properties, "layerQueries"));

        if (!OfflineReplicaRequests.TryReadCreate(fields, service, out CreateReplicaRequest? request, out string? error))
        {
            return (null, new PackageRefusal(400, error!));
        }

        string serviceDocument = await Program.ServiceDocumentTextAsync(context, service, cancellation).ConfigureAwait(false);
        (PackagePlan? plan, PackageRefusal? refusal) = await OfflinePackager.PlanAsync(
            services, service, request!, serviceDocument,
            id => Program.LayerDocumentTextAsync(context, service, id, cancellation), cancellation).ConfigureAwait(false);

        if (plan is null)
        {
            return (null, refusal);
        }

        (string path, string recorded) = files.Next();
        Guid replicaId = Guid.NewGuid();
        string serviceUrl = named.Address.Length > 0
            ? named.Address
            : TileFaces.Origin(context) + "/rest/services/" + service.QualifiedName + "/FeatureServer";

        IReadOnlyList<ReplicaLayer> generations = await OfflinePackager.WriteAsync(
            services, writer, plan, replicaId, new ReplicaOwner(caller.Principal.Id, caller.Principal.Name), serviceUrl, generation,
            path, cancellation).ConfigureAwait(false);

        try
        {
            (string sha, long size) = await MapAreaPackageFiles.HashAsync(path, cancellation).ConfigureAwait(false);
            string properties = OfflinePackager.PackageProperties(plan, replicaId, serviceUrl, generations);

            return (new MapAreaPackage(
                existing?.Id ?? Guid.NewGuid(), area.Id, area.MapId, service.Id, replicaId, request!.Name + ".geodatabase", recorded,
                sha, size, properties, existing?.Created ?? generation, generation), null);
        }
        catch
        {
            // The replica is registered and the file is written, and nothing records either: undo both.
            await services.GetRequiredService<IOfflineSyncStore>().DeleteAsync(replicaId, CancellationToken.None).ConfigureAwait(false);
            files.Delete(recorded);
            throw;
        }
    }

    /// <summary>Unregisters the replicas of these packages and removes their files.</summary>
    /// <param name="services">The services.</param>
    /// <param name="packages">The packages.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task DiscardAsync(IServiceProvider services, IEnumerable<MapAreaPackage> packages, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(packages);

        IOfflineSyncStore sync = services.GetRequiredService<IOfflineSyncStore>();
        MapAreaPackageFiles files = services.GetRequiredService<MapAreaPackageFiles>();

        foreach (MapAreaPackage package in packages)
        {
            try
            {
                await sync.DeleteAsync(package.ReplicaId, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A replica that cannot be unregistered now is unregistered when its service's sync is turned off; the
                // file is removed regardless, because a package without its file is the stale one an administrator can see.
            }

            files.Delete(package.FilePath);
        }
    }

    // ------------------------------------------------------------------------------------------------ admission

    private static async Task<IReadOnlyList<PublishedService>> PublishedAsync(IServiceProvider services, CancellationToken cancellation) =>
        (await services.GetRequiredService<CatalogFallback>().ListServicesAsync(cancellation).ConfigureAwait(false)).Services ?? [];

    /// <summary>
    /// What <c>createReplica</c> asks of a service and its caller, without a response to write the refusal to: the service
    /// is there and the caller may read it, sync is on, and the caller may edit it.
    /// </summary>
    private static async Task<(PublishedService? Service, string? Why)> AdmitAsync(
        HttpContext context, IReadOnlyList<PublishedService> published, MapService named, CancellationToken cancellation)
    {
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        PublishedService? service = published.FirstOrDefault(s => string.Equals(s.QualifiedName, named.Name, StringComparison.OrdinalIgnoreCase));

        if (service is null || !LayerAccess.Evaluate(service.Sharing, service.Owner, caller.Principal, caller.Authorization, service.SharedWith).IsAllowed())
        {
            return (null, "It is not a service of this server that you can read.");
        }

        if (!SyncFlags.IsOn(service))
        {
            return (null, "Sync is not enabled on it." + (SyncFlags.Refusal(service) is { } refusal ? " " + refusal : string.Empty));
        }

        string capabilities = await Program.ServiceCapabilitiesAsync(
            context, service, context.RequestServices.GetRequiredService<ServiceContexts>(), cancellation).ConfigureAwait(false);

        return SyncDocument.OffersSync(capabilities)
            ? (service, null)
            : (null, "Taking it offline and synchronising it is for those who may edit it, and you may not.");
    }

    // ------------------------------------------------------------------------------------------------ small things

    private static DateTimeOffset Truncated(DateTimeOffset moment) =>
        DateTimeOffset.FromUnixTimeMilliseconds(moment.ToUnixTimeMilliseconds());

    private static string? PropertyOf(string properties, string name)
    {
        try
        {
            return JsonNode.Parse(properties) is JsonObject o && o[name] is { } node
                ? node is JsonValue v && v.TryGetValue(out string? text) ? text : node.ToJsonString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<int> LayersOf(string properties)
    {
        try
        {
            return JsonNode.Parse(properties) is JsonObject { } o && o["layers"] is JsonArray layers
                ? [.. layers.Select(l => l!.GetValue<int>())]
                : [];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }
}
