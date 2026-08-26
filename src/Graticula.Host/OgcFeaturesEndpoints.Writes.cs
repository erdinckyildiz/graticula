using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// Creating, replacing, updating and deleting a feature over OGC API Features.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-44](../../docs/open-questions.md), owner decision 2026-08-25.</b> The question
/// was whether the write surface tracks OGC API Features Part 4, ships an extension of
/// our own, or both. The owner's answer was <em>"gerçekte nasıl ise öyle olmalı"</em> —
/// it should be however it really is — and how it really is has one shape: plain HTTP
/// verbs on the item addresses the read side already publishes. <c>POST</c> to the
/// collection, <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> to the item. There is no second
/// sensible mapping, which is why every implementation of Part 4 looks the same and why
/// inventing a parallel vocabulary would have been a choice to be different rather than a
/// choice to be right.
/// </para>
/// <para>
/// <b>No Part 4 conformance class is declared, and that is deliberate rather than
/// pending.</b> [CLAUDE.md](../../CLAUDE.md) §5 makes a public specification the citation
/// for anything a public specification defines, and this surface was built to the shape
/// rather than read out of the document clause by clause. A client that knows the shape
/// works today; the claim waits until somebody has checked it against the specification
/// and the CITE engine, which is [D-158](../../docs/architecture-debt.md)'s neighbourhood.
/// Advertising a class we have not verified is the over-claim
/// [Q-101](../../docs/open-questions.md) closed on.
/// </para>
/// <para>
/// <b>One writer underneath, which is the whole reason this is cheap.</b> Every edit here
/// becomes an <see cref="EditBatch"/> and goes through <see cref="IFeatureWriter"/> — the
/// same path ArcGIS <c>applyEdits</c> takes, with the same rollback rule, the same
/// per-service ceilings and the same audit record. Two protocol faces, one transaction
/// story; a second write path would be a second place for *did that actually commit* to
/// have a different answer.
/// </para>
/// <para>
/// <b>GeoJSON is longitude/latitude in WGS 84 by definition, and the layer usually is
/// not.</b> RFC 7946 fixes the coordinate reference, so a body posted into a layer stored
/// in web mercator has to be projected before it is written — the writer stamps the
/// layer's SRID onto the bytes it is given and transforms nothing. Skipping that would
/// store degrees as metres: a 200, a feature in the catalogue, and a shape in the Gulf of
/// Guinea.
/// </para>
/// </remarks>
internal static partial class OgcFeaturesEndpoints
{
    /// <summary>What Part 4 calls a partial update.</summary>
    private const string MergePatch = "application/merge-patch+json";

    /// <summary>Maps the four write routes.</summary>
    /// <param name="app">The application.</param>
    private static void MapWrites(WebApplication app)
    {
        const string Root = OgcNames.Base;

        app.MapPost($"{Root}/collections/{{collectionId}}/items", CreateItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapPut($"{Root}/collections/{{collectionId}}/items/{{featureId}}", ReplaceItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapPatch($"{Root}/collections/{{collectionId}}/items/{{featureId}}", UpdateItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        app.MapDelete($"{Root}/collections/{{collectionId}}/items/{{featureId}}", DeleteItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>
    /// Everything a write needs, or the refusal that has already been written.
    /// </summary>
    /// <remarks>
    /// <b>One resolution for all four verbs.</b> They differ in what they do and agree on
    /// what they need: the layer, its shape, its writer, and the two refusals that come
    /// before any of it — a collection the caller may not see, and a layer with no
    /// addressable identity.
    /// </remarks>
    private readonly record struct WriteTarget(
        PublishedLayer Layer,
        CollectionMetadata Collection,
        LayerDescription Described,
        IFeatureWriter Writer);

    private static async Task<WriteTarget?> TargetAsync(
        HttpContext context,
        string collectionId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        bool needsIdentity,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection, bool refused) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

        if (refused)
        {
            return null;
        }

        if (layer is null || collection is null)
        {
            await RefuseAsync(context, Missing(collectionId)).ConfigureAwait(false);
            return null;
        }

        // <b>The same rule ArcGIS `applyEdits` applies, for the same reason.</b> A layer
        // with no integer identity column has no way to name one of its rows, so replace,
        // update and delete have nothing to address — ADR-013 §2a. Create does not need
        // one to *work*, and is refused with the others anyway: a created feature whose
        // `Location` cannot be written is a resource the client is told exists and cannot
        // fetch.
        if (!layer.Definition.HasIntegerIdentity)
        {
            await RefuseAsync(
                context,
                OgcProblem.BadRequest(
                    $"`{collection.Id}` has no integer identity column, so its features cannot "
                    + "be addressed individually. Editing needs an addressable feature: a "
                    + "created one has to be given a URL, and a replaced or deleted one has to "
                    + "be named."))
                .ConfigureAwait(false);

            return null;
        }

        _ = needsIdentity;

        (_, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        return new WriteTarget(
            layer, collection, described, connections.WriterFor(layer, described.Fields));
    }

    /// <summary>
    /// Reads the request body, bounded by what this service accepts.
    /// </summary>
    /// <remarks>
    /// <b>Bounded before it is read, which is the rule Q-113 settled for
    /// <c>applyEdits</c>.</b> Reading an unbounded body and then measuring it is an
    /// accounting exercise: the memory is already spent. `Content-Length` is advisory and
    /// a chunked request carries none, so this refuses a body that *declares* too much and
    /// Kestrel's own limit stays the backstop for one that lies.
    /// </remarks>
    private static async Task<JsonElement?> BodyAsync(
        HttpContext context, PublishedLayer layer, CancellationToken cancellation)
    {
        if (layer.Cost.MaximumRequestBytes is { } ceiling
            && context.Request.ContentLength is { } declared
            && declared > ceiling)
        {
            await RefuseAsync(
                context,
                new OgcProblem(
                    StatusCodes.Status413PayloadTooLarge,
                    "Payload too large",
                    $"This request declares {declared} bytes and this service accepts at most "
                    + $"{ceiling}."))
                .ConfigureAwait(false);

            return null;
        }

        try
        {
            using JsonDocument document = await JsonDocument
                .ParseAsync(context.Request.Body, cancellationToken: cancellation)
                .ConfigureAwait(false);

            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            await RefuseAsync(
                context, OgcProblem.BadRequest($"The body is not JSON: {e.Message}"))
                .ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>
    /// Turns a GeoJSON feature into attributes and a geometry in the layer's reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Coerced through <see cref="TryValue"/>, which the read side already uses.</b>
    /// A JSON number is not a column type; the field list decides what a value must
    /// become, and there is one implementation of that rule rather than a second one
    /// here that eventually disagrees about what an integer is.
    /// </para>
    /// <para>
    /// <b>The identity column is not writable through this surface.</b> A body that
    /// carries it is refused rather than ignored: silently dropping the one property that
    /// decides which row this is would let a client believe it had moved a feature's
    /// identity.
    /// </para>
    /// </remarks>
    private static async Task<(Dictionary<string, object?> Attributes, Geometry? Geometry)?>
        ReadFeatureAsync(
            HttpContext context,
            WriteTarget target,
            JsonElement feature,
            bool requireGeometry,
            IProjector projector,
            CancellationToken cancellation)
    {
        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

        if (feature.ValueKind != JsonValueKind.Object)
        {
            await RefuseAsync(
                context, OgcProblem.BadRequest("The body must be a GeoJSON Feature object."))
                .ConfigureAwait(false);

            return null;
        }

        if (feature.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        target.Layer.Definition.IdentityColumn,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await RefuseAsync(
                        context,
                        OgcProblem.BadRequest(
                            $"`{property.Name}` is this collection's identity and is not writable. "
                            + "A feature's identity is the address it is published at; changing it "
                            + "would make the URL a client holds name a different feature."))
                        .ConfigureAwait(false);

                    return null;
                }

                if (property.Value.ValueKind is JsonValueKind.Null)
                {
                    attributes[property.Name] = null;
                    continue;
                }

                string text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };

                if (!TryValue(
                        target.Described, property.Name, text, out object? value,
                        out OgcProblem? problem))
                {
                    await RefuseAsync(context, problem!).ConfigureAwait(false);
                    return null;
                }

                attributes[property.Name] = value;
            }
        }

        Geometry? geometry = null;

        bool hasGeometry = feature.TryGetProperty("geometry", out JsonElement json)
            && json.ValueKind == JsonValueKind.Object;

        if (hasGeometry)
        {
            if (!GeoJsonGeometry.TryRead(json, 0, out Geometry? read, out string? why))
            {
                await RefuseAsync(context, OgcProblem.BadRequest(why!)).ConfigureAwait(false);
                return null;
            }

            geometry = read;
        }
        else if (requireGeometry)
        {
            await RefuseAsync(
                context,
                OgcProblem.BadRequest(
                    "The feature has no `geometry`. A null geometry is expressible — send "
                    + "`\"geometry\": null` — but omitting the member means *unchanged*, which "
                    + "is not a thing a create or a replace can mean."))
                .ConfigureAwait(false);

            return null;
        }

        if (geometry is not null && target.Layer.Definition.Srid != GeoJsonFeatures.GeoJsonSrid)
        {
            // <b>RFC 7946 fixes GeoJSON to WGS 84 longitude/latitude.</b> The writer stamps
            // the layer's SRID onto the bytes it is given and transforms nothing, so a body
            // written straight through would store degrees as metres — a 200, a feature in
            // the catalogue, and a shape in the Gulf of Guinea.
            try
            {
                (IReadOnlyList<Geometry> projected, _) = await projector
                    .ProjectAsync(
                        [geometry],
                        GeoJsonFeatures.GeoJsonSrid,
                        target.Layer.Definition.Srid,
                        cancellation)
                    .ConfigureAwait(false);

                geometry = projected.Count > 0 ? projected[0] : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await RefuseAsync(
                    context,
                    OgcProblem.BadRequest(
                        "The geometry could not be projected into this collection's storage "
                        + $"reference (EPSG:{target.Layer.Definition.Srid}): {e.Message}"))
                    .ConfigureAwait(false);

                return null;
            }
        }

        return (attributes, geometry);
    }

    private static async Task CreateItemAsync(
        HttpContext context,
        string collectionId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await TargetAsync(
                context, collectionId, catalog, contexts, connections, projector,
                needsIdentity: false, cancellation).ConfigureAwait(false)
            is not { } target)
        {
            return;
        }

        /*
          <b>After the target, not before it, and that is a change of order.</b> Shared update
          — [ADR-036](../../docs/adr/ADR-036-groups.md) §4a as amended 2026-08-25 — makes the
          answer depend on which groups *this collection* is shared with, so the privilege
          cannot be decided before the collection is resolved.

          <b>It leaks nothing, because resolving comes with its own refusal.</b> `TargetAsync`
          answers 404 for a collection this caller cannot read, exactly as the read path does,
          so a caller who could not previously discover a collection still cannot. What
          changed is only which of two identical-looking refusals arrives first.
        */
        if (!await Authorize
                .RequireEditAsync(context, Privilege.FeaturesEdit, target.Layer)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await BodyAsync(context, target.Layer, cancellation).ConfigureAwait(false)
            is not { } body)
        {
            return;
        }

        if (await ReadFeatureAsync(
                context, target, body, requireGeometry: true, projector, cancellation)
                .ConfigureAwait(false)
            is not { } read)
        {
            return;
        }

        EditOutcome outcome = await target.Writer
            .ApplyAsync(
                new EditBatch([new FeatureAdd(read.Attributes, read.Geometry)], [], []),
                cancellation)
            .ConfigureAwait(false);

        await AuditWriteAsync(context, audit, target, "create", outcome, cancellation)
            .ConfigureAwait(false);

        if (outcome.Adds.Count == 0 || !outcome.Adds[0].Succeeded)
        {
            await RefuseAsync(
                context,
                OgcProblem.BadRequest(
                    outcome.Adds.Count > 0 && outcome.Adds[0].Error is { Length: > 0 } why
                        ? why
                        : "The feature was not created."))
                .ConfigureAwait(false);

            return;
        }

        string id = outcome.Adds[0].Identity.ToString(CultureInfo.InvariantCulture);

        // <b>201 with a Location, which is the whole contract of a create.</b> A client
        // that posts a feature and is told 200 with no address has to guess where its
        // feature went, and guessing means listing the collection and hoping.
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location =
            $"{Origin(context)}{OgcNames.Base}/collections/{Uri.EscapeDataString(collectionId)}"
            + $"/items/{Uri.EscapeDataString(id)}";
    }

    private static async Task ReplaceItemAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        IAuditLog audit,
        CancellationToken cancellation) =>
        await ChangeAsync(
            context, collectionId, featureId, catalog, contexts, connections, projector, audit,
            replace: true, cancellation).ConfigureAwait(false);

    private static async Task UpdateItemAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        // <b>Part 4 gives PATCH a media type of its own</b>, and a body sent as plain
        // GeoJSON means something different from a merge patch — the first replaces what
        // it names and leaves the rest, the second can *remove* a property by naming it
        // null. This server implements the merge semantics, so it refuses the other type
        // rather than treating them as interchangeable.
        string? type = context.Request.ContentType;

        if (type is { Length: > 0 }
            && !type.StartsWith(MergePatch, StringComparison.OrdinalIgnoreCase)
            && !type.StartsWith(OgcNames.GeoJson, StringComparison.OrdinalIgnoreCase)
            && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(
                context,
                new OgcProblem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    $"A partial update is `{MergePatch}`; this request declared `{type}`."))
                .ConfigureAwait(false);

            return;
        }

        await ChangeAsync(
            context, collectionId, featureId, catalog, contexts, connections, projector, audit,
            replace: false, cancellation).ConfigureAwait(false);
    }

    private static async Task ChangeAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        IAuditLog audit,
        bool replace,
        CancellationToken cancellation)
    {
        if (await TargetAsync(
                context, collectionId, catalog, contexts, connections, projector,
                needsIdentity: true, cancellation).ConfigureAwait(false)
            is not { } target)
        {
            return;
        }

        /*
          <b>After the target, not before it, and that is a change of order.</b> Shared update
          — [ADR-036](../../docs/adr/ADR-036-groups.md) §4a as amended 2026-08-25 — makes the
          answer depend on which groups *this collection* is shared with, so the privilege
          cannot be decided before the collection is resolved.

          <b>It leaks nothing, because resolving comes with its own refusal.</b> `TargetAsync`
          answers 404 for a collection this caller cannot read, exactly as the read path does,
          so a caller who could not previously discover a collection still cannot. What
          changed is only which of two identical-looking refusals arrives first.
        */
        // <b>The wider privilege, as on the ArcGIS face.</b> D-20 records that this
        // server maps every update and delete to `features:fullEdit` because editor
        // tracking does not exist, so *your own features* cannot be distinguished from
        // everybody's.
        if (!await Authorize
                .RequireEditAsync(context, Privilege.FeaturesFullEdit, target.Layer)
                .ConfigureAwait(false))
        {
            return;
        }

        if (!TryFeatureNumber(target, featureId, out long objectId))
        {
            await RefuseAsync(
                context,
                OgcProblem.NotFound($"`{featureId}` is not a feature of `{target.Collection.Id}`."))
                .ConfigureAwait(false);

            return;
        }

        if (await BodyAsync(context, target.Layer, cancellation).ConfigureAwait(false)
            is not { } body)
        {
            return;
        }

        if (await ReadFeatureAsync(
                context, target, body, requireGeometry: replace, projector, cancellation)
                .ConfigureAwait(false)
            is not { } read)
        {
            return;
        }

        EditOutcome outcome = await target.Writer
            .ApplyAsync(
                new EditBatch(
                    [],
                    [new FeatureUpdate(objectId, read.Attributes, read.Geometry)],
                    []),
                cancellation)
            .ConfigureAwait(false);

        await AuditWriteAsync(
            context, audit, target, replace ? "replace" : "update", outcome, cancellation)
            .ConfigureAwait(false);

        await AnswerChangeAsync(context, target, featureId, outcome.Updates).ConfigureAwait(false);
    }

    private static async Task DeleteItemAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        IProjector projector,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await TargetAsync(
                context, collectionId, catalog, contexts, connections, projector,
                needsIdentity: true, cancellation).ConfigureAwait(false)
            is not { } target)
        {
            return;
        }

        /*
          <b>After the target, not before it, and that is a change of order.</b> Shared update
          — [ADR-036](../../docs/adr/ADR-036-groups.md) §4a as amended 2026-08-25 — makes the
          answer depend on which groups *this collection* is shared with, so the privilege
          cannot be decided before the collection is resolved.

          <b>It leaks nothing, because resolving comes with its own refusal.</b> `TargetAsync`
          answers 404 for a collection this caller cannot read, exactly as the read path does,
          so a caller who could not previously discover a collection still cannot. What
          changed is only which of two identical-looking refusals arrives first.
        */
        if (!await Authorize
                .RequireEditAsync(context, Privilege.FeaturesFullEdit, target.Layer)
                .ConfigureAwait(false))
        {
            return;
        }

        if (!TryFeatureNumber(target, featureId, out long objectId))
        {
            await RefuseAsync(
                context,
                OgcProblem.NotFound($"`{featureId}` is not a feature of `{target.Collection.Id}`."))
                .ConfigureAwait(false);

            return;
        }

        EditOutcome outcome = await target.Writer
            .ApplyAsync(new EditBatch([], [], [objectId]), cancellation)
            .ConfigureAwait(false);

        await AuditWriteAsync(context, audit, target, "delete", outcome, cancellation)
            .ConfigureAwait(false);

        await AnswerChangeAsync(context, target, featureId, outcome.Deletes).ConfigureAwait(false);
    }

    /// <summary>
    /// 204 when the one edit worked, 404 when the row was not there, 400 otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A missing row is 404 and not a failed edit.</b> The writer reports *no such
    /// feature* the same way it reports a constraint violation — as a result with an
    /// error — and answering 400 for the first would tell a client its request was
    /// malformed when the feature simply is not there.
    /// </remarks>
    private static async Task AnswerChangeAsync(
        HttpContext context,
        WriteTarget target,
        string featureId,
        IReadOnlyList<EditResult> results)
    {
        if (results.Count > 0 && results[0].Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // <b>Asked of the result rather than read out of its message.</b> The first
        // version matched the writer's text for *no such* or *not found*; the writer says
        // "No feature with object id 5 exists", so a missing row answered 400 — and the
        // defect would have come back the day somebody reworded the sentence. The
        // producer of the fact says what it is: `EditResult.Missing`.
        if (results.Count == 0 || results[0].NoSuchFeature)
        {
            await RefuseAsync(
                context,
                OgcProblem.NotFound($"`{featureId}` is not a feature of `{target.Collection.Id}`."))
                .ConfigureAwait(false);

            return;
        }

        await RefuseAsync(
            context,
            OgcProblem.BadRequest(results[0].Error ?? "The edit did not apply."))
            .ConfigureAwait(false);
    }

    /// <summary>The feature id as the identity column's number.</summary>
    /// <remarks>
    /// <b>The identity is an integer on every layer this surface will write to</b>, because
    /// <see cref="TargetAsync"/> has already refused a layer without one. What this
    /// converts is the *path segment*, which is a string and is whatever the client typed.
    /// </remarks>
    private static bool TryFeatureNumber(WriteTarget target, string featureId, out long objectId) =>
        long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId)
        && target.Described.Find(target.Layer.Definition.IdentityColumn) is not null;

    /// <summary>
    /// Records the write, with the same shape the ArcGIS face records.
    /// </summary>
    /// <remarks>
    /// <b>Counts and an outcome, not the feature.</b> An audit row carrying the coordinates
    /// would answer *what were the values* — the question an audit answers here is who
    /// changed this collection and whether it worked.
    /// </remarks>
    private static Task AuditWriteAsync(
        HttpContext context,
        IAuditLog audit,
        WriteTarget target,
        string what,
        EditOutcome outcome,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                $"ogc.{what}",
                target.Collection.Id,
                JsonSerializer.Serialize(new
                {
                    adds = outcome.Adds.Count,
                    updates = outcome.Updates.Count,
                    deletes = outcome.Deletes.Count,
                    outcome.RolledBack,
                }),
                outcome.AllSucceeded && !outcome.RolledBack),
            cancellation);
    }
}
