using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Features;
using Graticula.Formats;
using System.Text;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
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
        app.MapPost("/admin/hosted/define", DefineAsync);

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
                context, form, file, jobs, reader, scratch, cancellation).ConfigureAwait(false);

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
                context.Connection.RemoteIpAddress?.ToString(),
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
            await OpenInspectAsync(context, jobs, scratch, file, cancellation).ConfigureAwait(false);
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
            await Fail(context, 400,
                "'srid' is required for a shapefile. The .prj beside it is WKT rather than an "
                + "EPSG code, and matching WKT to a code by comparing strings is how a layer "
                + "comes to be declared as a system it is not in — so this server asks instead of "
                + "guessing."
                + (bundle.Prj is null
                    ? " This archive has no .prj at all."
                    : $" The .prj in this archive says: {Shorten(bundle.Prj)}"))
                .ConfigureAwait(false);

            return (false, null!);
        }

        if (!ShapefileReader.TryRead(
                bundle.Shp,
                bundle.Dbf,
                srid,
                encoding,
                ImportLimits.Default,
                out ImportedDataset? read,
                out string? readError))
        {
            await Fail(context, 400, readError!).ConfigureAwait(false);
            return (false, null!);
        }

        if (ShapefileReader.DropsZOrM(bundle.Shp))
        {
            context.Items[WarningKey] =
                "This shapefile carries z or m values and they were not stored. The geometry "
                + "model here is two-dimensional and the layer document reports hasZ false, so "
                + "there is no surface that could serve them — keep the original file.";
        }

        return (true, read!);
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
                context.Connection.RemoteIpAddress?.ToString(),
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
        ForeignArchive.Geodatabase =>
            "This is a File Geodatabase. Reading one needs the geodatabase reader, which this "
            + "deployment did not ship — it is built and copied beside the server by the solution, so "
            + "a server without it was assembled by hand. What imports without it is a zipped "
            + "shapefile, or a GeoJSON FeatureCollection.",

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

    private static Task Fail(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: code)
            .ExecuteAsync(context);
}
