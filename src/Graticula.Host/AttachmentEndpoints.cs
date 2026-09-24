using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// Files attached to features — the ArcGIS attachment surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the product's second surface that accepts arbitrary bytes</b>, and
/// the one ADR-013 §4d was written about. Hosted import came first and parses
/// what it is given; this one stores it and hands it back, which is a different
/// threat entirely — an uploaded SVG served same-origin as the admin API is
/// stored XSS against the GIS administrator, our most privileged user.
/// </para>
/// <para>
/// <b>Nothing here holds a whole attachment.</b> The upload is read from the
/// multipart body straight into a <c>bytea</c> parameter; the download is a
/// database stream copied to the response. ADR-013 §4a makes that a condition
/// rather than an optimisation, because A-037 measured an 80.9% GC pause on a
/// workload allocating 20 MB per request — and here the user picks the size.
/// </para>
/// </remarks>
internal static class AttachmentEndpoints
{
    /// <summary>
    /// The largest single attachment.
    /// </summary>
    /// <remarks>
    /// A cap rather than a measurement. 128 MB holds any photograph, any
    /// reasonable document and most drone imagery, and is small enough that one
    /// upload cannot consume a default layer quota. Enforced while streaming, so
    /// a lying <c>Content-Length</c> cannot get past it.
    /// </remarks>
    public const long MaximumBytes = 128L * 1024 * 1024;

    /// <summary>Maps the surface, under both service folders.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Root and any folder. `{folder}` is a parameter and a literal segment wins in
        // routing, so the hosted-prefixed registrations elsewhere still take precedence —
        // and a registered service in a named folder now has a route at all (2026-08-17).
        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            string layer = $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}";

            app.MapGet($"{layer}/{{objectId:long}}/attachments", ListAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{layer}/{{objectId:long}}/attachments/{{attachmentId:int}}", DownloadAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapPost($"{layer}/{{objectId:long}}/addAttachment", AddAsync).DisableAntiforgery()
                .Governed(SharingGovernedExtensions.ByService);
            app.MapPost($"{layer}/{{objectId:long}}/deleteAttachments", DeleteAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // <b>2026-09-15, both found by a review from an ArcGIS user's side.</b> Dashboards,
            // Experience Builder and the Maps SDK read a layer's attachments in one request with
            // queryAttachments, and Survey123 replaces a photograph with updateAttachment; both
            // were 404, so a gallery was empty and a replaced photograph was a failed edit.
            app.MapMethods($"{layer}/queryAttachments", ["GET", "POST"], QueryAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapPost($"{layer}/{{objectId:long}}/updateAttachment", UpdateAsync).DisableAntiforgery()
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    // ---------- listing ----------

    private static async Task ListAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        long objectId,
        CatalogFallback catalog,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: false, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        IReadOnlyList<AttachmentInfo> attachments =
            await store.ListAsync(objectId, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            attachmentInfos = attachments.Select(a => new
            {
                id = a.Id,
                globalId = (string?)null,
                parentGlobalId = (string?)null,

                // <b>Our determination, not theirs.</b> A client that renders by
                // content type must be told what the bytes are rather than what
                // the uploader wanted them treated as.
                contentType = a.ContentType,
                size = a.Size,
                name = a.Name,
                exifInfo = (object?)null,

                // Not part of the ArcGIS shape, and included because the gap
                // between the two is the interesting part of an incident.
                declaredContentType = a.DeclaredContentType,
                uploaded = a.UploadedAt,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- query ----------

    /// <summary>The most features one <c>queryAttachments</c> reads the attachments of.</summary>
    private const int MostQueriedFeatures = 1_000;

    /// <summary>
    /// The attachments of several features in one answer, grouped by feature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>By <c>objectIds</c>, which is what a client that already drew the features has, and by
    /// <c>definitionExpression</c> since 2026-09-23</b> — V-78, the fourth ArcGIS review: the Maps SDK sends an
    /// <c>AttachmentQuery.where</c> as that parameter, and it was refused. It is read by the query face's own
    /// parser, so it accepts and refuses exactly what <c>where</c> does, and it is combined with
    /// <c>objectIds</c> when both are sent. <c>globalIds</c>, <c>attachmentsWhere</c>, <c>keywords</c> and
    /// <c>size</c> are still refused rather than ignored: each narrows the answer, and a caller who sent
    /// one and got every attachment would be answered a different question.
    /// </para>
    /// <para>
    /// <b>The same reads the per-feature listing makes</b>, one per feature, so the two cannot
    /// disagree about what is attached; a thousand features is the ceiling, which is what a client
    /// asks for a page at a time.
    /// </para>
    /// </remarks>
    private static async Task QueryAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        LayerConnections connections,
        ServiceContexts contexts,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: false, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        string Value(string key) =>
            form.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues posted) && posted.Count > 0
                ? posted.ToString()
                : context.Request.Query[key].ToString();

        foreach (string narrowing in (string[])["globalIds", "attachmentsWhere", "keywords", "size"])
        {
            if (Value(narrowing).Trim().Length > 0)
            {
                await Refuse(context, 400,
                    $"'{narrowing}' is not supported by queryAttachments here, and it is refused rather "
                    + "than ignored, because it narrows the answer. Pass 'objectIds' — a query with "
                    + "returnIdsOnly=true turns a where clause into them.").ConfigureAwait(false);
                return;
            }
        }

        List<long> ids = [];

        foreach (string part in Value("objectIds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                await Refuse(context, 400, $"'{part}' in 'objectIds' is not an object id.").ConfigureAwait(false);
                return;
            }

            ids.Add(id);
        }

        string definition = Value("definitionExpression").Trim();

        if (definition.Length > 0)
        {
            if (await IdsMatchingAsync(context, serviceName, layerId, catalog, contexts, settings, definition, cancellation)
                    .ConfigureAwait(false) is not { } matched)
            {
                return;
            }

            ids = ids.Count == 0 ? [.. matched] : [.. ids.Intersect(matched)];

            if (ids.Count == 0)
            {
                await Results.Json(new { attachmentGroups = Array.Empty<object>() }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        if (ids.Count == 0)
        {
            await Refuse(context, 400, "'objectIds' or 'definitionExpression' is required.").ConfigureAwait(false);
            return;
        }

        if (ids.Count > MostQueriedFeatures)
        {
            await Refuse(context, 400,
                $"'objectIds' names {ids.Count} features and one request reads the attachments of at most "
                + $"{MostQueriedFeatures}. Ask in pages.").ConfigureAwait(false);
            return;
        }

        string[] types = Value("attachmentTypes")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool urls = string.Equals(Value("returnUrl"), "true", StringComparison.OrdinalIgnoreCase);
        string layerUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
            + context.Request.Path.Value![..context.Request.Path.Value!.LastIndexOf('/')];

        List<object> groups = [];

        foreach (long id in ids.Distinct())
        {
            IReadOnlyList<AttachmentInfo> attachments =
                await store.ListAsync(id, cancellation).ConfigureAwait(false);

            object[] infos =
            [
                .. attachments
                    .Where(a => types.Length == 0 || types.Any(t => Matches(a.ContentType, t)))
                    .Select(a => (object)new
                    {
                        id = a.Id,
                        globalId = (string?)null,
                        name = a.Name,
                        contentType = a.ContentType,
                        size = a.Size,
                        keywords = string.Empty,
                        exifInfo = (object?)null,
                        url = urls ? $"{layerUrl}/{id}/attachments/{a.Id}" : null,
                    }),
            ];

            if (infos.Length > 0)
            {
                groups.Add(new { parentObjectId = id, parentGlobalId = (string?)null, attachmentInfos = infos });
            }
        }

        await Results.Json(new { attachmentGroups = groups }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The object ids a <c>definitionExpression</c> matches, or null with the refusal written.</summary>
    private static async Task<IReadOnlyList<long>?> IdsMatchingAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        HostSettings settings,
        string definition,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return null;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        QueryCollection asked = new(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["where"] = definition,
            ["returnIdsOnly"] = "true",
        });

        if (!FeatureServerQueryParameters.TryParse(
                asked,
                layer.Definition.IntegerIdentityColumn!,
                layer.Definition.Srid,
                described.Fields,
                out FeatureQuery? query,
                out _,
                out string? error,
                serverDefaultRecordCount: await ServerPageSize.OfAsync(context, cancellation).ConfigureAwait(false),
                serverMaximumRecordCount: settings.MaximumRecordCount))
        {
            await Refuse(context, 400, $"'definitionExpression': {error}").ConfigureAwait(false);
            return null;
        }

        // The same lease the query face takes before a count or an id list, inside the same bound.
        BudgetedFeatureSource? budgeted = source as BudgetedFeatureSource;

        using ConnectionBudget.Lease lease = budgeted is not null
            ? await budgeted.LeaseAsync(cancellation).ConfigureAwait(false)
            : default;

        if ((budgeted?.Inner ?? source) is not IFeatureSummaries summaries)
        {
            await Refuse(context, 400, "'definitionExpression' cannot be answered on this layer; pass 'objectIds'.")
                .ConfigureAwait(false);
            return null;
        }

        IReadOnlyList<long> matched = await summaries.ObjectIdsAsync(query, cancellation).ConfigureAwait(false);

        if (matched.Count > MostQueriedFeatures)
        {
            await Refuse(context, 400,
                $"'definitionExpression' matches {matched.Count} features and one request reads the attachments of "
                + $"at most {MostQueriedFeatures}. Narrow it, or ask in pages by 'objectIds'.").ConfigureAwait(false);
            return null;
        }

        return matched;
    }

    /// <summary>
    /// Whether a stored type is one a caller named — a full type, or a family such as <c>image/*</c>
    /// or ArcGIS's bare <c>image</c>.
    /// </summary>
    private static bool Matches(string stored, string asked)
    {
        string family = asked.EndsWith("/*", StringComparison.Ordinal) ? asked[..^2] : asked;

        return string.Equals(stored, asked, StringComparison.OrdinalIgnoreCase)
            || (!family.Contains('/', StringComparison.Ordinal)
                && stored.StartsWith(family + "/", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- replace ----------

    /// <summary>
    /// Replaces an attachment's bytes, keeping its id — <c>updateAttachment</c>.
    /// </summary>
    /// <remarks>
    /// <b>The id comes before the file, and is required to.</b> The body is read as it arrives, as
    /// <see cref="AddAsync"/> reads it, so the attachment the bytes are for has to be known by the
    /// time they start; the Maps SDK and Survey123 send <c>attachmentId</c> first.
    /// </remarks>
    private static async Task UpdateAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        long objectId,
        CatalogFallback catalog,
        LayerConnections connections,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: true, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
            is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaximumBytes;
        }

        string? boundary = Boundary(context.Request.ContentType);

        if (boundary is null)
        {
            await Refuse(context, 400,
                "Post the file as multipart/form-data with 'attachmentId' and a part named "
                + "'attachment', which is what ArcGIS clients send.").ConfigureAwait(false);
            return;
        }

        int? attachmentId = null;
        MultipartReader reader = new(boundary, context.Request.Body, bufferSize: 1024 * 1024);
        MultipartSection? section;

        while ((section = await reader.ReadNextSectionAsync(cancellation).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(
                    section.ContentDisposition, out ContentDispositionHeaderValue? disposition))
            {
                continue;
            }

            if (!disposition.IsFileDisposition())
            {
                if (string.Equals(disposition.Name.Value, "attachmentId", StringComparison.OrdinalIgnoreCase))
                {
                    using StreamReader text = new(section.Body);
                    string raw = (await text.ReadToEndAsync(cancellation).ConfigureAwait(false)).Trim();

                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        await Refuse(context, 400, $"'{raw}' is not an attachment id.").ConfigureAwait(false);
                        return;
                    }

                    attachmentId = parsed;
                }

                continue;
            }

            if (attachmentId is not { } id)
            {
                await Refuse(context, 400,
                    "'attachmentId' must be sent before the file, so the bytes can be written as they "
                    + "arrive. Nothing was replaced.").ConfigureAwait(false);
                return;
            }

            string name = SafeName(disposition.FileName.Value ?? disposition.Name.Value);

            (string sniffed, Stream content) = await ContentSniffer
                .SniffAsync(section.Body, cancellation).ConfigureAwait(false);

            await using LimitedStream limited = new(content, MaximumBytes);

            try
            {
                if (!await store.UpdateAsync(objectId, id, name, sniffed, section.ContentType, limited, cancellation)
                        .ConfigureAwait(false))
                {
                    await Refuse(context, 404, $"No attachment {id} on feature {objectId}. Nothing was replaced.")
                        .ConfigureAwait(false);
                    return;
                }

                await AuditAsync(
                    context, audit, $"{serviceName}/{layerId}", objectId, name, sniffed, cancellation)
                    .ConfigureAwait(false);

                await Results.Json(new
                {
                    updateAttachmentResult = new { objectId = id, globalId = (string?)null, success = true },
                }).ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (AttachmentTooLargeException)
            {
                await Refuse(context, 413,
                    $"An attachment may be at most {MaximumBytes / 1048576} MB. Nothing was replaced.")
                    .ConfigureAwait(false);
            }
            catch (AttachmentQuotaExceededException e)
            {
                await Refuse(context, 507, e.Message).ConfigureAwait(false);
            }

            return;
        }

        await Refuse(context, 400, "No file part was found in the request.").ConfigureAwait(false);
    }

    // ---------- download ----------

    /// <summary>
    /// Streams an attachment back.
    /// </summary>
    /// <remarks>
    /// <b>Three headers do the security here, and none of them is optional.</b>
    /// <c>Content-Disposition: attachment</c> so nothing renders inline;
    /// <c>X-Content-Type-Options: nosniff</c> so a browser does not overrule the
    /// type with its own guess; and a <c>Content-Security-Policy</c> that
    /// forbids execution, for the case where a browser reaches the bytes anyway.
    /// ADR-013 §4d requires the first two and a separate origin or a CSP for the
    /// third — and a separate origin is a deployment this product does not have.
    /// </remarks>
    private static async Task DownloadAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        long objectId,
        int attachmentId,
        CatalogFallback catalog,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: false, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        await using OpenAttachment? open =
            await store.OpenAsync(attachmentId, cancellation).ConfigureAwait(false);

        if (open is null || open.Info.FeatureId != objectId)
        {
            // The feature check matters: without it an attachment id is a
            // guessable integer that reaches any attachment in the layer,
            // including one on a feature the caller could not otherwise reach
            // once row filtering exists.
            await Refuse(context, 404, $"No attachment {attachmentId} on feature {objectId}.")
                .ConfigureAwait(false);
            return;
        }

        HttpResponse response = context.Response;

        response.ContentType = open.Info.ContentType;
        response.ContentLength = open.Info.Size;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";

        // The filename is data. ContentDispositionHeaderValue quotes and encodes
        // it, so a name containing a quote, a semicolon or a newline cannot
        // break out of the header — which is how a filename becomes a second
        // header nobody sent.
        ContentDispositionHeaderValue disposition = new("attachment");
        disposition.SetHttpFileName(open.Info.Name);
        response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

        await open.Content.CopyToAsync(response.Body, cancellation).ConfigureAwait(false);
    }

    // ---------- upload ----------

    /// <summary>
    /// Stores an attachment, reading it from the request as it arrives.
    /// </summary>
    /// <remarks>
    /// <b><see cref="MultipartReader"/> rather than <c>IFormFile</c>.</b> The
    /// form binder buffers anything past a threshold to a temporary file, which
    /// is still buffering the whole payload at a layer — the thing ADR-013 §4a
    /// says not to do. Reading the section directly means the bytes go from the
    /// socket to PostgreSQL without stopping.
    /// </remarks>
    private static async Task AddAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        long objectId,
        CatalogFallback catalog,
        LayerConnections connections,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: true, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        // <b>Kestrel's own limit is raised here, beside the cap it has to
        // clear.</b> The default is 30 MB, which is well under the 128 MB this
        // endpoint documents — so an upload inside the stated limit was refused
        // by the framework, as a 500 telling the caller the server had failed.
        //
        // That is the third time this exact shape has appeared: an undocumented
        // framework limit firing ahead of a designed one and blaming the wrong
        // party. The pattern is now named in security.md rather than fixed a
        // fourth time by surprise.
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
            is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaximumBytes;
        }

        string? boundary = Boundary(context.Request.ContentType);

        if (boundary is null)
        {
            await Refuse(context, 400,
                "Post the file as multipart/form-data with a part named 'attachment', which is "
                + "what ArcGIS clients send.").ConfigureAwait(false);
            return;
        }

        // <b>The buffer size is not a default worth keeping.</b> MultipartReader
        // reads through a 4 KB buffer and scans every byte for the boundary, and
        // measured on a 40 MB upload that was 8.2 seconds — about 5 MB/s — while
        // PostgreSQL wrote the same 640 chunks in 191 ms. The database was never
        // the bottleneck; the framing was.
        MultipartReader reader = new(boundary, context.Request.Body, bufferSize: 1024 * 1024);
        MultipartSection? section;

        while ((section = await reader.ReadNextSectionAsync(cancellation).ConfigureAwait(false))
               is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(
                    section.ContentDisposition, out ContentDispositionHeaderValue? disposition)
                || !disposition.IsFileDisposition())
            {
                continue;
            }

            // The filename is data, never a path (security.md). Only the last
            // segment survives, and anything that could traverse is stripped —
            // it is a label shown to a person, and it never reaches a filesystem
            // call or a URL path.
            string name = SafeName(disposition.FileName.Value ?? disposition.Name.Value);

            (string sniffed, Stream content) = await ContentSniffer
                .SniffAsync(section.Body, cancellation).ConfigureAwait(false);

            await using LimitedStream limited = new(content, MaximumBytes);

            try
            {
                int id = await store.AddAsync(
                    objectId, name, sniffed, section.ContentType, limited, cancellation)
                    .ConfigureAwait(false);

                await AuditAsync(
                    context, audit, $"{serviceName}/{layerId}", objectId, name, sniffed,
                    cancellation)
                    .ConfigureAwait(false);

                await Results.Json(new
                {
                    addAttachmentResult = new { objectId = id, globalId = (string?)null, success = true },
                }).ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (AttachmentTooLargeException)
            {
                await Refuse(context, 413,
                    $"An attachment may be at most {MaximumBytes / 1048576} MB. Nothing was stored.")
                    .ConfigureAwait(false);
            }
            catch (AttachmentQuotaExceededException e)
            {
                await Refuse(context, 507, e.Message).ConfigureAwait(false);
            }
            catch (Npgsql.PostgresException missing)
                when (missing.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
            {
                // <b>The feature is not there, and saying so is the whole answer.</b> The companion
                // table references the layer, so the insert is refused by the database; until
                // 2026-09-15 that reached the exception handler as *a database this server depends
                // on is unreachable*, and the access log recorded it as 200.
                await Refuse(context, 404,
                    $"There is no feature {objectId} in this layer, so nothing was attached.")
                    .ConfigureAwait(false);
            }

            return;
        }

        await Refuse(context, 400, "No file part was found in the request.").ConfigureAwait(false);
    }

    // ---------- deletion ----------

    private static async Task DeleteAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        long objectId,
        CatalogFallback catalog,
        LayerConnections connections,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await ResolveAsync(context, serviceName, layerId, catalog, connections, write: true, cancellation)
                .ConfigureAwait(false) is not { } store)
        {
            return;
        }

        string raw = context.Request.HasFormContentType
            ? context.Request.Form["attachmentIds"].ToString()
            : context.Request.Query["attachmentIds"].ToString();

        List<int> ids = [];

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                               | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out int id))
            {
                await Refuse(context, 400, $"'{part}' is not an attachment id.").ConfigureAwait(false);
                return;
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            await Refuse(context, 400, "'attachmentIds' is required, comma separated.")
                .ConfigureAwait(false);
            return;
        }

        // Only this feature's. An attachment id is a guessable integer and a
        // delete that ignored the feature would let one caller remove another
        // feature's photographs by counting.
        HashSet<int> mine =
        [
            .. (await store.ListAsync(objectId, cancellation).ConfigureAwait(false)).Select(a => a.Id),
        ];

        IReadOnlyList<int> removed = await store
            .DeleteAsync([.. ids.Where(mine.Contains)], cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, $"{serviceName}/{layerId}", objectId,
            string.Join(",", removed), "delete", cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            // <b>A failure says why — 2026-09-15.</b> It said `success: false` and nothing else, so a
            // client that shows the reason had none to show.
            deleteAttachmentResults = ids.Select(id => removed.Contains(id)
                ? (object)new { objectId = id, globalId = (string?)null, success = true }
                : new
                {
                    objectId = id,
                    globalId = (string?)null,
                    success = false,
                    error = new { code = 404, description = $"No attachment {id} on feature {objectId}." },
                }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- shared ----------

    /// <summary>
    /// Resolves the layer, checks the caller may do this, and returns its store.
    /// </summary>
    /// <remarks>
    /// <b>Reading an attachment is governed by the layer's sharing, and writing
    /// one by the edit privilege.</b> An attachment is part of a feature, so
    /// anybody who may read the feature may read what is attached to it — and
    /// adding one is an edit, because it changes what the layer holds.
    /// </remarks>
    private static async Task<PostGisAttachmentStore?> ResolveAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        LayerConnections connections,
        bool write,
        CancellationToken cancellation)
    {
        // Folder, then sharing, then status — through the one resolver, so this
        // surface cannot drift from the query endpoint's answers.
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return null;
        }

        // <b>Whose layer it is — ADR-075.</b> This asked for the privilege alone, so an attachment
        // could be added to any layer the caller could read; it asks what every other write asks.
        if (write && !await Authorize.RequireEditAsync(context, Privilege.FeaturesEdit, layer)
                .ConfigureAwait(false))
        {
            return null;
        }

        // <b>Both halves of the question, because this endpoint runs DDL.</b>
        // `PostGisAttachmentStore.EnsureTableAsync` issues `create table if not
        // exists` in the *layer's own* schema, so the guard has to be the same one
        // the field endpoints use rather than the weaker half of it.
        //
        // <b>`IsHosted` alone was the guard until 2026-09-09, and it is not enough
        // — ADR-058 §5h, arriving here second.</b> `IsHosted` says the layer's
        // *source* is the datastore and says nothing about the schema: a datastore
        // source can serve any schema of that database, and the conformance fixture
        // publishes one that does. §5h found exactly this in the field endpoints,
        // fixed it there, wrote the paragraph — *hosted is not the same as we made
        // this table* — and the correction did not carry to this file. That is
        // CLAUDE.md §2's *what else carries this?* missing an instance, and the
        // answer to *what else* was one grep for `AlterableSchema` away.
        //
        // <b>One condition, two sentences</b>, because the two states need
        // different words: a registered layer is somebody else's database, and a
        // datastore layer in a schema we did not create is our database and still
        // not our table.
        if (!HostedDataEndpoints.AlterableSchema(
                layer.Definition.IsHosted, layer.Definition.SchemaName))
        {
            // ADR-013 §4c allows reading a migrated __ATTACH table on a
            // registered source and creating one where we hold DDL rights.
            // Neither is built, and saying so beats a table that fails to be
            // created halfway through an upload.
            string because = layer.Definition.IsHosted
                ? $"it is served from the '{layer.Definition.SchemaName}' schema, which this server "
                    + "did not create. Attachments need a companion table beside the layer's own, and "
                    + "this server creates tables only in the schema it made"
                : "it is registered rather than hosted";
        
            await Refuse(context, 501,
                $"Attachments are not available on '{layer.Definition.Name}' because {because}. "
                + "Reading a migrated __ATTACH table and creating a companion table "
                + "where this server has DDL rights are both designed (ADR-013 §4c) and not "
                + "built.").ConfigureAwait(false);
            return null;
        }

        return connections.AttachmentsFor(layer);
    }

    /// <summary>Only the last path segment, and nothing that could traverse.</summary>
    private static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "attachment";
        }

        string last = name.Replace('\\', '/').Split('/')[^1];
        Span<char> cleaned = stackalloc char[Math.Min(last.Length, 200)];
        int written = 0;

        foreach (char c in last)
        {
            if (written == cleaned.Length)
            {
                break;
            }

            // Control characters out: they are what turns a filename into an
            // extra header line. Everything printable stays, because the name is
            // the uploader's and mangling it loses information for no gain once
            // the header encodes it properly.
            cleaned[written++] = char.IsControl(c) ? '_' : c;
        }

        string safe = new string(cleaned[..written]).Trim().TrimStart('.');
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static string? Boundary(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? media)
        && media.Boundary.HasValue
            ? HeaderUtilities.RemoveQuotes(media.Boundary).Value
            : null;

    private static Task AuditAsync(
        HttpContext context,
        IAuditLog audit,
        string layerName,
        long objectId,
        string name,
        string what,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                "layer.attachment",
                $"{layerName}/{objectId}",
                System.Text.Json.JsonSerializer.Serialize(new { name, what }),
                true),
            cancellation);
    }

    private static Task Refuse(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code)
            .ExecuteAsync(context);
}

/// <summary>A stream that refuses to yield more than a fixed number of bytes.</summary>
/// <remarks>
/// <b>The cap has to be enforced while reading, not before.</b> A
/// <c>Content-Length</c> is whatever the client wrote in it, and a chunked
/// upload does not carry one at all — so the only honest place to stop is at the
/// byte that crosses the line.
/// </remarks>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int got)
    {
        _read += got;

        if (_read > limit)
        {
            throw new AttachmentTooLargeException(
                $"The upload passed {limit:N0} bytes and was stopped.");
        }

        return got;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

/// <summary>An upload exceeded the per-attachment cap.</summary>
internal sealed class AttachmentTooLargeException(string message) : Exception(message);
