using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Formats;
using GisServer.Geometries;
using GisServer.Platform.Admin;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GisServer.Host;

/// <summary>
/// Uploading data and getting a service back.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what "hosted" was always supposed to mean.</b> Until now a hosted
/// layer meant a table that already existed in the datastore, which requires the
/// person publishing it to have already got their data into PostgreSQL — the
/// exact step the product exists to remove for somebody who has a GeoJSON file
/// and no DBA.
/// </para>
/// <para>
/// <b>One call, not two.</b> ArcGIS separates uploading an item from publishing
/// a service, which is right when items have a life of their own — they can be
/// shared, versioned, re-published. Nothing here has that, so two calls would be
/// ceremony around a single act, and the second one would exist mainly to be
/// forgotten.
/// </para>
/// <para>
/// <b>GeoJSON only, and the constraint is
/// <see href="../../docs/security.md">security.md</see>'s.</b> Its upload rules
/// say archives are never opened — <em>decompression bombs are not our problem
/// if we never decompress</em> — and a shapefile is a ZIP of at least three
/// files. Accepting one means writing an exception to that rule, which is a
/// decision rather than a feature.
/// </para>
/// </remarks>
internal static class HostedDataEndpoints
{
    /// <summary>
    /// The largest upload accepted, before parsing.
    /// </summary>
    /// <remarks>
    /// <b>Enforced on the stream, not after reading it.</b> A cap checked once
    /// the body is in memory has already let the caller allocate it. 64 MB of
    /// GeoJSON is a few hundred thousand features, which is far past what
    /// anybody uploads through a browser and inside what this can parse without
    /// becoming the allocation problem A-037 measured.
    /// </remarks>
    public const long MaximumBytes = 64L * 1024 * 1024;

    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/hosted", ImportAsync).DisableAntiforgery();
    }

    /// <summary>
    /// Reads an uploaded file, makes a table from it, and publishes a service.
    /// </summary>
    /// <remarks>
    /// <b>The privilege is the publisher's.</b> Creating a table in the datastore
    /// is a content act, not an operational one — the same privilege that
    /// publishes an existing table, because the outcome is the same kind of
    /// thing. What it is <em>not</em> is <c>admin:manageServer</c>: hosting data
    /// must not require the account that can stop services.
    /// </remarks>
    private static async Task ImportAsync(
        HttpContext context,
        PostGisImporter importer,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await Fail(context, 400,
                "Post the file as multipart/form-data with fields 'name' and 'file'.")
                .ConfigureAwait(false);
            return;
        }

        IFormCollection form;

        try
        {
            form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        }
        catch (System.IO.InvalidDataException e)
        {
            await Fail(context, 413,
                $"The upload is larger than this server accepts ({MaximumBytes / 1048576} MB). "
                + $"({e.Message})").ConfigureAwait(false);
            return;
        }

        string? name = form["name"].ToString();

        if (string.IsNullOrWhiteSpace(name))
        {
            await Fail(context, 400, "'name' is required and becomes the service name.")
                .ConfigureAwait(false);
            return;
        }

        IFormFile? file = form.Files.GetFile("file");

        if (file is null)
        {
            await Fail(context, 400, "A 'file' part is required, containing GeoJSON.")
                .ConfigureAwait(false);
            return;
        }

        if (file.Length > MaximumBytes)
        {
            await Fail(context, 413,
                $"The file is {file.Length / 1048576} MB and the limit is "
                + $"{MaximumBytes / 1048576} MB.").ConfigureAwait(false);
            return;
        }

        // <b>The client's content type is not trusted</b> (security.md): the
        // document is parsed and either is GeoJSON or is not. A .json extension
        // and a text/plain header say nothing either way.
        JsonElement json;

        try
        {
            await using System.IO.Stream stream = file.OpenReadStream();

            json = (await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 32 },
                cancellation).ConfigureAwait(false)).RootElement;
        }
        catch (JsonException e)
        {
            // MaxDepth is the defence against a document nested deeply enough to
            // exhaust the stack — a parser bomb that costs the attacker almost
            // nothing to write.
            await Fail(context, 400, $"The file is not valid JSON: {e.Message}")
                .ConfigureAwait(false);
            return;
        }

        if (!GeoJsonFeatures.TryRead(json, ImportLimits.Default, out ImportedDataset? dataset,
                out string? error))
        {
            await Fail(context, 400, error!).ConfigureAwait(false);
            return;
        }

        SharingScope sharing = ParseSharing(form["sharing"].ToString());

        if (sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic)
                .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        Guid datastore = await DatastoreIdAsync(catalog, cancellation).ConfigureAwait(false);

        if (datastore == Guid.Empty)
        {
            await Fail(context, 503,
                "The datastore is not registered as a data source, so there is nowhere to host "
                + "data. This is registered automatically at startup; check the server log.")
                .ConfigureAwait(false);
            return;
        }

        // <b>Checked before importing, and checked again by the database.</b>
        // This is a race and the unique constraint is the real guard — but
        // without it, uploading a file under a name already in use loads every
        // feature into a new table and then throws it away. On a large file
        // that is a long wait for an answer that was knowable at the start.
        if (await NameTakenAsync(catalog, name, cancellation).ConfigureAwait(false))
        {
            await Fail(context, 409,
                $"A layer named '{name}' already exists. Nothing was imported. Choose another "
                + "name, or unpublish the existing layer first.").ConfigureAwait(false);
            return;
        }

        ImportResult result = await importer
            .ImportAsync(dataset!, name, cancellation)
            .ConfigureAwait(false);

        Guid layerId;

        try
        {
            layerId = await catalog.PublishLayerAsync(
                new LayerPublication(
                    name,
                    datastore,
                    result.SchemaName,
                    result.TableName,
                    "geom",
                    "objectid",
                    "objectid",
                    result.StoredSrid,
                    dataset!.GeometryType,
                    sharing),
                current.Principal.Id,
                cancellation).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
        {
            // Lost the race against another upload of the same name. A conflict,
            // not a fault — and until this was handled it fell through to the
            // catch-all mapping and was answered "a database this server depends
            // on is unreachable", which sends somebody to check their network
            // over a name they can change.
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None)
                .ConfigureAwait(false);

            await Fail(context, 409,
                $"A layer named '{name}' was created by another request while this one was "
                + "importing. Nothing was kept.").ConfigureAwait(false);
            return;
        }
        catch (Exception)
        {
            // <b>The table goes if the publish fails.</b> Otherwise the upload
            // leaves somebody's data in the datastore with no service pointing
            // at it, counting against their quota, invisible to every interface
            // — and they would upload it again.
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                "layer.import",
                name,
                Detail(result, dataset!),
                true),
            cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;

        await Results.Json(new
        {
            id = layerId,
            name,
            table = $"{result.SchemaName}.{result.TableName}",
            rows = result.Rows,
            geometryType = dataset!.GeometryType.ToString(),
            fields = dataset.Columns.Select(c => new { c.Name, type = c.Type.ToString() }),
            sharing = sharing.ToString().ToLowerInvariant(),

            // Said out loud because it is a change to the caller's data. ArcGIS
            // Online does the same and does not mention it, which is how people
            // are surprised later.
            storedIn = new
            {
                sourceSR = result.SourceSrid,
                storedSR = result.StoredSrid,
                engine = result.ProjEngine,
                note = result.SourceSrid == result.StoredSrid
                    ? "Stored as uploaded."
                    : $"Reprojected from {result.SourceSrid} to {result.StoredSrid} once, on the "
                      + "way in, so the layer can serve vector tiles. EPSG:4326 to EPSG:3857 is a "
                      + "closed formula with no datum shift, so nothing was lost.",
            },
            services = new
            {
                feature = $"/rest/services/{name}/FeatureServer",
                tiles = $"/rest/services/{name}/VectorTileServer",
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Whether a layer of that name is already published.</summary>
    private static async Task<bool> NameTakenAsync(
        IAdminCatalog catalog, string name, CancellationToken cancellation)
    {
        foreach (AdminLayer layer in
                 await catalog.ListLayersAsync(cancellation).ConfigureAwait(false))
        {
            if (string.Equals(layer.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The datastore's data source id, or empty when it is not registered.</summary>
    private static async Task<Guid> DatastoreIdAsync(
        IAdminCatalog catalog, CancellationToken cancellation)
    {
        foreach (RegisteredDataSource source in
                 await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false))
        {
            if (string.Equals(source.Name, PostgresAdminCatalog.DatastoreName, StringComparison.Ordinal))
            {
                return source.Id;
            }
        }

        return Guid.Empty;
    }

    /// <summary>
    /// The sharing scope, defaulting to private.
    /// </summary>
    /// <remarks>
    /// ADR-018's closed default, and it matters more here than on an ordinary
    /// publish: somebody uploading a file has not yet seen what the service looks
    /// like, and a default of *organisation* would share data before its owner
    /// had confirmed it imported correctly.
    /// </remarks>
    private static SharingScope ParseSharing(string? raw) => raw?.ToLowerInvariant() switch
    {
        "public" => SharingScope.Public,
        "organization" or "organisation" => SharingScope.Organization,
        _ => SharingScope.Private,
    };

    private static string Detail(ImportResult result, ImportedDataset dataset) =>
        JsonSerializer.Serialize(new
        {
            table = $"{result.SchemaName}.{result.TableName}",
            result.Rows,
            columns = dataset.Columns.Count,
            geometryType = dataset.GeometryType.ToString(),
            result.SourceSrid,
            result.StoredSrid,
        });

    private static Task Fail(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: code)
            .ExecuteAsync(context);
}
