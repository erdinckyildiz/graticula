using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// A layer taken away as a GeoPackage, a zipped shapefile or an Excel workbook — ADR-107.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rows are written here, the file is made by the reader.</b> This process reads the layer through its own
/// feature source, page by page, into a GeoJSON file in the layer's stored reference, named in the legacy
/// <c>crs</c> member; the import reader, which is where GDAL lives (ADR-009 §2.2), translates that into the format
/// asked for. Nothing is reprojected, so a TUREF layer leaves as TUREF with its <c>.prj</c>. <b>The rows are read by
/// <see cref="LayerExportRows"/></b>, which <see cref="FeatureExporter"/> — ADR-106's job — reads them with too, so
/// the two routes cannot come to disagree about what a layer's file holds.
/// </para>
/// <para>
/// <b>Who may — owner decision 2026-10-01, relayed through ADR-106.</b> The layer's owner and administrators always;
/// another signed-in reader only when the owner has <em>chosen</em> Extract in Settings › Feature layer (unchosen is
/// off — an unset ceiling is not a yes); nobody anonymous, ever.
/// </para>
/// <para>
/// <b>POST, and not by a cookie from another site</b> — ADR-098 §5.7's rule, the same check <c>exportTiles</c> has:
/// an image tag on another page must not be able to spend this server's time in an administrator's name. <b>One at a
/// time</b>, since it runs in the request.
/// </para>
/// </remarks>
internal static class LayerExportEndpoints
{
    /// <summary>The most rows one export writes; past it the answer says so rather than cutting silently.</summary>
    internal const int MaximumRows = 1_000_000;

    /// <summary>One export at a time: it holds a request, a scratch file and a child process while it runs.</summary>
    private static readonly SemaphoreSlim Running = new(1, 1);

    private static readonly Dictionary<string, (string Extension, string Type)> Formats =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpkg"] = (".gpkg", "application/geopackage+sqlite3"),
            ["shapefile"] = (".zip", "application/zip"),
            ["xlsx"] = (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),

            // ADR-106's formats, on ADR-107's road. A File Geodatabase is a folder, zipped as the shapefile's is.
            ["fgdb"] = (".gdb.zip", "application/zip"),
            ["kml"] = (".kml", "application/vnd.google-earth.kml+xml"),
            ["csv"] = (".csv", "text/csv; charset=utf-8"),
            ["geojson"] = (".geojson", "application/geo+json"),

            // ADR-106's Feature Collection: a FeatureSet as `query?f=json` answers it, written here, not by GDAL, which
            // reads Esri JSON and does not write it.
            ["esrijson"] = (".json", "application/json"),
        };


    /// <summary>Maps the route.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/services/{serviceName}/layers/{layerId:int}/export", ExportAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    private static async Task ExportAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        GeodatabaseReader reader,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        // Before anything else, so a forged request learns nothing and costs nothing (ADR-098 §5.7).
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is { } forged)
        {
            await RefuseAsync(context, 403, forged.Replace("exportTiles", "An export", StringComparison.Ordinal)).ConfigureAwait(false);
            return;
        }

        string format = context.Request.Query["format"].ToString().Trim().ToLowerInvariant();

        if (!Formats.TryGetValue(format, out (string Extension, string Type) kind))
        {
            await RefuseAsync(context, 400, $"'format' is one of {string.Join(", ", Formats.Keys.Select(k => $"'{k}'"))}.")
                .ConfigureAwait(false);
            return;
        }

        string? folder = context.Request.Query["folder"].ToString() is { Length: > 0 } f ? f : null;

        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation, folder ?? string.Empty)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        if (service.Layer(layerId) is not { } layer)
        {
            await RefuseAsync(context, 404, $"There is no layer {layerId} in '{service.Name}'.").ConfigureAwait(false);
            return;
        }

        // Nobody anonymous; another reader only on the owner's explicit Extract; the owner and administrators always.
        if (context.Features.Get<RequestPrincipal>() is not { Principal.IsAnonymous: false })
        {
            await RefuseAsync(context, 401, "Sign in to export a layer.").ConfigureAwait(false);
            return;
        }

        // The ceiling as served folds the owner's choice in, and an owner who has not chosen offers no Extract;
        // Program.OffersExtract is the same question the capability string asks (ADR-106 §5.5).
        bool chosen = layer.CapabilityCeiling?.Contains("Extract") == true;

        if (!chosen && !await AdminEndpoints.ManagesAsync(
                context, layer.Owner, layer.Sharing, layer.SharedWith, layer.Definition.Name, "export")
            .ConfigureAwait(false))
        {
            return;
        }

        if (!await Running.WaitAsync(TimeSpan.Zero, cancellation).ConfigureAwait(false))
        {
            await RefuseAsync(context, 503, "Another export is being written on this server. Try again when it is done.")
                .ConfigureAwait(false);
            return;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        string work = Path.Combine(scratch.Directory, "exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            if (format == "esrijson")
            {
                await WriteEsriJsonAsync(context, layer, source, described, work, kind, cancellation).ConfigureAwait(false);
                return;
            }

            string input = Path.Combine(work, "rows.geojson");
            int srid = layer.Definition.Srid;
            long written;

            await using (FileStream staged = File.Create(input))
            {
                written = await LayerExportRows.WriteGeoJsonAsync(
                        staged, source, described, layer, srid, MaximumRows, FeatureQuery.MaximumLimit, null, cancellation)
                    .ConfigureAwait(false);
            }

            if (written > MaximumRows)
            {
                await RefuseAsync(context, 413,
                    $"'{layer.Definition.Name}' has more than {MaximumRows:N0} features, which is more than one export writes. "
                    + "Filter it, or take the tile packages from Settings › Tile layer.").ConfigureAwait(false);
                return;
            }

            string name = LayerExportRows.SafeName(layer.Definition.Name);
            string output = Path.Combine(work, format switch
            {
                "shapefile" => "shapefile",

                // The folder's own name is what ArcGIS Pro shows, so it is the layer's, with the suffix it needs.
                "fgdb" => Path.Combine("fgdb", name + ".gdb"),
                _ => name + kind.Extension,
            });

            // GDAL makes the .gdb folder but not the one it sits in.
            if (format == "fgdb") Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            using JsonDocument answer = await reader.AskAsync(
                new { op = "export", @in = input, @out = output, format, layer = name },
                TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false);

            if (!answer.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
            {
                string why = answer.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "" : "";
                await RefuseAsync(context, 500, $"The file could not be written. {why}").ConfigureAwait(false);
                return;
            }

            string file = output;

            if (format == "shapefile")
            {
                file = Path.Combine(work, name + ".zip");
                ZipFile.CreateFromDirectory(output, file);
            }
            else if (format == "fgdb")
            {
                // With the .gdb folder itself inside, as ArcGIS Online's File Geodatabase download is: unzipped, it
                // is a geodatabase and not a loose set of tables.
                file = Path.Combine(work, name + kind.Extension);
                ZipFile.CreateFromDirectory(output, file, CompressionLevel.Optimal, includeBaseDirectory: true);
            }

            context.Response.ContentType = kind.Type;
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}{kind.Extension}\"";
            context.Response.Headers.CacheControl = "no-store";

            await using FileStream stream = File.OpenRead(file);
            context.Response.ContentLength = stream.Length;
            await stream.CopyToAsync(context.Response.Body, cancellation).ConfigureAwait(false);
        }
        catch (InvalidOperationException unavailable)
        {
            // The reader is not installed, or ran past its deadline.
            await RefuseAsync(context, 503, unavailable.Message).ConfigureAwait(false);
        }
        finally
        {
            Running.Release();

            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a scratch folder that will not go now goes with the scratch directory */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The layer as one Esri JSON FeatureSet — ADR-106's answer to ArcGIS Online's Feature Collection.
    /// </summary>
    /// <remarks>
    /// <b>Written by the query writer</b>, so its fields, types, aliases, object ids and dates are exactly what
    /// <c>query?f=json</c> answers, in the layer's own reference; <see cref="WholeLayerSource"/> hands it every page as
    /// one read, and the writer leaves <c>exceededTransferLimit</c> out because a file has no next page.
    /// </remarks>
    private static async Task WriteEsriJsonAsync(
        HttpContext context, PublishedLayer layer, IFeatureSource source, LayerDescription described, string work,
        (string Extension, string Type) kind, CancellationToken cancellation)
    {
        string name = LayerExportRows.SafeName(layer.Definition.Name);
        string file = Path.Combine(work, name + kind.Extension);
        long written;

        await using (FileStream stream = File.Create(file))
        {
            written = await LayerExportRows.WriteEsriJsonAsync(stream, layer, source, described, MaximumRows, cancellation)
                .ConfigureAwait(false);
        }

        if (written > MaximumRows)
        {
            await RefuseAsync(context, 413,
                $"'{layer.Definition.Name}' has more than {MaximumRows:N0} features, which is more than one export writes.")
                .ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = kind.Type;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}{kind.Extension}\"";
        context.Response.Headers.CacheControl = "no-store";

        await using FileStream sent = File.OpenRead(file);
        context.Response.ContentLength = sent.Length;
        await sent.CopyToAsync(context.Response.Body, cancellation).ConfigureAwait(false);
    }

    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code).ExecuteAsync(context);
}
