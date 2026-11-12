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
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
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
/// of a map, written once as a mobile geodatabase and kept as an item beside the area's; and, since the owner's change of
/// 2026-11-12, one vector tile package (a VTPK of the area's extent) per vector tile service of this server that the map
/// uses as a basemap.
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
        return found.Count > 0 ? found[0].Name : TileServiceNameOf(address);
    }

    /// <summary>
    /// The qualified name of the vector tile service an address names (<c>…/rest/services/{name}/VectorTileServer</c>),
    /// whatever host it is on, or null when it names none.
    /// </summary>
    /// <param name="address">A VectorTileServer address.</param>
    /// <returns>The name.</returns>
    public static string? TileServiceNameOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        string url = address;
        int query = url.IndexOfAny(['?', '#']);

        if (query >= 0)
        {
            url = url[..query];
        }

        int at = url.IndexOf("/rest/services/", StringComparison.OrdinalIgnoreCase);
        int end = url.IndexOf("/VectorTileServer", StringComparison.OrdinalIgnoreCase);

        return at < 0 || end <= at ? null : url[(at + "/rest/services/".Length)..end];
    }

    // ------------------------------------------------------------------------------------------------ the basemap

    /// <summary>A layer of a web map's basemap, as its document states it.</summary>
    /// <param name="Title">Its title, or its id, or its address, whichever the document gives first.</param>
    /// <param name="Type">Its <c>layerType</c>, or empty.</param>
    /// <param name="Url">Its address, or null for a layer that has none (OpenStreetMap).</param>
    internal sealed record BasemapLayer(string Title, string Type, string? Url)
    {
        /// <summary>Whether the document calls it a vector tile layer or its address is a VectorTileServer.</summary>
        public bool IsVectorTile =>
            Type.Equals("VectorTileLayer", StringComparison.OrdinalIgnoreCase)
            || (Url?.Contains("/VectorTileServer", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The layers of a web map document's basemap: <c>baseMap.baseMapLayers</c> and <c>baseMap.referenceLayers</c>.</summary>
    /// <param name="document">The web map document, or null.</param>
    /// <returns>The layers, in the order the document gives them.</returns>
    public static IReadOnlyList<BasemapLayer> BasemapLayersOf(string? document)
    {
        List<BasemapLayer> layers = [];

        try
        {
            if (JsonNode.Parse(document ?? "{}") is not JsonObject { } root || root["baseMap"] is not JsonObject baseMap)
            {
                return layers;
            }

            foreach (string array in (string[])["baseMapLayers", "referenceLayers"])
            {
                if (baseMap[array] is not JsonArray entries)
                {
                    continue;
                }

                foreach (JsonNode? entry in entries)
                {
                    if (entry is not JsonObject layer)
                    {
                        continue;
                    }

                    string? url = layer["url"] is JsonValue u && u.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

                    // A vector tile layer may name only its style (measured 2026-11-12: the probe map's basemap had no
                    // `url`, only `styleUrl` = …/VectorTileServer/resources/styles/root.json, which is what Field Maps
                    // fetched). The service is the address up to and including /VectorTileServer.
                    if (url is null
                        && layer["styleUrl"] is JsonValue s && s.TryGetValue(out string? style) && !string.IsNullOrWhiteSpace(style)
                        && style.Trim() is var trimmed
                        && trimmed.IndexOf("/VectorTileServer", StringComparison.OrdinalIgnoreCase) is var at and >= 0)
                    {
                        url = trimmed[..(at + "/VectorTileServer".Length)];
                    }

                    string type = layer["layerType"] is JsonValue t && t.TryGetValue(out string? kind) ? kind : string.Empty;
                    string title = (layer["title"] as JsonValue)?.TryGetValue(out string? name) == true && !string.IsNullOrWhiteSpace(name)
                        ? name
                        : (layer["id"] as JsonValue)?.TryGetValue(out string? id) == true && !string.IsNullOrWhiteSpace(id) ? id : url ?? type;

                    layers.Add(new BasemapLayer(title, type, url));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return [];
        }

        return layers;
    }

    /// <summary>
    /// Whether an address is on this server: relative, or on the host the request arrived at (scheme and case ignored).
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="origin">This server's origin as the request states it.</param>
    /// <returns>True when it is.</returns>
    public static bool IsOnThisServer(string address, string origin)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(origin);

        if (address.StartsWith('/'))
        {
            return true;
        }

        return Uri.TryCreate(address, UriKind.Absolute, out Uri? there)
            && Uri.TryCreate(origin, UriKind.Absolute, out Uri? here)
            && string.Equals(there.Authority, here.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The address a basemap package states: the layer's, without a query or a closing slash.</summary>
    /// <param name="url">The address as the document gives it.</param>
    /// <returns>The address.</returns>
    public static string TileAddressOf(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        int query = url.IndexOfAny(['?', '#']);
        return (query >= 0 ? url[..query] : url).TrimEnd('/');
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

        // The basemap's vector tile services of this server, each to be packaged as a VTPK of the area (ADR-288 section 11a).
        (List<TileTarget> tiles, List<SkippedService> tileSkipped) = await BasemapTargetsAsync(context, map, published, cancellation).ConfigureAwait(false);
        skipped.AddRange(tileSkipped);

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

            foreach (TileTarget target in tiles)
            {
                (MapAreaPackage? package, PackageRefusal? refusal) = await WriteTilesAsync(
                    context, area, target.Service, target.Address, existing: null, cancellation).ConfigureAwait(false);

                if (package is null)
                {
                    await DiscardAsync(services, made, cancellation).ConfigureAwait(false);
                    await store.DeleteAsync(area.Id, CancellationToken.None).ConfigureAwait(false);
                    return (null, new PackageRefusal(refusal!.Status, $"{target.Title}: {refusal.Message} Nothing was created."));
                }

                made.Add(package);
                await store.AddPackageAsync(package, TitleOf(title, target.Address), cancellation).ConfigureAwait(false);
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

            if (old.Format == MapAreas.VtpkFormat)
            {
                MapAreaPackage? tile = await RefreshTilesAsync(context, area, old, service, url, name, skipped, cancellation).ConfigureAwait(false);
                now.Add(tile ?? old);

                if (tile is not null)
                {
                    await store.ReplacePackageAsync(tile, tile.Refreshed, cancellation).ConfigureAwait(false);
                    await DiscardAsync(services, [old], CancellationToken.None).ConfigureAwait(false);
                }

                continue;
            }

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
                if (package.ReplicaId is { } replica)
                {
                    await sync.DeleteAsync(replica, cancellation).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A replica that cannot be unregistered now is unregistered when its service's sync is turned off; the
                // file is removed regardless, because a package without its file is the stale one an administrator can see.
            }

            files.Delete(package.FilePath);
        }
    }

    // ------------------------------------------------------------------------------------------------ a basemap's package

    /// <summary>A vector tile service of this server that a map uses as a basemap, and the address the map gives it.</summary>
    /// <param name="Service">The service.</param>
    /// <param name="Address">The address, as the map states it (the package states the same).</param>
    /// <param name="Title">The layer's title.</param>
    internal sealed record TileTarget(PublishedService Service, string Address, string Title);

    /// <summary>
    /// The basemap layers of a map that can be packaged, and the ones that cannot with why: a layer is packaged when it is
    /// a vector tile service of this server that the caller may read and that offers exports; every other basemap layer is
    /// named under <c>skipped</c>, because a device will not have it offline and an item that pretended otherwise would
    /// be found out in the field.
    /// </summary>
    private static async Task<(List<TileTarget> Targets, List<SkippedService> Skipped)> BasemapTargetsAsync(
        HttpContext context, WebMap map, IReadOnlyList<PublishedService> published, CancellationToken cancellation)
    {
        List<TileTarget> targets = [];
        List<SkippedService> skipped = [];
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        string origin = TileFaces.Origin(context);

        foreach (BasemapLayer layer in BasemapLayersOf(map.Document))
        {
            if (!layer.IsVectorTile || layer.Url is not { } url)
            {
                skipped.Add(new SkippedService(layer.Title, layer.Url is null
                    ? $"The basemap layer '{layer.Title}' has no address{(layer.Type.Length > 0 ? $" (a {layer.Type})" : string.Empty)}, so no tile package can be made for it and a device will not have it offline."
                    : $"The basemap layer '{layer.Title}' is a {(layer.Type.Length > 0 ? layer.Type : "tile layer")} at {layer.Url}, not a vector tile service of this server, so no tile package can be made for it and a device will not have it offline."));
                continue;
            }

            if (!IsOnThisServer(url, origin) || TileServiceNameOf(url) is not { } name)
            {
                skipped.Add(new SkippedService(layer.Title,
                    $"The basemap layer '{layer.Title}' is a vector tile service at {url}, which is not on this server, so no tile package can be made for it and a device will not have it offline."));
                continue;
            }

            PublishedService? service = published.FirstOrDefault(s => string.Equals(s.QualifiedName, name, StringComparison.OrdinalIgnoreCase));

            if (service is null || !LayerAccess.Evaluate(service.Sharing, service.Owner, caller.Principal, caller.Authorization, service.SharedWith).IsAllowed())
            {
                skipped.Add(new SkippedService(layer.Title,
                    $"The basemap layer '{layer.Title}' names the vector tile service '{name}', which is not a service of this server that you can read."));
                continue;
            }

            if (!VectorTileExportEndpoints.MayExport(service, caller.Principal.IsAnonymous))
            {
                skipped.Add(new SkippedService(layer.Title,
                    $"The vector tile service '{name}' does not offer exports to you, so no tile package can be made for it and a device will not have it offline."));
                continue;
            }

            if (targets.Any(t => t.Service.Id == service.Id))
            {
                continue; // the same service twice (a style and its labels): one package serves both
            }

            targets.Add(new TileTarget(service, TileAddressOf(url), layer.Title));
        }

        return (targets, skipped);
    }

    /// <summary>
    /// Writes a vector tile package of the area's extent for a vector tile service — the levels exportTiles' ArcGIS-origin
    /// export keeps (those the service has, down to the deepest that fits the tile cap) — and describes it as a package.
    /// </summary>
    private static async Task<(MapAreaPackage? Package, PackageRefusal? Refusal)> WriteTilesAsync(
        HttpContext context, MapArea area, PublishedService service, string address, MapAreaPackage? existing,
        CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        MapAreaPackageFiles files = services.GetRequiredService<MapAreaPackageFiles>();

        VectorTileExportEndpoints.Ask ask = new(
            TileExportFormat.Vtpk,
            null,
            new AdminEndpoints.SeedExtent(area.XMin, area.YMin, area.XMax, area.YMax, new AdminEndpoints.SeedReference(area.Wkid, null)),
            "arcgis");

        (VectorTileExportEndpoints.Planned? planned, VectorTileExportEndpoints.Refusal? refusal) = await VectorTileExportEndpoints.PlanAsync(
            service, ask, services.GetRequiredService<HostSettings>(), services.GetRequiredService<ServiceContexts>(),
            services.GetRequiredService<IProjector>(), services.GetRequiredService<ITileCache>(),
            services.GetRequiredService<GeoParquetSources>(), cancellation).ConfigureAwait(false);

        if (planned is null)
        {
            return (null, new PackageRefusal(refusal!.Status, refusal.Message));
        }

        DateTimeOffset generation = Truncated(services.GetRequiredService<TimeProvider>().GetUtcNow());
        (string path, string recorded) = files.Next(".vtpk");

        try
        {
            await services.GetRequiredService<TileExporter>().WriteVtpkAsync(service, planned, path, cancellation).ConfigureAwait(false);
            (string sha, long size) = await MapAreaPackageFiles.HashAsync(path, cancellation).ConfigureAwait(false);

            string properties = JsonSerializer.Serialize(new
            {
                url = address,
                format = "vtpk",
                levels = planned.Plan.Levels.Select(l => l.Z),
                tiles = planned.Plan.Total,
            });

            return (new MapAreaPackage(
                existing?.Id ?? Guid.NewGuid(), area.Id, area.MapId, service.Id, null, TileExportPackage.DownloadName(service, TileExportFormat.Vtpk),
                recorded, sha, size, properties, existing?.Created ?? generation, generation, MapAreas.VtpkFormat), null);
        }
        catch
        {
            files.Delete(recorded);
            throw;
        }
    }

    /// <summary>
    /// Writes an existing tile package again from the tiles as they are now. Answers the new package, or null after naming
    /// in <paramref name="skipped"/> why the old one was kept.
    /// </summary>
    private static async Task<MapAreaPackage?> RefreshTilesAsync(
        HttpContext context, MapArea area, MapAreaPackage old, PublishedService? service, string address, string name,
        List<SkippedService> skipped, CancellationToken cancellation)
    {
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;

        if (service is null)
        {
            skipped.Add(new SkippedService(name, "The vector tile service is no longer published, so its package was kept as it was."));
            return null;
        }

        if (!LayerAccess.Evaluate(service.Sharing, service.Owner, caller.Principal, caller.Authorization, service.SharedWith).IsAllowed()
            || !VectorTileExportEndpoints.MayExport(service, caller.Principal.IsAnonymous))
        {
            skipped.Add(new SkippedService(name, "You may no longer export this vector tile service, so its package was kept as it was."));
            return null;
        }

        (MapAreaPackage? package, PackageRefusal? refusal) = await WriteTilesAsync(
            context, area, service, address, old, cancellation).ConfigureAwait(false);

        if (package is null)
        {
            skipped.Add(new SkippedService(name, refusal!.Message + " Its package was kept as it was."));
        }

        return package;
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
