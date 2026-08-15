using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Features;
using GisServer.Platform.Admin;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GisServer.Host;

/// <summary>
/// Declared relationships, and the query that follows one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared rather than reverse-engineered</b> — ADR-013 §3. Reading
/// relationship classes out of a geodatabase's system tables is Esri internals,
/// which CLAUDE.md §5 forbids; it only works when the source is a geodatabase;
/// and it breaks silently when the layout changes. Declaring them instead works
/// on any schema, including one with no foreign keys at all.
/// </para>
/// <para>
/// <b>The cost of that freedom is that a declaration can be wrong</b>, and §7
/// makes validating it a condition. Both columns are checked to exist and to
/// have comparable types before a declaration is accepted. What cannot be
/// checked — and this is worth saying rather than implying — is whether the
/// values <em>mean</em> the same thing. Two integer columns always join; whether
/// the join is meaningful is a fact about the data.
/// </para>
/// </remarks>
internal static class RelationshipEndpoints
{
    /// <summary>The most related records one query returns.</summary>
    /// <remarks>
    /// Mirrors the feature query's limit, so a client that respects one is never
    /// surprised by the other.
    /// </remarks>
    public const int MaximumRelated = FeatureQuery.MaximumLimit;

    /// <summary>Maps the admin surface and the query.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/relationships", DeclareAsync);
        app.MapGet("/admin/relationships", ListAsync);
        app.MapDelete("/admin/relationships/{id:guid}", RemoveAsync);

        foreach (string prefix in (string[])
            ["/rest/services", $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}"])
        {
            app.MapGet(
                $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/queryRelatedRecords",
                QueryRelatedAsync);
            app.MapPost(
                $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/queryRelatedRecords",
                QueryRelatedAsync);
        }
    }

    /// <summary>What a caller sends to declare a relationship.</summary>
    /// <param name="Name">What to call it.</param>
    /// <param name="OriginLayer">The layer a client starts from.</param>
    /// <param name="OriginKey">Its join column.</param>
    /// <param name="RelatedLayer">The layer it reaches.</param>
    /// <param name="RelatedKey">Its join column.</param>
    /// <param name="Cardinality">OneToOne or OneToMany.</param>
    /// <param name="Composite">Whether deleting an origin deletes the related.</param>
    internal sealed record RelationshipRequest(
        string? Name,
        string? OriginLayer,
        string? OriginKey,
        string? RelatedLayer,
        string? RelatedKey,
        string? Cardinality,
        bool? Composite);

    // ---------- declaring ----------

    private static async Task DeclareAsync(
        HttpContext context,
        RelationshipRequest request,
        PostgresLayerCatalog layers,
        PostgresRelationshipCatalog relationships,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Fail(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        if (!Enum.TryParse(request.Cardinality, ignoreCase: true, out RelationshipCardinality kind)
            || !Enum.IsDefined(kind))
        {
            await Fail(context, 400,
                "'cardinality' must be OneToOne or OneToMany. Many-to-many needs an intermediate "
                + "table and a second declaration to describe it (ADR-013 §3), which is sketched "
                + "there and not specified — so it is refused rather than accepted as a word that "
                + "cannot be queried.").ConfigureAwait(false);
            return;
        }

        // <b>Refused rather than stored, because storing it would be a lie.</b>
        // ADR-013 §3 says a composite relationship cascades on delete — by the
        // database where it already declares ON DELETE CASCADE, by us in the
        // same transaction where it does not. Neither is implemented. Accepting
        // the flag would put it in the layer document, where an administrator
        // reads it and concludes that deleting a parcel removes its owners; the
        // orphans that follow are silent. D-26.
        if (request.Composite == true)
        {
            await Fail(context, 501,
                "'composite' is not implemented. ADR-013 §3 says a composite relationship "
                + "cascades on delete, and this server does not do it yet — so accepting the flag "
                + "would report a guarantee it does not honour, and the orphaned rows would be "
                + "silent. Declare the relationship without it, or add ON DELETE CASCADE to the "
                + "foreign key in the database, where PostgreSQL will enforce it.")
                .ConfigureAwait(false);
            return;
        }

        PublishedLayer? origin = await Find(layers, request.OriginLayer, cancellation)
            .ConfigureAwait(false);

        PublishedLayer? related = await Find(layers, request.RelatedLayer, cancellation)
            .ConfigureAwait(false);

        if (origin is null || related is null)
        {
            await Fail(context, 404,
                $"No layer named '{(origin is null ? request.OriginLayer : request.RelatedLayer)}'.")
                .ConfigureAwait(false);
            return;
        }

        // <b>ADR-013 §7's condition.</b> A declaration that names a column which
        // is not there produces a relationship that fails at query time, on
        // somebody else's request, long after the mistake.
        if (!await ValidateAsync(context, contexts, origin, request.OriginKey, "originKey", cancellation)
                .ConfigureAwait(false)
            || !await ValidateAsync(context, contexts, related, request.RelatedKey, "relatedKey", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        LayerRelationship declaration = new(
            Guid.Empty,
            request.Name,
            origin.Id,
            request.OriginKey!,
            related.Id,
            request.RelatedKey!,
            kind,
            request.Composite ?? false);

        Guid id;

        try
        {
            id = await relationships.DeclareAsync(declaration, cancellation).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
        {
            await Fail(context, 409, $"A relationship named '{request.Name}' already exists.")
                .ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id, current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                "relationship.declare", request.Name,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    origin = origin.Definition.Name,
                    related = related.Definition.Name,
                    cardinality = kind.ToString(),
                    composite = declaration.Composite,
                }),
                true),
            cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;

        await Results.Json(new
        {
            id,
            name = request.Name,
            origin = new { layer = origin.Definition.Name, key = request.OriginKey },
            related = new { layer = related.Definition.Name, key = request.RelatedKey },
            cardinality = kind.ToString(),
            composite = declaration.Composite,
            note = "The columns exist and their types can be compared. Nothing checks that their "
                 + "values mean the same thing, and nothing could.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Checks a join column exists on a layer and can be compared.</summary>
    private static async Task<bool> ValidateAsync(
        HttpContext context,
        ServiceContexts contexts,
        PublishedLayer layer,
        string? key,
        string field,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            await Fail(context, 400, $"'{field}' is required.").ConfigureAwait(false);
            return false;
        }

        (_, LayerDescription description) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        FieldDescription? column = description.Fields
            .FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));

        if (column is null || column.Value.Name is null)
        {
            await Fail(context, 400,
                $"'{key}' is not a column of '{layer.Definition.Name}'. Its columns are: "
                + string.Join(", ", description.Fields.Select(f => f.Name)) + ".")
                .ConfigureAwait(false);
            return false;
        }

        // A geometry or binary column joins to nothing useful, and the equality
        // it would compile to is either an error or a comparison of blobs.
        if (column.Value.Type is FieldType.Unknown or FieldType.Binary)
        {
            await Fail(context, 400,
                $"'{key}' on '{layer.Definition.Name}' is a {column.Value.Type} column, which "
                + "cannot be a join key.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    // ---------- listing and removing ----------

    private static async Task ListAsync(
        HttpContext context,
        PostgresLayerCatalog layers,
        PostgresRelationshipCatalog relationships,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminViewAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        Dictionary<Guid, string> names = (await layers.ListAsync(cancellation).ConfigureAwait(false))
            .ToDictionary(l => l.Id, l => l.Definition.Name);

        string Named(Guid id) => names.TryGetValue(id, out string? name) ? name : id.ToString();

        await Results.Json(new
        {
            relationships = (await relationships.ListAsync(cancellation).ConfigureAwait(false))
                .Select(r => new
                {
                    r.Id,
                    r.Name,
                    origin = new { layer = Named(r.OriginLayerId), key = r.OriginKey },
                    related = new { layer = Named(r.RelatedLayerId), key = r.RelatedKey },
                    cardinality = r.Cardinality.ToString(),
                    r.Composite,
                }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RemoveAsync(
        HttpContext context,
        Guid id,
        PostgresRelationshipCatalog relationships,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        bool removed = await relationships.RemoveAsync(id, cancellation).ConfigureAwait(false);

        if (!removed)
        {
            await Fail(context, 404, $"No relationship {id}.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id, current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                "relationship.remove", id.ToString(), "{}", true),
            cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            id,
            removed = true,

            // Worth saying: composite relationships cascade on delete, so
            // somebody could reasonably fear this removed rows.
            note = "The declaration was removed. No data in either layer was touched.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- querying ----------

    /// <summary>
    /// ArcGIS <c>queryRelatedRecords</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One query for the whole batch, not one per object id.</b> ADR-013 §3
    /// says this explicitly, and the naive shape is a loop: a client asking for
    /// the owners of two hundred parcels would produce two hundred round trips
    /// and take a second where this takes a few milliseconds.
    /// </para>
    /// <para>
    /// <b>Both directions.</b> A client may start from either layer, so the
    /// relationship is matched on whichever side this layer is, and the join
    /// runs the other way.
    /// </para>
    /// </remarks>
    private static async Task QueryRelatedAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        PostgresLayerCatalog layers,
        PostgresRelationshipCatalog relationships,
        ServiceContexts contexts,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, layers, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        string relationshipId = Value(context, "relationshipId");
        string objectIdsRaw = Value(context, "objectIds");

        if (!Guid.TryParse(relationshipId, out Guid wanted))
        {
            await Fail(context, 400,
                "'relationshipId' is required and is the id from /admin/relationships. It also "
                + "appears in this layer's document under 'relationships'.").ConfigureAwait(false);
            return;
        }

        LayerRelationship? relationship =
            await relationships.FindAsync(wanted, cancellation).ConfigureAwait(false);

        if (relationship is null
            || (relationship.OriginLayerId != layer.Id && relationship.RelatedLayerId != layer.Id))
        {
            await Fail(context, 404,
                $"Relationship {wanted} does not exist, or does not involve "
                + $"'{layer.Definition.Name}'.")
                .ConfigureAwait(false);
            return;
        }

        List<long> objectIds = [];

        foreach (string part in objectIdsRaw.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, CultureInfo.InvariantCulture, out long id))
            {
                await Fail(context, 400, $"'{part}' is not an object id.").ConfigureAwait(false);
                return;
            }

            objectIds.Add(id);
        }

        if (objectIds.Count == 0)
        {
            await Fail(context, 400, "'objectIds' is required, comma separated.").ConfigureAwait(false);
            return;
        }

        // Which way round this layer sits in the declaration.
        bool fromOrigin = relationship.OriginLayerId == layer.Id;

        Guid otherId = fromOrigin ? relationship.RelatedLayerId : relationship.OriginLayerId;
        string thisKey = fromOrigin ? relationship.OriginKey : relationship.RelatedKey;
        string otherKey = fromOrigin ? relationship.RelatedKey : relationship.OriginKey;

        PublishedLayer? other = await layers.FindByIdAsync(otherId, cancellation).ConfigureAwait(false);

        if (other is null)
        {
            await Fail(context, 404, "The related layer is no longer published.").ConfigureAwait(false);
            return;
        }

        // <b>The other layer's own sharing decides whether it may be read.</b>
        // A relationship must not become a way around it: relating a public
        // layer to a private one would otherwise publish the private one's rows
        // to anybody who could follow the join.
        if (!LayerAccess
            .Evaluate(other.Sharing, other.Owner, current.Principal, current.Authorization)
            .IsAllowed())
        {
            await Fail(context, 403,
                $"'{layer.Definition.Name}' is related to a layer you may not read, so the related records "
                + "cannot be returned. A relationship does not widen who may see a layer.")
                .ConfigureAwait(false);
            return;
        }

        (_, LayerDescription description) =
            await contexts.GetAsync(other, cancellation).ConfigureAwait(false);

        IReadOnlyList<RelatedGroup> groups = await connections
            .RelatedFor(layer, other)
            .QueryAsync(
                thisKey, otherKey, objectIds, description.Fields, MaximumRelated, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new
        {
            relationshipId = wanted,
            fields = description.Fields.Select(f => new
            {
                name = f.Name,
                type = FeatureServerMetadataWriter.TypeName(f.Type),
                alias = f.Name,
            }),
            relatedRecordGroups = groups.Select(g => new
            {
                objectId = g.ObjectId,
                relatedRecords = g.Records.Select(r => new { attributes = r }),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task<PublishedLayer?> Find(
        PostgresLayerCatalog layers, string? name, CancellationToken cancellation) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : await layers.FindAsync(name, cancellation).ConfigureAwait(false);

    private static string Value(HttpContext context, string name) =>
        context.Request.HasFormContentType && context.Request.Form.ContainsKey(name)
            ? context.Request.Form[name].ToString()
            : context.Request.Query[name].ToString();

    private static Task Fail(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: code)
            .ExecuteAsync(context);
}
