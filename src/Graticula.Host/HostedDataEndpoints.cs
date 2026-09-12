using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Formats;
using System.Text;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// Making hosted feature classes: from a file, or from a schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosted means the datastore holds the data.</b> It is a statement about
/// where a feature class lives and who owns it — not about how it got there.
/// There are two ways in and both end in the same place:
/// </para>
/// <list type="bullet">
/// <item><c>POST /admin/hosted/import</c> — a file becomes a feature class.</item>
/// <item><c>POST /admin/hosted/define</c> — a schema becomes an empty one, filled
/// afterwards through <c>applyEdits</c>. A survey layer, an incident log,
/// anything collected rather than converted starts this way.</item>
/// </list>
/// <para>
/// The distinction that matters is not file-versus-form, it is <b>hosted versus
/// registered</b>: a hosted feature class is ours to create, alter and drop; a
/// registered one points at a table in somebody else's database and must never
/// be touched. That is why hosted services live under
/// <c>/rest/services/hosted</c> and registered ones do not.
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

        app.MapPost("/admin/hosted/import", ImportAsync).DisableAntiforgery();

        // <b>The other half of a geodatabase upload.</b> The upload opens an inspection and answers
        // with what is inside; this takes the feature classes chosen from that answer and publishes
        // them into one service. ADR-038.
        app.MapPost("/admin/hosted/geodatabase", PublishGeodatabaseAsync);
        app.MapPost("/admin/hosted/define", DefineAsync);

        /*
          <b>ADR-058 §5b: the datastore's schema is edited here, and here only.</b> Owner
          instruction 2026-09-08 — nobody connects to the datastore to change a column. Two
          operations and no third: a rename is add-copy-delete and a retype is a data migration,
          so neither is offered as one press.

          <b>Under `/admin`, not on the layer's feature address.</b> DDL on a read surface would
          make `content:publishFeatures` carry a meaning it was not designed around, and ArcGIS
          puts the same three operations on a separate admin endpoint for the same reason.
        */
        app.MapPost("/admin/hosted/{layer}/fields", AddFieldAsync);
        app.MapDelete("/admin/hosted/{layer}/fields/{field}", DropFieldAsync);

        // ADR-064: Portal's four editor-tracking columns, added and given their roles at once.
        app.MapPost("/admin/hosted/{layer}/editor-tracking", TrackEditsAsync);

        // The original path, kept working. It was only ever the import, and
        // moving it silently would break the one thing already built against it.
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
        IJobStore jobs,
        JobSignal signal,
        GeodatabaseReader reader,
        ImportScratch scratch,
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
        // bytes are read and either are a ZIP, or GeoJSON, or neither. A .zip
        // extension and an application/octet-stream header say nothing.
        ImportedDataset? dataset;
        string? error;

        byte[] head = new byte[4];
        int peeked;

        await using (System.IO.Stream probe = file.OpenReadStream())
        {
            peeked = await probe.ReadAsync(head, cancellation).ConfigureAwait(false);
        }

        if (peeked == 4 && BoundedArchive.LooksLikeZip(head))
        {
            (bool ok, ImportedDataset shapes) = await TryShapefileAsync(
                context, form, file, jobs, signal, reader, scratch, cancellation)
                .ConfigureAwait(false);

            if (!ok)
            {
                // TryShapefileAsync has already written the refusal.
                return;
            }

            dataset = shapes;
        }
        else
        {
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
                // MaxDepth is the defence against a document nested deeply enough
                // to exhaust the stack — a parser bomb that costs the attacker
                // almost nothing to write.
                await Fail(context, 400,
                    $"The file is neither a ZIP nor valid JSON: {e.Message}")
                    .ConfigureAwait(false);
                return;
            }

            if (!GeoJsonFeatures.TryRead(json, ImportLimits.Default, out dataset, out error))
            {
                await Fail(context, 400, error!).ConfigureAwait(false);
                return;
            }
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

        PublishedLayerAddress published;

        try
        {
            published = await catalog.PublishLayerAsync(
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

        // <b>D-53: the import says what it wrote, including what PostGIS thinks of it.</b>
        // Until 2026-08-18 this reported a row count and nothing else, and
        // `hosted.tr_ilce_511f6767` went out with 18 invalid geometries in 25,280 that
        // nobody learned about until another server refused to publish the table.
        //
        // <b>Asked after the commit rather than before it.</b> The scan is a full pass
        // over the geometry — there is no index that can answer it — so doing it inside
        // the import transaction would hold a write lock for the length of a sequential
        // scan. And the answer does not change what is stored: this server does not
        // repair silently (see GeometryValidity), so the report is the whole product.
        //
        // <b>It cannot fail the import.</b> An import that wrote everything and then
        // could not count is still an import that wrote everything, and answering 500
        // after a successful commit would send somebody looking for data that is there.
        GeometryValidity? validity = null;

        try
        {
            validity = await importer
                .ValidityOfAsync(result.SchemaName, result.TableName, cancellation)
                .ConfigureAwait(false);
        }
        catch (Npgsql.NpgsqlException)
        {
            // Reported as unmeasured below, which is honest and is not a failure.
        }

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                "layer.import",
                name,
                Detail(result, dataset!, validity),
                true),
            cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;

        string? warning = context.Items.TryGetValue(WarningKey, out object? note)
            ? note as string
            : null;

        await Results.Json(new
        {
            id = published.Id,
            name,
            warning,
            table = $"{result.SchemaName}.{result.TableName}",
            rows = result.Rows,
            geometryType = dataset!.GeometryType.ToString(),
            fields = dataset.Columns.Select(c => new { c.Name, type = c.Type.ToString() }),
            sharing = sharing.ToString().ToLowerInvariant(),

            // <b>What PostGIS makes of what we wrote (D-53).</b> Reported rather than
            // repaired: ST_MakeValid can drop a ring, split a polygon, or turn an area
            // into a line, and a server that hands back different geometry from what it
            // was given is one nobody can reconcile against their source.
            geometry = validity is null
                ? new
                {
                    valid = (bool?)null,
                    invalid = (long?)null,
                    reasons = Array.Empty<string>(),
                    note = "The validity scan did not complete, so this says nothing about the "
                         + "geometry. Everything that was uploaded was written.",
                }
                : new
                {
                    valid = (bool?)validity.AllValid,
                    invalid = (long?)validity.Invalid,
                    reasons = validity.Reasons.ToArray(),
                    note = validity.Explanation,
                },

            // <b>Nothing is reprojected on the way in any more.</b> Owner
            // correction 2026-08-15. The previous version transformed every
            // import to Web Mercator and reported "EPSG:4326 to EPSG:3857 is a
            // closed formula with no datum shift, so nothing was lost" — a
            // sentence about 4326 printed over a national-grid import, where it
            // is false and the survey coordinates were already gone.
            storedIn = new
            {
                sourceSR = result.SourceSrid,
                storedSR = result.StoredSrid,
                note = "Stored in the reference it arrived in. Vector tiles are cut on the Web "
                     + "Mercator grid, so the tile path transforms per request and caches the "
                     + "result; the stored coordinates are the ones you uploaded.",
            },
            services = Services(published.ServiceName, published.LayerIndex),
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


    /// <summary>
    /// Reads a shapefile out of an uploaded ZIP, or writes the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only place in the server that opens an archive</b>, and it
    /// is a deliberate exception to security.md's <em>never decompress</em>
    /// rule, taken by the owner in Q-98. The bounds that buy the exception are in
    /// <see cref="BoundedArchive"/>; what happens here is choosing the one
    /// shapefile, settling the encoding, and resolving the spatial reference.
    /// </para>
    /// <para>
    /// <b>The .prj is not parsed.</b> It is WKT, and matching WKT to an EPSG
    /// code by string comparison is how a layer ends up declared as something it
    /// is not — the same authority writes several spellings of the same system.
    /// The caller states the SRID; the .prj is echoed back so they can see what
    /// the file claimed and disagree.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Where the shapefile path leaves a note for the response to pick up.
    /// </summary>
    /// <remarks>
    /// <b><c>HttpContext.Items</c>, not a static dictionary.</b> The first
    /// version of this was a static map keyed by the context — which is global
    /// mutable state on a request path and leaks an entry whenever a request
    /// fails between writing and reading. Items is per-request and goes when the
    /// request does.
    /// </remarks>
    private const string WarningKey = "import.warning";

    private static async Task<(bool Ok, ImportedDataset Dataset)> TryShapefileAsync(
        HttpContext context,
        IFormCollection form,
        IFormFile file,
        IJobStore jobs,
        JobSignal signal,
        GeodatabaseReader reader,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        // <b>Recognised before it is attempted, and the first version did it afterwards.</b> Putting
        // this after the shapefile attempt failed twice over. A geodatabase is a *directory* named
        // `x.gdb`, so `BoundedArchive` refuses it at the folder rule long before assembly is reached —
        // and that refusal reads *"zip the shapefile's files directly rather than the folder holding
        // them"*, which is advice that cannot be followed for a format whose whole shape is a folder.
        // And the second `OpenReadStream()` came back unusable, so the recogniser silently answered
        // *nothing recognised* for every archive. Measured, not reasoned: a `.gdb.zip`, a `.gpkg` in a
        // zip and a `.kml` in a zip were all refused with the generic sentence.
        //
        // One open, at position zero, before anything consumes it.
        ForeignArchive foreign;

        await using (System.IO.Stream looking = file.OpenReadStream())
        {
            foreign = RecogniseArchive(looking);
        }

        // <b>A geodatabase is work rather than a refusal, when the reader shipped.</b> It is read by a
        // child process minutes after this request has been answered, so the request cannot carry the
        // answer — it opens a job and says where to watch it. ADR-011 §3.2 decided the claim protocol;
        // this is the first kind of work that uses it.
        if (foreign == ForeignArchive.Geodatabase && reader.Available)
        {
            await OpenInspectAsync(context, jobs, signal, scratch, file, cancellation)
                .ConfigureAwait(false);
            return (false, null!);
        }

        if (foreign != ForeignArchive.None)
        {
            await Fail(context, 400, Refusal(foreign)).ConfigureAwait(false);
            return (false, null!);
        }

        await using System.IO.Stream archive = file.OpenReadStream();

        if (!BoundedArchive.TryRead(
                archive,
                ShapefileBundle.Extensions,
                ArchiveLimits.ForShapefile,
                out IReadOnlyList<ArchiveMember> members,
                out string? archiveError))
        {
            await Fail(context, 400, archiveError!).ConfigureAwait(false);
            return (false, null!);
        }

        if (!ShapefileBundle.TryAssemble(members, out ShapefileBundle bundle, out string? bundleError))
        {
            await Fail(context, 400, bundleError!).ConfigureAwait(false);
            return (false, null!);
        }

        if (!bundle.TryEncoding(
                form["encoding"].ToString(), out Encoding encoding, out string? encodingError))
        {
            await Fail(context, 400, encodingError!).ConfigureAwait(false);
            return (false, null!);
        }

        string requestedSrid = form["srid"].ToString();

        if (!int.TryParse(requestedSrid, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int srid))
        {
            // <b>Asked rather than demanded, when there is something able to answer.</b> ADR-024 made
            // `srid` mandatory because a `.prj` is bare WKT and matching WKT to a code **by comparing
            // strings** is how a layer comes to declare a system it is not in. That reasoning is intact.
            // What changed is that there is now a projection database in this product: the reader
            // resolves a coordinate system through PROJ's authority tables, which is what the
            // geodatabase path has been doing since it was built — and asking an authority is not
            // guessing.
            //
            // <b>Measured before it was believed.</b> Six archives from
            // `tests/Graticula.Core.Tests/corpus/shapefile`, whose `.prj` files are **Esri dialect** —
            // `GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",…]]`, what ArcGIS writes rather than what OGC
            // specifies — all resolved to 4326. What is **not** measured is a projected Esri `.prj`,
            // because this project has no such file it did not write, so the refusal below is still the
            // path when PROJ cannot answer. ADR-024 is amended for it, Q-98.
            // <b>Only when there is a `.prj` to resolve, and the reason is weaker than I first wrote
            // it.</b> The comment here claimed GDAL invents EPSG:4326 for an archive with no
            // projection, which would have made this gate load bearing. Measured instead —
            // `ProjectionResolutionTests` builds a real shapefile with its `.prj` left out — and GDAL
            // is honest: no declaration, no coordinate system. So the gate is belt and braces, and
            // that test is where it is recorded, with an instruction to keep the gate if a future
            // version starts assuming.
            //
            // <b>It stays because it is free and it is the right shape.</b> The archive's own `.prj` is
            // the licence to ask an authority about it; with no `.prj` there is nothing to ask about,
            // and one child process is saved on the path that was going to be refused anyway.
            srid = bundle.Prj is null
                ? 0
                : await ResolveSridAsync(file, reader, scratch, cancellation).ConfigureAwait(false);

            if (srid <= 0)
            {
                await Fail(context, 400,
                    "'srid' is required for this shapefile. The .prj beside it is WKT rather than an "
                    + "EPSG code, and matching WKT to a code by comparing strings is how a layer "
                    + "comes to be declared as a system it is not in — so this server asks the "
                    + "projection database instead of guessing, and this time it had no answer."
                    + (bundle.Prj is null
                        ? " This archive has no .prj at all, so there was nothing to resolve."
                        : $" The .prj in this archive says: {Shorten(bundle.Prj)}")
                    + " Give the code and the import proceeds.")
                    .ConfigureAwait(false);

                return (false, null!);
            }
        }

        /*
          <b>Parsed in the child process, which is [D-113](../../docs/architecture-debt.md).</b>
          1,094 lines of our own shapefile parser used to run here — inside the process that
          serves public requests — under a decision taken three days before ADR-037 §5a moved
          GDAL out for the stated reason that it *"removes an untrusted-file parser from the
          process that serves public requests"*. Two archive formats from the same untrusted
          upload were parsed on opposite sides of a boundary drawn on purpose, and the
          independent §66 simplicity gate named it as its disqualifying finding.

          <b>What stays here is the archive's bounds.</b> `BoundedArchive` and
          `ShapefileBundle` above have already refused a bomb, a nested archive, a folder and
          a bundle with two shapefiles in it, and `TryEncoding` has already refused a
          character set this server cannot name. Those are limits on an upload rather than a
          parse of its contents.

          <b>The upload is written to scratch, because a child process reads a file.</b>
          `ResolveSridAsync` above already does this for the `.prj`, and doing it twice would
          be two copies of a file an operator uploaded once — so it is kept here and reused.
        */
        Guid kept = Guid.NewGuid();
        string? path = null;

        try
        {
            path = await scratch.KeepAsync(file, kept, cancellation).ConfigureAwait(false);

            (ImportedDataset? read, bool dropped, string? readError) =
                await ShapefileViaReader.ReadAsync(
                    reader,
                    path,
                    srid,
                    encoding.WebName,
                    ImportLimits.Default,
                    cancellation).ConfigureAwait(false);

            if (read is null)
            {
                await Fail(context, 400, readError!).ConfigureAwait(false);
                return (false, null!);
            }

            if (dropped)
            {
                context.Items[WarningKey] =
                    "This shapefile carries z or m values and they were not stored. The geometry "
                    + "model here is two-dimensional and the layer document reports hasZ false, so "
                    + "there is no surface that could serve them — keep the original file.";
            }

            return (true, read);
        }
        finally
        {
            // <b>Removed whichever way this went.</b> The scratch budget is shared, and an
            // archive left behind by a refused import is a budget somebody else's upload
            // runs into — which reads as *the disk is full* rather than as *a file was not
            // cleaned up*.
            if (path is not null)
            {
                scratch.Release(path);
            }
        }
    }

    /// <summary>A .prj's first line, which is the part a person recognises.</summary>
    private static string Shorten(string wkt) =>
        wkt.Length <= 120 ? wkt : wkt[..120] + "…";

    /// <summary>What a caller sends to design a feature class.</summary>
    /// <param name="Name">The service name.</param>
    /// <param name="GeometryType">Point, LineString, Polygon, or their Multi forms.</param>
    /// <param name="Fields">Its attribute columns.</param>
    /// <param name="Sharing">Who may read it. Private unless said otherwise.</param>
    /// <param name="CacheSeconds">
    /// How long this layer's tiles stay fresh, or null for the server default.
    /// Zero means never serve a cached tile. Asked here because whoever is
    /// designing the layer knows how often it changes (D-25, A-028).
    /// </param>
    /// <param name="ParentLayerId">
    /// A group layer inside that service to nest this layer under, or null for
    /// the top level. Create the group first with
    /// <c>POST /admin/services/{name}/groups</c>.
    /// </param>
    /// <param name="ServiceName">
    /// The service to put this layer in, or null for a service of its own.
    /// <b>This is what lets a portal screen design three layers into one
    /// service</b> — points, lines and fences under one name — which is the
    /// shape the owner asked for on 2026-08-15. The layer keeps its own name;
    /// only its address changes.
    /// </param>
    internal sealed record LayerDesign(
        string? Name,
        string? GeometryType,
        IReadOnlyList<FieldDesign>? Fields,
        string? Sharing,
        string? ServiceName = null,
        int? ParentLayerId = null,
        int? CacheSeconds = null);

    /// <summary>One designed column.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="Type">Its type.</param>
    /// <param name="Nullable">Whether it may be empty. True unless said otherwise.</param>
    internal sealed record FieldDesign(string? Name, string? Type, bool? Nullable);

    /// <summary>
    /// Creates an empty hosted feature class from a schema.
    /// </summary>
    /// <remarks>
    /// <b>This is the half of hosting that has nothing to do with files.</b> A
    /// team collecting inspections has no data to upload — they have a shape in
    /// mind and need somewhere to put what they gather. The result is a complete
    /// layer with no features: a client can add it, draw nothing, and edit.
    /// </remarks>
    private static async Task DefineAsync(
        HttpContext context,
        LayerDesign design,
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

        if (string.IsNullOrWhiteSpace(design.Name))
        {
            await Fail(context, 400, "'name' is required and becomes the service name.")
                .ConfigureAwait(false);
            return;
        }

        if (!Enum.TryParse(design.GeometryType, ignoreCase: true, out GeometryKind kind)
            || !Enum.IsDefined(kind))
        {
            await Fail(context, 400,
                "'geometryType' must be one of Point, MultiPoint, LineString, MultiLineString, "
                + "Polygon or MultiPolygon.").ConfigureAwait(false);
            return;
        }

        if (!TryFields(design.Fields, out List<FieldDescription> fields, out string? fieldError))
        {
            await Fail(context, 400, fieldError!).ConfigureAwait(false);
            return;
        }

        SharingScope sharing = ParseSharing(design.Sharing);

        if (sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await NameTakenAsync(catalog, design.Name, cancellation).ConfigureAwait(false))
        {
            await Fail(context, 409, $"A layer named '{design.Name}' already exists.")
                .ConfigureAwait(false);
            return;
        }

        Guid datastore = await DatastoreIdAsync(catalog, cancellation).ConfigureAwait(false);

        if (datastore == Guid.Empty)
        {
            await Fail(context, 503, "The datastore is not registered as a data source.")
                .ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        ImportResult result = await importer.DefineAsync(
            fields, kind, PostGisImporter.StoredSrid, design.Name, cancellation)
            .ConfigureAwait(false);

        PublishedLayerAddress published;

        try
        {
            published = await catalog.PublishLayerAsync(
                new LayerPublication(
                    design.Name, datastore, result.SchemaName, result.TableName,
                    "geom", "objectid", "objectid", result.StoredSrid, kind, sharing,
                    string.IsNullOrWhiteSpace(design.ServiceName)
                        ? null
                        : design.ServiceName.Trim(),
                    design.ParentLayerId,
                    design.CacheSeconds),
                current.Principal.Id,
                cancellation).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id, current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                "layer.define", design.Name,
                JsonSerializer.Serialize(new
                {
                    table = $"{result.SchemaName}.{result.TableName}",
                    fields = fields.Count,
                    geometryType = kind.ToString(),
                }),
                true),
            cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;

        await Results.Json(new
        {
            id = published.Id,
            name = design.Name,
            table = $"{result.SchemaName}.{result.TableName}",
            rows = 0,
            geometryType = kind.ToString(),
            fields = fields.Select(f => new { f.Name, type = f.Type.ToString(), f.Nullable }),
            sharing = sharing.ToString().ToLowerInvariant(),
            services = Services(published.ServiceName, published.LayerIndex),
            note = "The feature class is empty. Add features through the FeatureServer's "
                 + "applyEdits. Its extent is unknown until it has one, so a client will show it "
                 + "as covering the world.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Validates the designed columns.</summary>
    /// <remarks>
    /// <b>Names are checked here and quoted later, which is two defences.</b> A
    /// designed column name reaches DDL as an identifier and cannot be a bound
    /// parameter, so the check is the safety — and the sanitiser in the importer
    /// is what makes it survivable if this ever misses one.
    /// </remarks>
    private static bool TryFields(
        IReadOnlyList<FieldDesign>? designs, out List<FieldDescription> fields, out string? error)
    {
        fields = [];
        error = null;

        if (designs is null || designs.Count == 0)
        {
            // Allowed. A layer with geometry and no attributes is an ordinary
            // thing to collect, and demanding a dummy column would be ceremony.
            return true;
        }

        if (designs.Count > MaximumFields)
        {
            error = $"A layer may have at most {MaximumFields} fields.";
            return false;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase)
        {
            // Ours. A designed column of either name would collide with the
            // identity or the geometry, and the collision would surface as a
            // database error rather than as this sentence.
            "objectid",
            "geom",
        };

        foreach (FieldDesign design in designs)
        {
            if (string.IsNullOrWhiteSpace(design.Name))
            {
                error = "Every field needs a name.";
                return false;
            }

            if (!seen.Add(design.Name))
            {
                error =
                    $"'{design.Name}' is either duplicated or reserved. 'objectid' and 'geom' are "
                    + "created for you.";
                return false;
            }

            if (!Enum.TryParse(design.Type, ignoreCase: true, out FieldType type)
                || type == FieldType.Unknown)
            {
                error =
                    $"Field '{design.Name}' has type '{design.Type}'. Use one of SmallInteger, "
                    + "Integer, BigInteger, Single, Double, Text, Boolean, Date or Guid.";
                return false;
            }

            fields.Add(new FieldDescription(design.Name, type, design.Nullable ?? true, null));
        }

        return true;
    }

    /// <summary>How many attribute columns a designed layer may have.</summary>
    /// <remarks>
    /// PostgreSQL refuses past 1,600 and does it less politely. This is well
    /// inside that and past anything a person designs by hand.
    /// </remarks>
    private const int MaximumFields = 250;

    /// <summary>
    /// Where a hosted layer's services live — the service address, not the
    /// layer's name.
    /// </summary>
    /// <param name="serviceName">The service the layer landed in.</param>
    /// <param name="layerIndex">Its number within that service.</param>
    /// <remarks>
    /// <b>These two strings are the whole answer to "what URL did I just
    /// create", and they were wrong for one request.</b> A layer designed into a
    /// named service was reported at <c>/hosted/{layerName}/FeatureServer</c>,
    /// which is nothing — the service is named something else and the layer is
    /// an index inside it. A creation response that hands back a 404 is worse
    /// than one that hands back no link at all.
    /// </remarks>
    private static object Services(string serviceName, int layerIndex) => new
    {
        feature =
            $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{serviceName}"
            + $"/FeatureServer/{layerIndex}",
        tiles =
            $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{serviceName}"
            + "/VectorTileServer",
    };

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
    // enum-default-is-deliberate: private
    //
    // <b>Everything unrecognised is `private`, which is the decision the remarks above argue for</b> —
    // a default of *organisation* would share an import before its owner had seen what it looks like.
    // The marker tells `EnumeratedValuesAreCoveredTests` this is not the fourth-scope defect it exists
    // to catch: four of the five parsers that missed `group` had a discard arm too, and read a
    // group-scoped service as private, which is worse than refusing it.
    private static SharingScope ParseSharing(string? raw) => raw?.ToLowerInvariant() switch
    {
        "public" => SharingScope.Public,
        "group" => SharingScope.Group,
        "organization" or "organisation" => SharingScope.Organization,
        _ => SharingScope.Private,
    };

    /// <summary>The audit detail for an import.</summary>
    /// <remarks>
    /// <b>The validity goes inside the object, and it went beside it first.</b> This
    /// column is `json`, and appending `", invalidGeometries=18"` to a serialised object
    /// produces something PostgreSQL refuses — `22P02: invalid input syntax for type
    /// json` — which surfaced as a 503 on an import that had already written its data.
    /// Caught by the database on the first real upload, which is the right place for it to
    /// be caught and the wrong place to be relying on.
    /// </remarks>
    private static string Detail(
        ImportResult result, ImportedDataset dataset, GeometryValidity? validity) =>
        JsonSerializer.Serialize(new
        {
            table = $"{result.SchemaName}.{result.TableName}",
            result.Rows,
            columns = dataset.Columns.Count,
            geometryType = dataset.GeometryType.ToString(),
            result.SourceSrid,
            result.StoredSrid,
            invalidGeometries = validity?.Invalid,
        });

    /// <summary>
    /// Names the format in an archive that is not a shapefile, when it is one we recognise.
    /// </summary>
    /// <returns>What was recognised, or <c>None</c> to let the shapefile attempt proceed.</returns>
    /// <remarks>
    /// <para>
    /// <b>A recogniser for the refusal, not a step towards support.</b> It exists so that
    /// *"this format is not imported yet"* is said by the product rather than found in an ADR. Each
    /// arm points at the decision that owns it, because a refusal that names a question is a refusal
    /// somebody can act on.
    /// </para>
    /// <para>
    /// <b>Entry names only, and it runs before the shapefile attempt.</b> Nothing is decompressed,
    /// so this cannot be turned into an attack by the content of the archive —
    /// <see cref="ArchiveLimits.ForShapefile"/> guards the reading path and this one does no reading.
    /// The scan stops after a bounded number of names for the same reason the reader bounds its member
    /// count. Running first is what makes it work at all: a geodatabase is a folder, and the archive
    /// reader refuses folders before it ever gets to assembling a bundle.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A format this endpoint can recognise without being able to read it.
    /// </summary>
    /// <remarks>
    /// <b>An enumeration rather than a message, because one of these is no longer a refusal.</b> This
    /// returned the sentence to say no with, which was right while the answer was no for all three. A
    /// geodatabase now opens a job instead, and a caller cannot branch on prose.
    /// </remarks>
    private enum ForeignArchive
    {
        /// <summary>Nothing recognised — carry on and try to assemble a shapefile.</summary>
        None,

        /// <summary>A File Geodatabase: a folder named <c>x.gdb</c>, or its table files.</summary>
        Geodatabase,

        /// <summary>A GeoPackage, which is a SQLite database.</summary>
        GeoPackage,

        /// <summary>KML or KMZ.</summary>
        Kml,
    }

    private static ForeignArchive RecogniseArchive(System.IO.Stream archive)
    {
        const int Enough = 512;

        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase);
        bool gdbFolder = false;

        try
        {
            using System.IO.Compression.ZipArchive zip = new(
                archive, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);

            int seen = 0;

            foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
            {
                if (++seen > Enough)
                {
                    break;
                }

                string name = entry.FullName.Replace('\\', '/');

                extensions.Add(System.IO.Path.GetExtension(name));

                // A geodatabase is a *directory* named `something.gdb`, so the giveaway is a path
                // segment rather than a file extension.
                foreach (string segment in name.Split('/'))
                {
                    if (segment.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                    {
                        gdbFolder = true;
                    }
                }
            }
        }
        catch (System.IO.InvalidDataException)
        {
            return ForeignArchive.None;
        }

        if (gdbFolder
            || extensions.Contains(".gdbtable")
            || extensions.Contains(".gdbtablx")
            || extensions.Contains(".gdbindexes"))
        {
            return ForeignArchive.Geodatabase;
        }

        if (extensions.Contains(".gpkg"))
        {
            return ForeignArchive.GeoPackage;
        }

        if (extensions.Contains(".kml") || extensions.Contains(".kmz"))
        {
            return ForeignArchive.Kml;
        }

        return ForeignArchive.None;
    }

    /// <summary>What a geodatabase publish request carries.</summary>
    /// <param name="Archive">The inspection job whose archive is still on the disk.</param>
    /// <param name="Service">The one service every chosen layer is published into.</param>
    /// <param name="Layers">Which feature classes, by the names the inspection reported.</param>
    /// <param name="Sharing">The scope for that service, or null for private.</param>
    internal sealed record GeodatabasePublish(
        Guid Archive, string? Service, IReadOnlyList<string>? Layers, string? Sharing);

    /// <summary>
    /// Publishes chosen feature classes out of an inspected archive into one service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-038](../../docs/adr/ADR-038-how-a-geodatabase-becomes-a-service.md), on the owner's
    /// rule:</b> *"servis ve katman ayrı şeyler. bir serviste n katman olabilir."* One archive becomes
    /// one service holding N layers. Every other route into hosted data makes one service holding one
    /// layer, which is why the two words have been interchangeable in this product until now.
    /// </para>
    /// <para>
    /// <b>It names the inspection rather than uploading again.</b> The archive is still in the scratch
    /// directory under the inspection job's own id — that job keeps it precisely so the operator can
    /// choose from what it found — so this request carries an id and a list of names, and a
    /// two-gigabyte upload does not cross the wire twice.
    /// </para>
    /// <para>
    /// <b>Which layers may be named is checked against the inspection, not trusted.</b> The archive is
    /// a file somebody else wrote, and a request naming an arbitrary string would be asking GDAL to
    /// open whatever it likes inside it.
    /// </para>
    /// <para>
    /// <b>202, because this takes minutes.</b> The work is a job; this endpoint's whole responsibility
    /// is to refuse what cannot be done before anything is written, and then to say where to watch.
    /// </para>
    /// </remarks>
    private static async Task PublishGeodatabaseAsync(
        HttpContext context,
        GeodatabasePublish asked,
        IJobStore jobs,
        JobSignal signal,
        IAdminCatalog catalog,
        GeodatabaseReader reader,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (!reader.Available)
        {
            await Fail(context, 400, Refusal(ForeignArchive.Geodatabase)).ConfigureAwait(false);
            return;
        }

        string service = asked?.Service?.Trim() ?? string.Empty;

        if (service.Length == 0)
        {
            await Fail(context, 400,
                "'service' is required: it is the one service every chosen layer is published into, "
                + "which is what makes an archive of twenty-three feature classes one service rather "
                + "than twenty-three.").ConfigureAwait(false);
            return;
        }

        if (service.Length > 128 || service.AsSpan().IndexOfAny(NotInAServiceName) >= 0)
        {
            await Fail(context, 400,
                $"'{service}' cannot be a service name: it becomes one segment of the service's URL, "
                + "so it may be at most 128 characters and may not contain / \\ ? # or %.")
                .ConfigureAwait(false);
            return;
        }

        if (asked!.Layers is not { Count: > 0 } wanted)
        {
            await Fail(context, 400,
                "'layers' is required and names the feature classes to publish. The inspection listed "
                + "what is in the archive; this says which of them you want.").ConfigureAwait(false);
            return;
        }

        SharingScope sharing = ParseSharing(asked.Sharing);

        // Publishing straight to public needs the privilege that puts data on the internet,
        // separately from the one that publishes at all — the same order `DefineAsync` uses.
        if (sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic)
                .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // <b>The inspection is the authority on which names may be published.</b> It is found as its
        // owner: `FindAsync` answers null for *not yours* as well as for *not there*, which is what
        // stops this becoming a way to publish out of somebody else's upload.
        JobRecord? inspection = await jobs.FindAsync(
            asked.Archive,
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            cancellation).ConfigureAwait(false);

        if (inspection is null || inspection.Kind != JobKind.GeodatabaseInspect)
        {
            await Fail(context, 404,
                "No inspection of that archive belongs to you. Upload the geodatabase again — the "
                + "archive is kept only while its own inspection is the newest thing that happened "
                + "to it.").ConfigureAwait(false);
            return;
        }

        if (inspection.Status != JobStatus.Done)
        {
            await Fail(context, 409,
                $"That inspection is {inspection.Status.ToString().ToLowerInvariant()}, so there is "
                + "no list of feature classes to choose from yet. Watch "
                + $"/admin/jobs/{inspection.Id} and publish when it is done.").ConfigureAwait(false);
            return;
        }

        HashSet<string> offered = Offered(inspection.Detail);
        List<string> unknown = [.. wanted.Where(l => !offered.Contains(l))];

        if (unknown.Count > 0)
        {
            await Fail(context, 400,
                $"The inspection did not report {string.Join(", ", unknown.Select(u => $"'{u}'"))}. "
                + "Only what it found may be published — a request naming anything else would be "
                + "asking this server to open whatever it likes inside somebody's archive.")
                .ConfigureAwait(false);
            return;
        }

        if (!System.IO.File.Exists(scratch.PathFor(asked.Archive)))
        {
            await Fail(context, 409,
                "The archive is no longer on this server. It is kept while its inspection is the "
                + "newest thing that happened to it and released when a publish finishes, so this has "
                + "either been published already or the file was swept. Upload it again.")
                .ConfigureAwait(false);
            return;
        }

        // <b>A taken name is refused rather than added to.</b> The catalogue publishes into an
        // existing service when the name matches, which is how three layers come to share one — but it
        // does not ask whose service that is, so a publish naming somebody else's would put twenty
        // layers inside it. D-104 records that the older `POST /admin/publish` has the same hole; this
        // endpoint does not open a second one.
        foreach (AdminService existing in
                 await catalog.ListServicesAsync(cancellation).ConfigureAwait(false))
        {
            // <b>In `hosted`, and only there.</b> A datastore publish always lands in `hosted` — the
            // catalogue's own SQL forces it — so a service of the same name at the root or in another
            // folder answers on a different path and is not in the way. The first version treated a
            // null folder as `hosted` and would have refused a name that was free, with a sentence
            // saying it was taken in a folder it is not in.
            if (string.Equals(existing.Name, service, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    existing.Folder,
                    FeatureServerMetadataWriter.HostedFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                await Fail(context, 409,
                    $"There is already a service called '{service}' in hosted. Choose another name — "
                    + "publishing into it would add these layers to whatever is in there, which is a "
                    + "different request from the one this endpoint answers.").ConfigureAwait(false);
                return;
            }
        }

        Guid datastore = await DatastoreIdAsync(catalog, cancellation).ConfigureAwait(false);

        if (datastore == Guid.Empty)
        {
            await Fail(context, 503, "The datastore is not registered as a data source.")
                .ConfigureAwait(false);
            return;
        }

        JobRecord job = await jobs.CreateAsync(
            current.Principal.Id,
            JobKind.GeodatabaseImport,
            $"Publishing {wanted.Count} of the archive's feature classes into {service}",

            // <b>The scope is resolved here and stored as its own name.</b> The word the API takes is
            // this endpoint's business — 'organisation' spelled either way is the same scope — and a
            // worker parsing an operator's spelling hours later is a place for the two to disagree.
            JsonSerializer.Serialize(new
            {
                archive = asked.Archive,
                owner = current.Principal.Id,
                datastore,
                service,
                folder = FeatureServerMetadataWriter.HostedFolder,
                sharing = sharing.ToString(),
                layers = wanted,
            }),
            cancellation).ConfigureAwait(false);

        // <b>The worker is told rather than left to find it.</b> D-110: it polls at up to half
        // a minute when idle so the connection pool can prune, and this is what keeps that from
        // being half a minute of latency on work this node was just asked to do.
        signal.Wake(job.Kind);

        context.Response.Headers.Location = $"/admin/jobs/{job.Id}";

        await Results.Json(
            new
            {
                job = job.Id,
                status = "pending",
                watch = $"/admin/jobs/{job.Id}",
                service,
                layers = wanted.Count,
                note = "Each feature class becomes a table in the datastore and a layer in that one "
                    + "service. The job reports per layer as it goes: twenty-three feature classes is "
                    + "twenty-three chances to fail, and which one failed is the only thing that "
                    + "makes a failure actionable.",
            },
            statusCode: 202).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What cannot appear in a service name, which is one segment of a URL.</summary>
    private static readonly System.Buffers.SearchValues<char> NotInAServiceName =
        System.Buffers.SearchValues.Create(@"/\?#%");

    /// <summary>The layer names an inspection reported, or none when its answer cannot be read.</summary>
    /// <remarks>
    /// <b>Ordinal, because these names are matched against what GDAL will be asked for.</b> A
    /// case-insensitive match here would accept <c>omsf_extension</c> and then hand it to
    /// <c>GetLayerByName</c>, which is case-sensitive — so the check would pass and the layer would
    /// fail, at the far end of a job, with a message about a layer the operator did name.
    /// </remarks>
    private static HashSet<string> Offered(string? detail)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        try
        {
            using JsonDocument found = JsonDocument.Parse(detail ?? "{}");

            if (found.RootElement.ValueKind == JsonValueKind.Object
                && found.RootElement.TryGetProperty("layers", out JsonElement listed)
                && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement one in listed.EnumerateArray())
                {
                    if (one.ValueKind == JsonValueKind.Object
                        && one.TryGetProperty("name", out JsonElement named)
                        && named.GetString() is { } had)
                    {
                        names.Add(had);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Nothing can be checked against an answer that cannot be read, and an empty set is what
            // that means: every name the caller asked for is refused as unreported.
        }

        return names;
    }

    /// <summary>
    /// Keeps the archive, opens a job to look inside it, and answers 202 with where to watch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inspect, not import, and the two are separate on purpose.</b> A geodatabase holds many
    /// feature classes — one of the owner's holds six beside six attachment tables — so *which layer*
    /// is a question nobody can answer before the archive has been read. The first job reports what is
    /// in there; publishing is a second request naming a layer. Guessing here, or importing all of
    /// them, would both be decisions this endpoint has no basis for.
    /// </para>
    /// <para>
    /// <b>The order is job first, archive second.</b> The file is named after the job, so the job has
    /// to exist to name it — and a job whose archive failed to land can be finished with a reason,
    /// where an archive with no job is a file nobody will ever collect.
    /// </para>
    /// <para>
    /// <b>202 with a `Location`, which is what the status code means.</b> The console polls it; ADR-011
    /// §3.2's own reasoning is that a request which cannot be answered now is answered later at an
    /// address, rather than held open.
    /// </para>
    /// </remarks>
    private static async Task OpenInspectAsync(
        HttpContext context,
        IJobStore jobs,
        JobSignal signal,
        ImportScratch scratch,
        IFormFile file,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        JobRecord job = await jobs.CreateAsync(
            current.Principal.Id,
            JobKind.GeodatabaseInspect,
            $"Reading {file.FileName}",

            // <b>What a person should see, and nothing else.</b> The archive's path is not here: this
            // string is returned to the caller verbatim by `GET /admin/jobs/{id}`, and the path is
            // derived from the job id by whoever needs it.
            JsonSerializer.Serialize(new
            {
                file = file.FileName,
                bytes = file.Length,
            }),
            cancellation).ConfigureAwait(false);

        try
        {
            await scratch.KeepAsync(file, job.Id, cancellation).ConfigureAwait(false);
        }
        catch (System.IO.IOException full)
        {
            // <b>Finished rather than left pending.</b> A job created and then abandoned is the one
            // state `IJobStore` cannot explain to anybody: it would sit at *pending* for ever while
            // nothing was going to claim it.
            await jobs.FinishAsync(
                job.Id, JobStatus.Failed, null, full.Message, cancellation).ConfigureAwait(false);

            await Fail(context, 507, full.Message).ConfigureAwait(false);
            return;
        }

        // <b>The worker is told rather than left to find it.</b> D-110: it polls at up to half
        // a minute when idle so the connection pool can prune, and this is what keeps that from
        // being half a minute of latency on work this node was just asked to do.
        signal.Wake(job.Kind);

        context.Response.Headers.Location = $"/admin/jobs/{job.Id}";

        await Results.Json(
            new
            {
                job = job.Id,
                status = "pending",
                watch = $"/admin/jobs/{job.Id}",
                note = "A File Geodatabase is read by a separate process, which takes as long as the "
                    + "archive is large. This job reports the feature classes inside it; publishing "
                    + "one is a second request naming the layer you want.",
            },
            statusCode: 202).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The EPSG code the archive's own projection resolves to, or 0 when nothing can say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through the reader, because the reader is where a projection database lives.</b> The archive
    /// goes to the import scratch directory — GDAL opens a path, not a stream — and is deleted whether
    /// this succeeds or not. It is one child process and about sixty milliseconds on the archives this
    /// has been measured against, and it happens only when the operator gave no code.
    /// </para>
    /// <para>
    /// <b>Every failure is the same answer: zero.</b> No reader shipped, the scratch directory full, a
    /// refusal from GDAL, a layer with no spatial reference, a code PROJ declined to identify — none of
    /// them is a different thing to tell the operator, because in all of them the next step is the same
    /// and the refusal already names it. What must not happen is a guess, and nothing here guesses.
    /// </para>
    /// </remarks>
    private static async Task<int> ResolveSridAsync(
        IFormFile file,
        GeodatabaseReader reader,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        if (!reader.Available)
        {
            return 0;
        }

        Guid probe = Guid.NewGuid();
        string? kept = null;

        try
        {
            kept = await scratch.KeepAsync(file, probe, cancellation).ConfigureAwait(false);

            using JsonDocument answer = await reader.AskAsync(
                new { op = "layers", archive = kept },
                TimeSpan.FromSeconds(30),
                cancellation).ConfigureAwait(false);

            if (!answer.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
            {
                return 0;
            }

            if (!answer.RootElement.TryGetProperty("layers", out JsonElement layers))
            {
                return 0;
            }

            // <b>The first layer that has one, and a shapefile archive holds one shapefile.</b>
            // `ShapefileBundle` has already refused an archive with two, so there is no question of
            // which layer's system this is.
            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (layer.TryGetProperty("srid", out JsonElement code)
                    && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out int found)
                    && found > 0)
                {
                    return found;
                }
            }

            return 0;
        }
        catch (InvalidOperationException)
        {
            // The reader refused, died, or ran past its deadline. Not an answer, and not an error worth
            // relabelling: the caller's refusal says what to do.
            return 0;
        }
        catch (System.IO.IOException)
        {
            // The scratch directory is at its budget. Same reasoning.
            return 0;
        }
        finally
        {
            scratch.Release(kept);
        }
    }

    /// <summary>
    /// Why a recognised archive is being refused.
    /// </summary>
    /// <remarks>
    /// <b>The geodatabase sentence has been rewritten twice and the history is the reason it is
    /// careful.</b> It first said *"there is no GDAL-free managed reader to adopt, so writing one is a
    /// project"* — true under the constraint of the hour and wrong by the evening, when the owner
    /// allowed GDAL. It then said the reader *"is not built"*, which stopped being true when it was.
    /// A refusal that names a plan has to be corrected every time the plan moves, or it becomes the
    /// most confidently wrong text in the product — so this one names the **deployment** instead,
    /// which is a fact about the server answering rather than a claim about the roadmap.
    /// </remarks>
    private static string Refusal(ForeignArchive kind) => kind switch
    {
        // <b>Rewritten 2026-09-09 for the reason in `GeodatabaseReader`</b>, which carried the
        // same two errors: it blamed the operator for a packaging fault that hits the published
        // image specifically, and it offered a zipped shapefile as the way round — which needs
        // the same reader, so of the three intake formats only GeoJSON survives. D-235.
        ForeignArchive.Geodatabase =>
            "This is a File Geodatabase. Reading one needs the import reader, which this "
            + "deployment did not ship. The reader is built beside the server but is not carried "
            + "by `dotnet publish`, so an image built from the published output does not have it "
            + "— this is a packaging fault rather than something you did. A zipped shapefile "
            + "needs the same reader and will refuse too; a GeoJSON FeatureCollection imports "
            + "without it.",

        ForeignArchive.GeoPackage =>
            "This is a GeoPackage, and this server does not import one yet. ADR-024 condition 3 is "
            + "deliberate about it: a second archive format does not reuse the shapefile exception "
            + "without its own decision, because 'we already decompress' is not an argument. What "
            + "imports today is a zipped shapefile, or a GeoJSON FeatureCollection.",

        ForeignArchive.Kml =>
            "This looks like KML in an archive, and this server does not import one yet — ADR-024 "
            + "condition 3. What imports today is a zipped shapefile, or a GeoJSON FeatureCollection.",

        _ => "This archive is not one this server imports.",
    };

    /// <summary>
    /// Adds a column to a hosted layer's table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-058](../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
    /// §5b.</b> Until this existed a datastore layer's shape was frozen at creation and the only
    /// repair was to drop it and import again — which loses the item, its sharing, its symbology
    /// and every service composed over it.
    /// </para>
    /// <para>
    /// <b>Nothing is written to the catalogue, and that is §5f rather than an omission.</b> This
    /// server stores no field list: <c>ServiceContexts</c> reads the database's own catalogue
    /// per table and keeps the answer thirty seconds. So a new column is visible as soon as that
    /// memory is dropped, which is what this method does after the DDL.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer's name.</param>
    /// <param name="field">The column to add.</param>
    /// <param name="layers">The published layers, for the lookup.</param>
    /// <param name="importer">What runs the DDL.</param>
    /// <param name="contexts">The remembered shapes.</param>
    /// <param name="tiles">The tile cache.</param>
    /// <param name="catalog">The catalogue, for the change stamp.</param>
    /// <param name="audit">The log.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The task.</returns>
    private static async Task AddFieldAsync(
        HttpContext context,
        string layer,
        FieldDesign? field,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await HostedLayerAsync(context, layers, layer, "add a field to", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        if (field is null || string.IsNullOrWhiteSpace(field.Name))
        {
            await Fail(context, 400, "`name` is required: it is what the column is called.")
                .ConfigureAwait(false);

            return;
        }

        if (!TryFields([field], out List<FieldDescription> read, out string? unreadable))
        {
            await Fail(context, 400, unreadable!).ConfigureAwait(false);
            return;
        }

        (_, LayerDescription shape) = await contexts.GetAsync(found, cancellation)
            .ConfigureAwait(false);

        // <b>Asked before the DDL, so the refusal names the column rather than a constraint.</b>
        // PostgreSQL would refuse a duplicate anyway, with a message about a relation.
        if (shape.Find(field.Name) is not null)
        {
            await Fail(
                context, 409,
                $"'{found.Definition.Name}' already has a field called '{field.Name}'.")
                .ConfigureAwait(false);

            return;
        }

        string column;

        try
        {
            column = await importer.AddFieldAsync(
                found.Definition.SchemaName, found.Definition.TableName, read[0], cancellation)
                .ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException blocked) when (blocked.SqlState == "55P03")
        {
            // §5g and condition 1: a table somebody is reading refuses quickly rather than
            // taking ACCESS EXCLUSIVE and queueing every request behind the waiting DDL — which
            // D-08 measured at 30.30 s against 0.296 s unblocked.
            await Fail(
                context, 409,
                $"'{found.Definition.Name}' is being read right now, so its table could not be "
                + "altered — the change was abandoned rather than made to wait, because a "
                + "waiting ALTER holds up every request that arrives after it. Try again.")
                .ConfigureAwait(false);

            return;
        }

        await AfterSchemaChangeAsync(found, contexts, tiles, catalog, cancellation)
            .ConfigureAwait(false);

        await RecordAsync(
            context, audit, "layer.field.add", found.Definition.Name,
            new { column, type = field.Type }, cancellation).ConfigureAwait(false);

        await Results.Json(
            new
            {
                layer = found.Definition.Name,
                field = column,

                // <b>Said back, because the name that was asked for is not always the name that
                // was made.</b> `ColumnNameFor` is the import path's rule and it lower-cases and
                // rewrites what it must; a screen that went on showing what somebody typed would
                // be showing a column that does not exist.
                asked = field.Name,
                nullable = true,
                note = "Every existing row has this field empty. A column added to a table that "
                     + "already holds rows cannot be required without a default, and a default "
                     + "would be a value this server invented for data it has not seen.",
            },
            statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a column from a hosted layer's table, unless something depends on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-058 §5c: the refusal is a dependency check, not only a privilege one.</b> A column
    /// the layer's time dimension or its symbology reads is refused with the holder named,
    /// because <i>this field is in use</i> sends somebody to look through four screens.
    /// </para>
    /// <para>
    /// <b>And the system columns are not fields</b> (§5d). The object id, the identity and the
    /// geometry column are how a layer is addressed; they are refused here and the screen does
    /// not draw them, because a control for an act that is always refused is
    /// [ADR-034](../../docs/adr/ADR-034-server-and-studio.md)'s prohibition.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer's name.</param>
    /// <param name="field">The column.</param>
    /// <param name="layers">The published layers, for the lookup.</param>
    /// <param name="importer">What runs the DDL.</param>
    /// <param name="contexts">The remembered shapes.</param>
    /// <param name="tiles">The tile cache.</param>
    /// <param name="catalog">The catalogue, for the change stamp.</param>
    /// <param name="audit">The log.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The task.</returns>
    private static async Task DropFieldAsync(
        HttpContext context,
        string layer,
        string field,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await HostedLayerAsync(context, layers, layer, "drop a field from", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (_, LayerDescription shape) = await contexts.GetAsync(found, cancellation)
            .ConfigureAwait(false);

        if (shape.Find(field) is not { } column)
        {
            /*
              <b>Not in the field list is not the same as not in the table, and answering 404 for
              the geometry column was measured wrong on the first run.</b> `LayerDescription`
              lists the *attributes* — the geometry column is not one, because no client asks for
              it as a field. So `DELETE …/fields/geom` fell through to *has no field called
              'geom'*, which tells an operator their geometry column is already gone.

              <b>So the holders are asked before the 404 rather than after it.</b> A name this
              server refuses to drop is refused with its reason whether or not it appears in the
              list a client reads; only a name that is neither an attribute nor a system column
              is genuinely absent.
            */
            if (HoldingOn(found, field) is { } system)
            {
                await Fail(context, 409, system).ConfigureAwait(false);
                return;
            }

            await Fail(
                context, 404,
                $"'{found.Definition.Name}' has no field called '{field}'.")
                .ConfigureAwait(false);

            return;
        }

        if (HoldingOn(found, column.Name) is { } holder)
        {
            await Fail(context, 409, holder).ConfigureAwait(false);
            return;
        }

        try
        {
            await importer.DropFieldAsync(
                found.Definition.SchemaName, found.Definition.TableName, column.Name, cancellation)
                .ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException blocked) when (blocked.SqlState == "55P03")
        {
            await Fail(
                context, 409,
                $"'{found.Definition.Name}' is being read right now, so its table could not be "
                + "altered — the change was abandoned rather than made to wait. Try again.")
                .ConfigureAwait(false);

            return;
        }

        await AfterSchemaChangeAsync(found, contexts, tiles, catalog, cancellation)
            .ConfigureAwait(false);

        await RecordAsync(
            context, audit, "layer.field.drop", found.Definition.Name,
            new { column = column.Name }, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            layer = found.Definition.Name,
            field = column.Name,
            dropped = true,
            note = "The data that was in this field is gone and cannot be recovered.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The layer named, when it is hosted and the caller may change it.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="layers">The published layers.</param>
    /// <param name="name">The layer's name.</param>
    /// <param name="what">What was being attempted, for the refusal.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The layer, or null when a refusal has been written.</returns>
    /// <remarks>
    /// <b>ADR-058 §5h: a registered table is refused, and told where it is changed instead.</b>
    /// That table is in somebody else's database; this server re-reads its shape within thirty
    /// seconds of a DBA altering it and does not alter it itself. Saying so beats a 404 that
    /// leaves an operator wondering whether the layer exists.
    /// </remarks>
    private static async Task<PublishedLayer?> HostedLayerAsync(
        HttpContext context,
        PostgresLayerCatalog layers,
        string name,
        string what,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return null;
        }

        if (await AdminEndpoints.OneNamedLayerAsync(context, layers, name, cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        if (!found.Definition.IsHosted)
        {
            // <b>*Somebody else administers it* is what this said until it was read back on a
            // fixture where a registered source pointed at this server's own database.</b> That
            // is a legitimate setup — an operator may register the same PostgreSQL — and the
            // sentence then claimed a stranger owned a table we made. The true statement is
            // narrower and is the one that matters: the schema belongs to the source it was
            // registered from, not to this server.
            await Fail(
                context, 409,
                $"'{found.Definition.Name}' is a registered layer, so this server does not "
                + $"{what} it: its schema belongs to the database it was registered from. Its "
                + $"table is '{found.Definition.SchemaName}.{found.Definition.TableName}' — "
                + "change it there, and this server picks the new shape up within thirty "
                + "seconds or immediately with "
                + $"POST /admin/layers/{found.Definition.Name}/refresh.")
                .ConfigureAwait(false);

            return null;
        }

        /*
          <b>Hosted is not the same as *we made this table*, and the first version of this guard
          conflated them.</b> `IsHosted` says the layer's **source** is the datastore; it says
          nothing about the schema. A datastore source can serve any schema of that database —
          the conformance fixture publishes `cicorpus.shapes` through it — and such a layer
          reached `PostGisImporter`, whose own guard threw, and the caller got a **500** for a
          state this endpoint should have refused in a sentence.

          <b>Found by the test that was written to check something else.</b> It was meant to
          exercise the registered branch, borrowed a table the way every other test here does,
          and got an unhandled exception instead — which is the argument for writing the test
          before believing the guard.

          <b>The rule the importer keeps is the right one and this repeats it in the operator's
          words rather than replacing it.</b> Only tables in the schema this server creates into
          were created by this server; anything else in the same database belongs to whoever put
          it there, and altering it because a catalogue row pointed at it is the one thing that
          class must never do.
        */
        if (!AlterableSchema(true, found.Definition.SchemaName))
        {
            await Fail(
                context, 409,
                $"'{found.Definition.Name}' is served from the datastore but its table is "
                + $"'{found.Definition.SchemaName}.{found.Definition.TableName}', and this "
                + $"server does not {what} a table it did not create. Only what it imported or "
                + $"defined — everything in the '{PostGisImporter.HostedSchema}' schema — is "
                + "its to alter.")
                .ConfigureAwait(false);

            return null;
        }

        return found;
    }

    /// <summary>
    /// Whatever stops a column being dropped, as the sentence to answer with.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="column">The column, as the table spells it.</param>
    /// <returns>The refusal, or null when nothing holds it.</returns>
    /// <remarks>
    /// <para>
    /// <b>ADR-058 §5c and §5d, and this list is the whole safety of the delete.</b> Nothing in
    /// the language ties <i>this code reads a column by name</i> to <i>this column may not be
    /// dropped</i>, so the next feature that reads one will not add itself here. That is recorded
    /// as the ADR's strongest counterargument and as its second condition, not smoothed over.
    /// </para>
    /// <para>
    /// <b>The first draft of the ADR listed a fourth holder — a layer filter — and there is no
    /// such thing in this server.</b> The list was wrong by imagining a dependency before it was
    /// ever wrong by missing one, which is worth knowing about a list maintained this way.
    /// </para>
    /// </remarks>
    private static string? HoldingOn(PublishedLayer layer, string column)
    {
        bool Same(string? other) =>
            other is { Length: > 0 }
            && string.Equals(other, column, StringComparison.OrdinalIgnoreCase);

        if (Same(layer.Definition.GeometryColumn))
        {
            return $"'{column}' is this layer's geometry. A layer without geometry is not a "
                 + "layer, so it cannot be dropped — republish without it if that is what you "
                 + "mean.";
        }

        if (Same(layer.Definition.IdentityColumn) || Same(layer.Definition.IntegerIdentityColumn))
        {
            return $"'{column}' is how this layer's features are addressed. Every query, every "
                 + "edit and every tile identifies a feature by it, so it cannot be dropped.";
        }

        if (Same(layer.TimeField))
        {
            return $"'{column}' is this layer's time field, so WMS-T and any time-aware client "
                 + "read it. Clear the time field on the layer's own screen first, and this "
                 + "field can then go.";
        }

        // <b>ADR-065: the subtype column holds what kind each feature is.</b> Dropping it takes
        // every subtype, template and per-subtype domain with it, and the Fields page is where
        // somebody who means that says so first.
        if (layer.FieldOverrides.Any(o => o.Subtypes is not null && Same(o.Column)))
        {
            return $"'{column}' is this layer's subtype column — every feature's kind is recorded in "
                 + "it, and its templates are built from it. Remove the subtypes on the layer's "
                 + "Fields page first, and this field can then go.";
        }

        if (layer.Symbology is { Length: > 0 } document && SymbologyNames(document).Any(Same))
        {
            return $"'{column}' is what this layer's symbology draws with — its classes are "
                 + "built from it. Change the symbology to a different field, or back to the "
                 + "generated appearance, and this field can then go.";
        }

        return null;
    }

    /// <summary>
    /// Every column a stored symbology document names.
    /// </summary>
    /// <param name="document">The stored style.</param>
    /// <returns>The column names, or none when the document cannot be read.</returns>
    /// <remarks>
    /// <para>
    /// <b>The document rather than the picture, and until 2026-09-09 this asked for the
    /// picture.</b> It compiled the style and took <c>SymbologyPlan.Fields</c>, which is
    /// collected from the paint expressions the compilation produces — the right answer to
    /// *what does drawing this need to fetch* and the wrong one to *what does this document
    /// name*. A unique-value renderer whose classes all carry the same colour derives a
    /// <c>match</c> with one outcome; that collapses to a constant, the plan reports **no
    /// fields**, and the guard let the column be dropped. `CimProjection.AllFields` says why
    /// that matters: the map does not change, the **document** breaks.
    /// </para>
    /// <para>
    /// <b>A MapLibre document still goes through the plan</b>, because there is nothing else to
    /// ask — a paint expression is the only place it names a column, and reading it means
    /// compiling it. ADR-052 made CIM canonical, so this is the older shape rather than the
    /// common one.
    /// </para>
    /// <para>
    /// <b>An unreadable document names nothing, and does not stop the drop.</b> A style this
    /// server cannot compile is already refused everywhere it is drawn; making it also block a
    /// schema change would turn one broken document into a layer nobody can edit, and the
    /// operator would have no way to tell which of the two problems they had.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> SymbologyNames(string document)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(document) is System.Text.Json.Nodes.JsonObject body
                && Cim.IsRenderer(body))
            {
                return Cim.Project(body).AllFields();
            }

            return SymbologyPlan.Compile(document).Fields;
        }
        catch (Exception e) when (e is SymbologyException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Drops what this server remembered about a layer whose shape has just changed.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="contexts">The remembered shapes.</param>
    /// <param name="tiles">The tile cache.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The work.</returns>
    /// <remarks>
    /// <para>
    /// <b>ADR-058 §5g, and thirty seconds is not good enough here.</b> For a registered table the
    /// TTL is the only bound available because nothing tells us. For a hosted one <i>we</i> made
    /// the change, so serving a stale field list afterwards would be a staleness we chose to keep
    /// for no reason.
    /// </para>
    /// <para>
    /// <b>The tiles go whichever way the schema moved.</b> A tile built from a column that no
    /// longer exists is not out of date, it is wrong
    /// ([ADR-010](../../docs/adr/ADR-010-caching.md) §5.1); one built before a column was added
    /// is merely stale. Purging both is one rule, and a screen that purged sometimes is a rule
    /// nobody could predict.
    /// </para>
    /// </remarks>
    private static async Task AfterSchemaChangeAsync(
        PublishedLayer layer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        contexts.Forget(layer);
        tiles.Purge(layer.Id);

        // <b>The stamp, so anything listing the service sees that it changed.</b> ArcGIS moves a
        // timestamp on the item for exactly this; here the service row's `updated_at` is what a
        // listing already reads, so there is nothing new to store.
        await catalog.TouchServiceAsync(layer.ServiceName, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this server may alter a layer's table, which is two facts and not one.
    /// </summary>
    /// <param name="hosted">Whether the layer's source is the datastore.</param>
    /// <param name="schema">The schema its table is in.</param>
    /// <returns>Whether the field endpoints will accept it.</returns>
    /// <remarks>
    /// <para>
    /// <b>Written once because it is asked in two places, and conflating the two facts cost a
    /// 500.</b> <c>Hosted</c> says the *source* is the datastore; it says nothing about the
    /// schema, and a datastore source can serve any schema of that database. Only what this
    /// server created — the <c>hosted</c> schema — is its to alter.
    /// </para>
    /// <para>
    /// <b>The listing needs the same answer the endpoint gives</b>, so the console can leave the
    /// control off a layer that would be refused rather than drawing one that always fails —
    /// [ADR-034](../../docs/adr/ADR-034-server-and-studio.md). Two expressions of one rule is how
    /// the screen comes to offer what the server declines.
    /// </para>
    /// </remarks>
    internal static bool AlterableSchema(bool hosted, string? schema) =>
        hosted && string.Equals(schema, PostGisImporter.HostedSchema, StringComparison.Ordinal);

    /// <summary>One audited act, in the shape this file already writes them.</summary>
    /// <param name="context">The request, for the caller's address.</param>
    /// <param name="audit">The log.</param>
    /// <param name="action">What was done.</param>
    /// <param name="subject">What it was done to.</param>
    /// <param name="detail">Whatever is worth reading afterwards.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The work.</returns>
    /// <remarks>
    /// <b>Written once here rather than reached for from <c>AdminEndpoints</c>.</b> That class
    /// has its own <c>AuditAsync</c> and its own <c>Detail</c>; making either visible across the
    /// two files would be a shared helper whose two callers disagree about what a failed act
    /// looks like. Two schema changes were about to be the third and fourth hand-rolled copy in
    /// this file, which is where a helper earns its place.
    /// </remarks>
    private static async Task RecordAsync(
        HttpContext context,
        IAuditLog audit,
        string action,
        string subject,
        object detail,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                action,
                subject,
                JsonSerializer.Serialize(detail),
                true),
            cancellation).ConfigureAwait(false);
    }

    private static Task Fail(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: code)
            .ExecuteAsync(context);

    /// <summary>
    /// Adds Portal's four editor-tracking columns to a hosted layer and gives them their roles —
    /// ADR-064.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One action, because the four only mean something together.</b> On a registered layer
    /// the table is the customer's and this server issues no DDL, so the roles are given to
    /// columns that already exist on the Fields page. On a hosted layer the table is ours, and
    /// asking an operator to add four columns and then find them again to give each a role is
    /// four chances to give <c>created_date</c> the creator's role.
    /// </para>
    /// <para>
    /// <b>Portal's names</b>, so an operator arriving from Portal recognises them and a layer
    /// exported from here to there needs nothing renamed. A column of one of those names that
    /// is already there and of the right type is used as it is; one of the wrong type is a
    /// refusal naming it, because writing an account name into somebody's integer column is
    /// the kind of guess this server does not make.
    /// </para>
    /// <para>
    /// <b>Rows that were there before have no creator</b>, and ADR-064 makes such a row
    /// nobody's own: only <c>features:fullEdit</c> changes it. That is said in the answer, since
    /// it is the one thing about turning this on that an operator might not expect.
    /// </para>
    /// </remarks>
    private static async Task TrackEditsAsync(
        HttpContext context,
        string layer,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await HostedLayerAsync(context, layers, layer, "track the edits of", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (_, LayerDescription table) = await contexts.TableAsync(found, cancellation)
            .ConfigureAwait(false);

        (string Name, FieldType Type, Graticula.Catalog.EditRole Role)[] portal =
        [
            ("created_user", FieldType.Text, Graticula.Catalog.EditRole.Creator),
            ("created_date", FieldType.Date, Graticula.Catalog.EditRole.Created),
            ("last_edited_user", FieldType.Text, Graticula.Catalog.EditRole.Editor),
            ("last_edited_date", FieldType.Date, Graticula.Catalog.EditRole.Edited),
        ];

        foreach ((string name, FieldType type, _) in portal)
        {
            if (table.Find(name) is { } existing && existing.Type != type)
            {
                await Fail(
                    context, 409,
                    $"'{found.Definition.Name}' already has a column called '{name}', and it is not "
                    + $"a {(type == FieldType.Text ? "text" : "date")} column, so it cannot record "
                    + "what that name means. Rename it, or give the roles to other columns on the "
                    + "Fields page.")
                    .ConfigureAwait(false);

                return;
            }
        }

        List<string> added = [];

        foreach ((string name, FieldType type, _) in portal)
        {
            if (table.Find(name) is not null)
            {
                continue;
            }

            try
            {
                await importer.AddFieldAsync(
                    found.Definition.SchemaName,
                    found.Definition.TableName,
                    new FieldDescription(name, type, Nullable: true, MaxLength: null),
                    cancellation).ConfigureAwait(false);

                added.Add(name);
            }
            catch (Npgsql.PostgresException blocked) when (blocked.SqlState == "55P03")
            {
                // ADR-058 §5g, as for adding one field: refused quickly rather than queueing
                // every request behind a waiting ALTER. What was added so far stays, and a
                // second press adds the rest.
                await Fail(
                    context, 409,
                    $"'{found.Definition.Name}' is being read right now, so its table could not be "
                    + $"altered{(added.Count == 0 ? string.Empty : $" after adding {string.Join(", ", added)}")}. "
                    + "Try again: what is already there is kept.")
                    .ConfigureAwait(false);

                return;
            }
        }

        // <b>The roles go to these four and leave every other column</b>, which keeps its label
        // and its hidden flag but gives up any role — one role, one column.
        List<Graticula.Catalog.FieldOverride> overrides = [];

        foreach (Graticula.Catalog.FieldOverride said in found.FieldOverrides)
        {
            if (!portal.Any(p => said.Matches(p.Name)))
            {
                overrides.Add(said with { Tracks = Graticula.Catalog.EditRole.None });
            }
        }

        foreach ((string name, _, Graticula.Catalog.EditRole role) in portal)
        {
            string? alias = found.FieldOverrides.FirstOrDefault(o => o.Matches(name)).Alias;

            overrides.Add(new Graticula.Catalog.FieldOverride(name, alias, Hidden: false, role));
        }

        await catalog.SetFieldOverridesAsync(
            found.Id, [.. overrides.Where(o => o.SaysSomething)], cancellation).ConfigureAwait(false);

        await AfterSchemaChangeAsync(found, contexts, tiles, catalog, cancellation)
            .ConfigureAwait(false);

        await RecordAsync(
            context, audit, "layer.editorTracking", found.Definition.Name,
            new { added }, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            layer = found.Definition.Name,
            added,
            tracks = portal.Select(p => new { column = p.Name, role = p.Role.ToString().ToLowerInvariant() }),
            note = "Features added or changed from now on record who did it and when. Features that "
                 + "were already there have no creator, so they are nobody's own: changing them "
                 + "needs features:fullEdit.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
