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
    /// <param name="settings">Its refresh schedule and tile scale, or null for none (D-405).</param>
    /// <returns>The area, or why not.</returns>
    public static Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> CreateAsync(
        HttpContext context, WebMap map, string title, Envelope extent, int wkid, CancellationToken cancellation,
        MapAreaSettings? settings = null) =>
        CreateAsync(PackagingCaller.Of(context), map, title, extent, wkid, cancellation, settings);

    /// <inheritdoc cref="CreateAsync(HttpContext, WebMap, string, Envelope, int, CancellationToken, MapAreaSettings?)"/>
    /// <param name="packaging">Who the packages are written for.</param>
    /// <param name="map">The map.</param>
    /// <param name="title">The area's title.</param>
    /// <param name="extent">The area's extent.</param>
    /// <param name="wkid">The extent's reference.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="settings">Its refresh schedule and tile scale, or null.</param>
    public static async Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> CreateAsync(
        PackagingCaller packaging, WebMap map, string title, Envelope extent, int wkid, CancellationToken cancellation,
        MapAreaSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(packaging);
        ArgumentNullException.ThrowIfNull(map);

        IServiceProvider services = packaging.Services;
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();
        IWebMapStore maps = services.GetRequiredService<IWebMapStore>();

        if (services.GetService<IMobileGeodatabaseWriter>() is not { } writer)
        {
            return (null, new PackageRefusal(501,
                "This server has no mobile geodatabase writer installed, so it cannot write the file a package is (ADR-288 §6). "
                + "Nothing was created."));
        }

        RequestPrincipal caller = packaging.Principal;
        IReadOnlyDictionary<string, IReadOnlyList<string>> urls =
            await maps.FeatureServiceUrlsAsync([map.Id], cancellation).ConfigureAwait(false);
        IReadOnlyList<MapService> named = ServicesOf(urls.TryGetValue(map.Id, out IReadOnlyList<string>? held) ? held : []);
        List<SkippedService> skipped = [];
        List<(PublishedService Service, MapService Named)> eligible = [];
        IReadOnlyList<PublishedService> published = await PublishedAsync(services, cancellation).ConfigureAwait(false);

        foreach (MapService one in named)
        {
            (PublishedService? service, string? why) = await AdmitAsync(packaging, published, one, cancellation).ConfigureAwait(false);

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
        (List<TileTarget> tiles, List<SkippedService> tileSkipped) = await BasemapTargetsAsync(packaging, map, published, cancellation).ConfigureAwait(false);
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
            caller.Principal.Id, caller.Principal.Name,
            settings?.PackageRefreshSchedule, settings?.RefreshParams, settings?.ScheduledUpdates ?? false,
            settings?.TileScaleMin, settings?.TileScaleMax, packaging.Origin);

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
                    packaging, writer, area, service, one, existing: null, cancellation).ConfigureAwait(false);

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
                // D-410: the basemap's package is an export job; its row is recorded here, processing, and the job's end
                // completes it. The item says so until then.
                (MapAreaPackage? package, PackageRefusal? refusal) = await StartTilesAsync(
                    packaging, area, target.Service, target.Address, existing: null, TitleOf(title, target.Address), cancellation)
                    .ConfigureAwait(false);

                if (package is null)
                {
                    await DiscardAsync(services, made, CancellationToken.None).ConfigureAwait(false);
                    await store.DeleteAsync(area.Id, CancellationToken.None).ConfigureAwait(false);
                    return (null, new PackageRefusal(refusal!.Status, $"{target.Title}: {refusal.Message} Nothing was created."));
                }

                made.Add(package);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            await DiscardAsync(services, made, CancellationToken.None).ConfigureAwait(false);
            await store.DeleteAsync(area.Id, CancellationToken.None).ConfigureAwait(false);

            return (null, new PackageRefusal(500, $"The area could not be packaged: {e.Message} Nothing was created."));
        }

        return (new PackagedArea(
            area with { Status = made.Any(p => p.Status == MapAreas.PackageProcessing) ? MapAreas.Processing : MapAreas.Complete },
            made, skipped), null);
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
    public static Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> RefreshAsync(
        HttpContext context, MapArea area, CancellationToken cancellation) =>
        RefreshAsync(PackagingCaller.Of(context), area, cancellation);

    /// <summary>
    /// <see cref="RefreshAsync(HttpContext, MapArea, CancellationToken)"/> for a caller with no request - what the scheduled
    /// refresh runs (D-405) - and which also adds a package for each feature service and vector tile basemap the map has gained
    /// since the area was made.
    /// </summary>
    /// <param name="packaging">Who the packages are written for.</param>
    /// <param name="area">The area.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The area as refreshed, or why not.</returns>
    public static async Task<(PackagedArea? Packaged, PackageRefusal? Refusal)> RefreshAsync(
        PackagingCaller packaging, MapArea area, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(packaging);
        ArgumentNullException.ThrowIfNull(area);

        IServiceProvider services = packaging.Services;
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
                // D-410: the job writes the new file and the old one is removed when it finishes; until then the old file
                // is what a device downloads.
                MapAreaPackage? tile = await RefreshTilesAsync(packaging, area, old, service, url, name, skipped, cancellation).ConfigureAwait(false);
                now.Add(tile ?? old);
                continue;
            }

            if (service is null)
            {
                skipped.Add(new SkippedService(name, "The service is no longer published, so its package was kept as it was."));
                now.Add(old);
                continue;
            }

            MapService one = new(service.QualifiedName, url, LayersOf(old.Properties));
            (PublishedService? admitted, string? why) = await AdmitAsync(packaging, published, one, cancellation).ConfigureAwait(false);

            if (admitted is null)
            {
                skipped.Add(new SkippedService(name, why! + " Its package was kept as it was."));
                now.Add(old);
                continue;
            }

            (MapAreaPackage? package, PackageRefusal? refusal) = await WriteAsync(
                packaging, writer, area, admitted, one, old, cancellation).ConfigureAwait(false);

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
        // D-405: what the map has gained since the area was made. A package for it is written as for a new area; one that
        // cannot be is named under skipped and does not fail the refresh of the rest.
        await AddGainedAsync(packaging, area, existing, published, writer, now, skipped, cancellation).ConfigureAwait(false);

        if (area.Origin != packaging.Origin)
        {
            await store.SetOriginAsync(area.Id, packaging.Origin, cancellation).ConfigureAwait(false);
        }

        string status = await store.SettleStatusAsync(area.Id, refreshed, cancellation).ConfigureAwait(false) ?? MapAreas.Complete;

        return (new PackagedArea(area with { Status = status, Refreshed = refreshed }, now, skipped), null);
    }

    /// <summary>
    /// Writes a package for each feature service of the map, and each vector tile basemap of this server, that the area has none for.
    /// </summary>
    private static async Task AddGainedAsync(
        PackagingCaller packaging, MapArea area, IReadOnlyList<MapAreaPackage> existing, IReadOnlyList<PublishedService> published,
        IMobileGeodatabaseWriter writer, List<MapAreaPackage> now, List<SkippedService> skipped, CancellationToken cancellation)
    {
        IServiceProvider services = packaging.Services;
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();

        if (await services.GetRequiredService<IWebMapStore>().FindAsync(area.MapId, cancellation).ConfigureAwait(false) is not { } map)
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> urls =
            await services.GetRequiredService<IWebMapStore>().FeatureServiceUrlsAsync([map.Id], cancellation).ConfigureAwait(false);

        foreach (MapService one in ServicesOf(urls.TryGetValue(map.Id, out IReadOnlyList<string>? held) ? held : []))
        {
            PublishedService? known = published.FirstOrDefault(s => string.Equals(s.QualifiedName, one.Name, StringComparison.OrdinalIgnoreCase));

            if (known is not null && existing.Any(p => p.Format == MapAreas.SqliteFormat && p.ServiceId == known.Id))
            {
                continue;
            }

            (PublishedService? admitted, string? why) = await AdmitAsync(packaging, published, one, cancellation).ConfigureAwait(false);

            if (admitted is null)
            {
                skipped.Add(new SkippedService(one.Name, why! + " It is new to the map and has no package."));
                continue;
            }

            try
            {
                (MapAreaPackage? package, PackageRefusal? refusal) = await WriteAsync(
                    packaging, writer, area, admitted, one, existing: null, cancellation).ConfigureAwait(false);

                if (package is null)
                {
                    skipped.Add(new SkippedService(one.Name, refusal!.Message + " It is new to the map and has no package."));
                    continue;
                }

                await store.AddPackageAsync(
                    package, TitleOf(area.Title, one.Address.Length > 0 ? one.Address : $"/rest/services/{one.Name}/FeatureServer"), cancellation)
                    .ConfigureAwait(false);
                now.Add(package);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                skipped.Add(new SkippedService(one.Name, $"{e.Message} It is new to the map and has no package."));
            }
        }

        (List<TileTarget> tiles, List<SkippedService> tileSkipped) = await BasemapTargetsAsync(packaging, map, published, cancellation).ConfigureAwait(false);
        skipped.AddRange(tileSkipped);

        foreach (TileTarget target in tiles)
        {
            if (existing.Any(p => p.Format == MapAreas.VtpkFormat && p.ServiceId == target.Service.Id))
            {
                continue;
            }

            try
            {
                (MapAreaPackage? package, PackageRefusal? refusal) = await StartTilesAsync(
                    packaging, area, target.Service, target.Address, existing: null, TitleOf(area.Title, target.Address), cancellation)
                    .ConfigureAwait(false);

                if (package is null)
                {
                    skipped.Add(new SkippedService(target.Title, refusal!.Message + " It is new to the map and has no package."));
                    continue;
                }

                now.Add(package);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                skipped.Add(new SkippedService(target.Title, $"{e.Message} It is new to the map and has no package."));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ one package

    /// <summary>
    /// Plans, writes and registers one service's package for an area, and describes it. A new package gets a new id; a
    /// refreshed one keeps <paramref name="existing"/>'s.
    /// </summary>
    private static async Task<(MapAreaPackage? Package, PackageRefusal? Refusal)> WriteAsync(
        PackagingCaller packaging, IMobileGeodatabaseWriter writer, MapArea area, PublishedService service, MapService named,
        MapAreaPackage? existing, CancellationToken cancellation)
    {
        IServiceProvider services = packaging.Services;
        MapAreaPackageFiles files = services.GetRequiredService<MapAreaPackageFiles>();
        RequestPrincipal caller = packaging.Principal;
        // The layers the map uses of the service, or all of them when the map names the service alone.
        IEnumerable<int> wanted = named.Layers.Count > 0 ? named.Layers : service.LayersAndTables.Select(l => l.LayerIndex);
        int[] layers = [.. wanted.Where(id => service.LayerOrTable(id) is not null).Distinct().Order()];

        if (layers.Length == 0)
        {
            return (null, new PackageRefusal(409, "None of the layers the map uses is a layer or table of this service."));
        }

        // ADR-290: the attachment tables record their changes from before the generation this package is taken at.
        await OfflinePackager.PrepareAttachmentsAsync(services, layers.Select(id => service.LayerOrTable(id)!), cancellation).ConfigureAwait(false);

        DateTimeOffset generation = Truncated(services.GetRequiredService<TimeProvider>().GetUtcNow());

        Func<string, string?> fields = RequestFields(
            ReplicaNameAt(generation), layers, area, existing is null ? null : PropertyOf(existing.Properties, "layerQueries"));

        if (!OfflineReplicaRequests.TryReadCreate(fields, service, out CreateReplicaRequest? request, out string? error))
        {
            return (null, new PackageRefusal(400, error!));
        }

        string serviceDocument = await Program.ServiceDocumentTextAsync(packaging.Carrier, service, cancellation).ConfigureAwait(false);
        (PackagePlan? plan, PackageRefusal? refusal) = await OfflinePackager.PlanAsync(
            services, service, request!, serviceDocument,
            id => Program.LayerDocumentTextAsync(packaging.Carrier, service, id, cancellation), cancellation).ConfigureAwait(false);

        if (plan is null)
        {
            return (null, refusal);
        }

        (string path, string recorded) = files.Next();
        Guid replicaId = Guid.NewGuid();
        string serviceUrl = named.Address.Length > 0
            ? named.Address
            : packaging.Origin + "/rest/services/" + service.QualifiedName + "/FeatureServer";

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
            // D-410: a basemap package still being written has a job; stopping it frees the budget it holds and the walk.
            if (package.ExportJobId is { } job && package.Status == MapAreas.PackageProcessing)
            {
                await CancelJobAsync(services, job).ConfigureAwait(false);
            }

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

    /// <summary>
    /// The packages taken from a service, to be put away with <see cref="DiscardAsync"/> once the service is deleted or its
    /// sync turned off (D-407); none when this deployment keeps no map areas.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The packages.</returns>
    public static async Task<IReadOnlyList<MapAreaPackage>> PackagesOfServiceAsync(IServiceProvider services, Guid serviceId, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetService<IMapAreaStore>() is { } store
            ? await store.PackagesOfServiceAsync(serviceId, cancellation).ConfigureAwait(false)
            : [];
    }

    /// <summary>
    /// Puts away the replicas and files of the packages a service's sync being turned off leaves behind, and marks their
    /// areas <see cref="MapAreas.NeedsRefresh"/> (D-407). The package items stay, as the migration 139 comment says they must
    /// for a device that holds one; their <c>/data</c> answers that the file is no longer held.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="packages">What <see cref="PackagesOfServiceAsync"/> read before the switch.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task PutAwayForSyncOffAsync(
        IServiceProvider services, IReadOnlyList<MapAreaPackage> packages, Guid serviceId, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(packages);

        if (packages.Count == 0)
        {
            return;
        }

        await DiscardAsync(services, packages, cancellation).ConfigureAwait(false);

        if (services.GetService<IMapAreaStore>() is { } store)
        {
            await store.MarkNeedsRefreshAsync(null, serviceId, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What restoring a web map or a service from the recycle bin does to its map areas (D-407 (3)): the rows come back, the
    /// files and replicas do not, so the areas are marked <see cref="MapAreas.NeedsRefresh"/> and refreshing one writes its
    /// packages again. INFERRED: marking is the simpler correct choice, because rebuilding on restore would need the
    /// restoring request's caller to own the new replicas and to be able to read every service the map names, and a restore
    /// that fails halfway for either reason must not undo the restore.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="kind">The entry's kind: <c>web map</c> or <c>service</c> mean something here, others nothing.</param>
    /// <param name="itemId">The restored item's id.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>How many areas were marked.</returns>
    public static async Task<int> MarkRestoredAsync(IServiceProvider services, string kind, Guid itemId, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService<IMapAreaStore>() is not { } store)
        {
            return 0;
        }

        return kind switch
        {
            "web map" => await store.MarkNeedsRefreshAsync(itemId.ToString("N"), null, cancellation).ConfigureAwait(false),
            "service" => await store.MarkNeedsRefreshAsync(null, itemId, cancellation).ConfigureAwait(false),
            _ => 0,
        };
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
        PackagingCaller packaging, WebMap map, IReadOnlyList<PublishedService> published, CancellationToken cancellation)
    {
        List<TileTarget> targets = [];
        List<SkippedService> skipped = [];
        RequestPrincipal caller = packaging.Principal;
        string origin = packaging.Origin;

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
    /// Starts the export job that writes a vector tile package of the area's extent for a vector tile service, and records the
    /// package as <c>processing</c> - ADR-288 section 11a, D-410. The levels are the ones <c>exportTiles</c>' ArcGIS-origin export
    /// keeps (those the service has, down to the deepest that fits the tile cap), the job is the exporter's own
    /// (<see cref="TileExporter"/>: the walk, the checkpoints, the lease, the export budget and the package writer), and what
    /// the area adds is only the end: <c>TileExporter.AdoptAsync</c> moves the finished file into the area's folder.
    /// </summary>
    /// <remarks>
    /// <paramref name="existing"/> is the package being refreshed, which keeps serving its old file until the new one is
    /// written; <paramref name="newTitle"/> is the package item's title when there is none.
    /// </remarks>
    private static async Task<(MapAreaPackage? Package, PackageRefusal? Refusal)> StartTilesAsync(
        PackagingCaller packaging, MapArea area, PublishedService service, string address, MapAreaPackage? existing, string? newTitle,
        CancellationToken cancellation)
    {
        IServiceProvider services = packaging.Services;
        RequestPrincipal caller = packaging.Principal;
        IMapAreaStore store = services.GetRequiredService<IMapAreaStore>();
        ITileExportStore exports = services.GetRequiredService<ITileExportStore>();
        HostSettings settings = services.GetRequiredService<HostSettings>();

        VectorTileExportEndpoints.Ask ask = new(
            TileExportFormat.Vtpk,
            TileLevelsOf(area, service),
            new AdminEndpoints.SeedExtent(area.XMin, area.YMin, area.XMax, area.YMax, new AdminEndpoints.SeedReference(area.Wkid, null)),
            "arcgis");

        (VectorTileExportEndpoints.Planned? planned, VectorTileExportEndpoints.Refusal? refusal) = await VectorTileExportEndpoints.PlanAsync(
            service, ask, settings, services.GetRequiredService<ServiceContexts>(),
            services.GetRequiredService<IProjector>(), services.GetRequiredService<ITileCache>(),
            services.GetRequiredService<GeoParquetSources>(), cancellation).ConfigureAwait(false);

        if (planned is null)
        {
            return (null, new PackageRefusal(refusal!.Status, refusal.Message));
        }

        // A refresh of a package whose job is still running replaces that job.
        if (existing is { ExportJobId: { } running, Status: MapAreas.PackageProcessing })
        {
            await CancelJobAsync(services, running).ConfigureAwait(false);
        }

        // Not woken yet: the package's row must exist before a worker can finish the job, or the finished file would be an
        // ordinary export's and expire in a day.
        (TileExportState? started, VectorTileExportEndpoints.Refusal? full) = await VectorTileExportEndpoints.StartAsync(
            exports, caller.Principal.Id, service, ask, planned, settings, services.GetRequiredService<JobSignal>(), cancellation,
            wake: false).ConfigureAwait(false);

        if (started is null)
        {
            return (null, new PackageRefusal(full!.Status, full.Message));
        }

        DateTimeOffset generation = Truncated(services.GetRequiredService<TimeProvider>().GetUtcNow());
        string properties = JsonSerializer.Serialize(new
        {
            url = address,
            format = "vtpk",
            levels = planned.Plan.Levels.Select(l => l.Z),
            tiles = planned.Plan.Total,
        });

        try
        {
            MapAreaPackage package;

            if (existing is null)
            {
                package = new MapAreaPackage(
                    Guid.NewGuid(), area.Id, area.MapId, service.Id, null, TileExportPackage.DownloadName(service, TileExportFormat.Vtpk),
                    string.Empty, string.Empty, 0, properties, generation, generation, MapAreas.VtpkFormat,
                    MapAreas.PackageProcessing, started.Job.Id);

                if (!await store.AddPackageAsync(package, newTitle ?? TitleOf(area.Title, address), cancellation).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The area no longer exists.");
                }
            }
            else
            {
                package = existing with
                {
                    Properties = properties, Status = MapAreas.PackageProcessing, ExportJobId = started.Job.Id, Error = null,
                };

                if (!await store.MarkPackageProcessingAsync(existing.Id, started.Job.Id, properties, cancellation).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The package no longer exists.");
                }
            }

            services.GetRequiredService<JobSignal>().Wake(JobKind.TileExport);
            return (package, null);
        }
        catch
        {
            await CancelJobAsync(services, started.Job.Id).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The levels a package is bounded to by the area's tile scale, or null for the service's own (D-405): from the service's
    /// first level down to the one whose scale reaches the area's smallest scale.
    /// </summary>
    internal static IReadOnlyList<int>? TileLevelsOf(MapArea area, PublishedService service)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(service);

        return LevelsForScales(service.TileScheme, area.TileScaleMin, area.TileScaleMax);
    }

    /// <summary>
    /// The levels of a grid that cover <c>mapAreaTileScale</c> (D-405): from the level at or just coarser than
    /// <paramref name="minScale"/> down to the level at or just finer than <paramref name="maxScale"/>, so a client drawing
    /// at either end of its range has tiles. Null when neither bound is set, which leaves the service's own levels.
    /// </summary>
    /// <param name="scheme">The service's grid.</param>
    /// <param name="minScale">The coarsest scale (the larger denominator), or null.</param>
    /// <param name="maxScale">The finest scale (the smaller denominator), or null.</param>
    /// <returns>The levels, ascending, or null.</returns>
    public static IReadOnlyList<int>? LevelsForScales(VectorTileScheme scheme, double? minScale, double? maxScale)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        if (minScale is null && maxScale is null)
        {
            return null;
        }

        int first = 0;

        if (minScale is { } coarse)
        {
            for (int z = 0; z <= scheme.MaxLevel; z++)
            {
                if (scheme.Scale(z) >= coarse)
                {
                    first = z;
                }
            }
        }

        int last = scheme.MaxLevel;

        if (maxScale is { } fine)
        {
            last = scheme.MaxLevel;

            for (int z = 0; z <= scheme.MaxLevel; z++)
            {
                if (scheme.Scale(z) <= fine)
                {
                    last = z;
                    break;
                }
            }
        }

        last = Math.Max(first, last);
        return [.. Enumerable.Range(first, last - first + 1)];
    }

    /// <summary>Stops an export job whose package is going away; a job already over is not an error.</summary>
    private static async Task CancelJobAsync(IServiceProvider services, Guid job)
    {
        try
        {
            if (services.GetService<ITileExportStore>() is { } exports)
            {
                await exports.CancelAsync(job, CancellationToken.None).ConfigureAwait(false);
            }

            services.GetService<TileExporter>()?.Stop(job);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The job is reclaimed by its lease or ends on its own; the sweep fails a package nothing finishes.
        }
    }

    /// <summary>
    /// Writes an existing tile package again from the tiles as they are now: starts its job. Answers the package as it is
    /// being written, or null after naming in <paramref name="skipped"/> why the old one was kept.
    /// </summary>
    private static async Task<MapAreaPackage?> RefreshTilesAsync(
        PackagingCaller packaging, MapArea area, MapAreaPackage old, PublishedService? service, string address, string name,
        List<SkippedService> skipped, CancellationToken cancellation)
    {
        RequestPrincipal caller = packaging.Principal;

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

        (MapAreaPackage? package, PackageRefusal? refusal) = await StartTilesAsync(
            packaging, area, service, address, old, newTitle: null, cancellation).ConfigureAwait(false);

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
        PackagingCaller packaging, IReadOnlyList<PublishedService> published, MapService named, CancellationToken cancellation)
    {
        RequestPrincipal caller = packaging.Principal;
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
            packaging.Carrier, service, packaging.Services.GetRequiredService<ServiceContexts>(), cancellation).ConfigureAwait(false);

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
