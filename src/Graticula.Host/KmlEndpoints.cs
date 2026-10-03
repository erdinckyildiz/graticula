using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's <c>generateKml</c> on a map or image service — ADR-164: a KML document, as KMZ by default, whose overlay
/// Google Earth redraws from the service's own WMS address each time the view stops moving.
/// </summary>
/// <remarks>
/// <para>
/// <b>A network link to the map, not a copy of the data</b> — ArcGIS's <i>composite</i> layer option, and the only
/// one this answers: the picture is drawn by the WMS this server already serves (ADR-162), so it carries the layers'
/// styles and stays current; a layer's features as KML are the layer's export (ADR-106).
/// </para>
/// <para>
/// <b>Google Earth sends no credentials</b>, so a service shared with fewer than everyone draws only for a client that
/// can sign in — said in the document's description, rather than discovered as a blank globe.
/// </para>
/// </remarks>
internal static class KmlEndpoints
{
    /// <summary>Maps <c>generateKml</c> under map and image services.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/generateKml", ["GET", "POST"], MapServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/generateKml", ["GET", "POST"], ImageServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }).ExecuteAsync(context);

    private static string? Folder(HttpContext context) =>
        context.Request.RouteValues.TryGetValue("folder", out object? folder) && folder is string text && text.Length > 0
            ? text
            : null;

    private static async Task MapServiceAsync(
        HttpContext context, string serviceName, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await ServiceLookup.ServiceAsync(context, catalog, serviceName, cancellation, Folder(context) ?? string.Empty)
                .ConfigureAwait(false) is not { } service)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        List<PublishedLayer> drawn = [.. service.Layers.Where(l => l.Definition.GeometryColumn is { Length: > 0 })];

        // `layers` names layer ids, as ArcGIS's does; every layer with a geometry when it names none.
        if (parameter("layers") is { Length: > 0 } ids)
        {
            HashSet<int> wanted = [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(i => int.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)];
            drawn = [.. drawn.Where(l => wanted.Contains(l.LayerIndex))];
        }

        if (drawn.Count == 0)
        {
            await RefuseAsync(context, 400, "`layers` names no layer of this service that has a geometry to draw.")
                .ConfigureAwait(false);
            return;
        }

        await AnswerAsync(context, parameter, service.QualifiedName, "MapServer", [.. drawn.Select(l => l.Definition.Name)],
            service.Sharing).ConfigureAwait(false);
    }

    private static async Task ImageServiceAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        string? folder = Folder(context);
        PublishedCoverage? coverage = await coverages.FindAsync(folder, serviceName, cancellation).ConfigureAwait(false);
        RequestPrincipal principal = context.Features.Get<RequestPrincipal>()
            ?? new RequestPrincipal(Graticula.Platform.Identity.Principal.Anonymous, null, Graticula.Platform.Identity.Authorization.Nothing);

        if (coverage is null || !LayerAccess.Evaluate(
                coverage.Sharing, coverage.Owner, principal.Principal, principal.Authorization, coverage.SharedWith).IsAllowed())
        {
            await RefuseAsync(context, 404, $"No image service '{serviceName}' is visible to you.").ConfigureAwait(false);
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        await AnswerAsync(context, parameter, coverage.QualifiedName, "ImageServer", [coverage.QualifiedName], coverage.Sharing)
            .ConfigureAwait(false);
    }

    /// <summary>The document: one ground overlay a service, its picture asked of the service's WMS for the view.</summary>
    private static async Task AnswerAsync(
        HttpContext context, Func<string, string?> parameter, string qualified, string kind, IReadOnlyList<string> layers,
        Graticula.Platform.Identity.SharingScope sharing)
    {
        string name = parameter("docName") is { Length: > 0 } given ? given : qualified;
        string wms = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/rest/services/{qualified}/{kind}/WMSServer";

        // WMS 1.1.1, whose EPSG:4326 is longitude first — the order Google Earth's [bboxWest],[bboxSouth] fill in.
        string href = $"{wms}?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&SRS=EPSG:4326&STYLES=&FORMAT=image/png&TRANSPARENT=TRUE"
            + $"&LAYERS={Uri.EscapeDataString(string.Join(",", layers))}";
        string description = sharing == Graticula.Platform.Identity.SharingScope.Public
            ? $"{qualified}, drawn by this server's WMS as the view changes."
            : $"{qualified} is not shared with everyone, and Google Earth does not sign in: it draws only where the "
                + "server can tell who is asking.";

        string kml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document>
                <name>{SecurityElement.Escape(name)}</name>
                <description>{SecurityElement.Escape(description)}</description>
                <GroundOverlay>
                  <name>{SecurityElement.Escape(qualified)}</name>
                  <Icon>
                    <href>{SecurityElement.Escape(href)}</href>
                    <viewRefreshMode>onStop</viewRefreshMode>
                    <viewRefreshTime>1</viewRefreshTime>
                    <viewBoundScale>1</viewBoundScale>
                    <viewFormat>BBOX=[bboxWest],[bboxSouth],[bboxEast],[bboxNorth]&amp;WIDTH=[horizPixels]&amp;HEIGHT=[vertPixels]</viewFormat>
                  </Icon>
                  <LatLonBox><north>85</north><south>-85</south><east>180</east><west>-180</west></LatLonBox>
                </GroundOverlay>
              </Document>
            </kml>
            """;

        string file = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

        // KMZ unless KML is asked for, as ArcGIS's generateKml answers.
        if (string.Equals(parameter("f"), "kml", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/vnd.google-earth.kml+xml";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{file}.kml\"";
            await context.Response.WriteAsync(kml).ConfigureAwait(false);
            return;
        }

        using MemoryStream zip = new();

        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using Stream entry = archive.CreateEntry("doc.kml").Open();
            byte[] bytes = Encoding.UTF8.GetBytes(kml);
            await entry.WriteAsync(bytes).ConfigureAwait(false);
        }

        context.Response.ContentType = "application/vnd.google-earth.kmz";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{file}.kmz\"";
        await context.Response.Body.WriteAsync(zip.ToArray()).ConfigureAwait(false);
    }
}
