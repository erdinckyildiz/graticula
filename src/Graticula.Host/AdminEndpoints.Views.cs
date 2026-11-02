using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Hosted feature layer views — ADR-113: a second service over a hosted service's rows, through PostgreSQL views.
/// </summary>
internal static partial class AdminEndpoints
{
    private static void MapViews(WebApplication app)
    {
        app.MapPost("/admin/services/{name}/views", CreateViewAsync);
        app.MapGet("/admin/services/{name}/views", ListViewsAsync);
        app.MapPut("/admin/services/{name}/views/{index:int}/definition", SetViewDefinitionAsync);
    }

    /// <summary>What <c>POST …/views</c> reads.</summary>
    /// <param name="Name">The view's service name.</param>
    /// <param name="Layers">The source layers it shows, by index; all of them when absent.</param>
    /// <param name="Definitions">A filter per source layer index, as ArcGIS's <c>where</c> writes one.</param>
    /// <param name="Capabilities">What it offers: Query, Create, Update, Delete. Query alone when absent.</param>
    /// <param name="Sharing">private, organization or public; private when absent.</param>
    internal sealed record ViewRequest(
        string? Name,
        int[]? Layers,
        Dictionary<string, string?>? Definitions,
        string[]? Capabilities,
        string? Sharing);

    /// <summary>What <c>PUT …/views/{index}/definition</c> reads.</summary>
    /// <param name="Definition">The filter, or null or empty for every row.</param>
    internal sealed record ViewDefinitionRequest(string? Definition);

    private static readonly string[] ViewCapabilities = ["Query", "Create", "Update", "Delete"];

    /// <summary>
    /// Makes a view of a hosted service: whoever manages the source and may publish (ADR-113 §5.3).
    /// </summary>
    internal static async Task CreateViewAsync(
        HttpContext context,
        string name,
        string? folder,
        ViewRequest request,
        IAdminCatalog catalog,
        PostgresLayerCatalog published,
        PostGisImporter importer,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false)
            || !await ManagesServiceAsync(context, published, at, name, "make a view of", cancellation).ConfigureAwait(false))
        {
            return;
        }

        PublishedService source = (await published.FindServiceAsync(at, name, cancellation).ConfigureAwait(false))!;

        if (source.ViewOf is not null)
        {
            await Refuse(context, 400, $"'{name}' is itself a view. Make the view of its source instead.").ConfigureAwait(false);
            return;
        }

        // Its tables too (ADR-213 condition 9): a view of a field service carries the inspections with the sites.
        PublishedLayer[] hosted = [.. source.LayersAndTables.Where(l => l.Definition.IsHosted && l.Definition.IntegerIdentityColumn is not null)];

        if (hosted.Length == 0)
        {
            await Refuse(context, 400,
                $"'{name}' has no hosted layer. A view reads a hosted layer's rows; a registered table can be published "
                + "again instead.").ConfigureAwait(false);
            return;
        }

        string viewName = (request.Name ?? string.Empty).Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(viewName, "^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$"))
        {
            await Refuse(context, 400,
                "'name' is the view's service name: letters, digits, '_' and '-', starting with a letter or digit, at "
                + "most 40.").ConfigureAwait(false);
            return;
        }

        if (await HostedDataEndpoints.NameTakenAsync(catalog, viewName, cancellation).ConfigureAwait(false)
            || await published.FindServiceAsync(source.Folder, viewName, cancellation).ConfigureAwait(false) is not null)
        {
            await Refuse(context, 409, $"A service or layer named '{viewName}' already exists. Choose another name.")
                .ConfigureAwait(false);
            return;
        }

        PublishedLayer[] chosen = request.Layers is { Length: > 0 } asked
            ? [.. hosted.Where(l => asked.Contains(l.LayerIndex))]
            : hosted;

        if (request.Layers is { Length: > 0 } wanted && chosen.Length != wanted.Distinct().Count())
        {
            await Refuse(context, 400,
                $"'layers' names a layer '{name}' does not have as a hosted feature layer. It has "
                + string.Join(", ", hosted.Select(l => l.LayerIndex)) + ".").ConfigureAwait(false);
            return;
        }

        string[] capabilities = request.Capabilities is { Length: > 0 } caps
            ? [.. caps.Select(c => c.Trim())]
            : ["Query"];

        if (capabilities.FirstOrDefault(c => !ViewCapabilities.Contains(c, StringComparer.Ordinal)) is { } unknown)
        {
            await Refuse(context, 400, $"'{unknown}' is not a view capability; Query, Create, Update and Delete are.")
                .ConfigureAwait(false);
            return;
        }

        SharingScope sharing = HostedDataEndpoints.ParseSharing(request.Sharing);

        if (sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic).ConfigureAwait(false))
        {
            return;
        }

        // Every filter parsed before anything is made, so a mistake in the third leaves nothing behind.
        List<(PublishedLayer Layer, string? Written, ParsedWhere Parsed)> plan = [];

        foreach (PublishedLayer layer in chosen)
        {
            string? written = null;
            request.Definitions?.TryGetValue(layer.LayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), out written);

            (ParsedWhere? parsed, string? why) = await ParseViewFilterAsync(contexts, layer, written, cancellation)
                .ConfigureAwait(false);

            if (parsed is null)
            {
                await Refuse(context, 400, $"The filter for layer {layer.LayerIndex} could not be read. {why}").ConfigureAwait(false);
                return;
            }

            plan.Add((layer, string.IsNullOrWhiteSpace(written) ? null : written.Trim(), parsed.Value));
        }

        (PublishedService view, List<(Guid LayerId, int LayerIndex, string? Definition)> placed) = await PlaceViewLayersAsync(
            context, source, viewName, plan, sharing, firstLayersOfView: true, catalog, published, importer, cancellation)
            .ConfigureAwait(false);

        try
        {
            // <b>The ceiling is what was asked, so an add-only view offers no Query</b> — Survey123's shape — and the
            // edits offered are the same list without it (ADR-113 §5.3).
            await catalog.SetServiceCapabilitiesAsync(
                viewName, view.Folder, new ServiceCapabilityLimits(null, null, capabilities, null), cancellation).ConfigureAwait(false);
            await catalog.SetEditingOfferedAsync(
                viewName, view.Folder, [.. capabilities.Where(c => c != "Query")], cancellation).ConfigureAwait(false);
        }
        catch
        {
            await RemoveViewLayersAsync(catalog, importer, placed, published).ConfigureAwait(false);
            throw;
        }

        await AuditAsync(context, audit, "service.view", viewName,
            Detail(new { source = name, folder = source.Folder, layers = plan.Select(p => p.Layer.LayerIndex), capabilities }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;
        await Results.Json(new
        {
            name = viewName,
            folder = source.Folder,
            source = name,
            layers = placed.Select(p => p.LayerIndex),
            capabilities,
            url = $"/rest/services/{(source.Folder is null ? "" : source.Folder + "/")}{viewName}/FeatureServer",
        }, statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds view layers to a service that holds no layers or is already a view of the same source — ADR-257 §11: the
    /// second half of the ArcGIS API for Python's <c>create_view</c>, which makes the view service empty and then sends
    /// <c>addToDefinition</c> naming each source layer. Studio's own view checks, in the same order.
    /// </summary>
    /// <param name="context">The request; the refusal is written to it.</param>
    /// <param name="view">The service the layers go into.</param>
    /// <param name="sourceName">The source service's name, in the view's folder.</param>
    /// <param name="layers">The source layer indices.</param>
    /// <param name="definitions">A filter per source layer index, or none.</param>
    /// <param name="catalog">The admin catalogue.</param>
    /// <param name="published">The layer catalogue.</param>
    /// <param name="importer">The importer, which makes the PostgreSQL views.</param>
    /// <param name="contexts">Layer contexts, for the filters' columns.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>Each placed layer's index and name, or null when refused.</returns>
    internal static async Task<IReadOnlyList<(int Id, string Name)>?> AddViewLayersAsync(
        HttpContext context,
        PublishedService view,
        string sourceName,
        int[] layers,
        Dictionary<string, string?> definitions,
        IAdminCatalog catalog,
        PostgresLayerCatalog published,
        PostGisImporter importer,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(layers);

        if (!await ManagesServiceAsync(context, published, view.Folder, sourceName, "make a view of", cancellation).ConfigureAwait(false))
        {
            return null;
        }

        PublishedService source = (await published.FindServiceAsync(view.Folder, sourceName, cancellation).ConfigureAwait(false))!;

        if (source.ViewOf is not null)
        {
            await Refuse(context, 400, $"'{sourceName}' is itself a view. Name its source instead.").ConfigureAwait(false);
            return null;
        }

        if (source.Id == view.Id)
        {
            await Refuse(context, 400, "A service is not a view of itself.").ConfigureAwait(false);
            return null;
        }

        if (view.ViewOf is { } of ? of != source.Id : view.LayersAndTables.Count > 0)
        {
            await Refuse(context, 400, view.ViewOf is null
                ? $"'{view.Name}' already holds layers of its own, so it cannot become a view. A view starts empty."
                : $"'{view.Name}' is a view of another service. A view here shows one source.").ConfigureAwait(false);
            return null;
        }

        PublishedLayer[] hosted = [.. source.LayersAndTables.Where(l => l.Definition.IsHosted && l.Definition.IntegerIdentityColumn is not null)];
        PublishedLayer[] chosen = [.. hosted.Where(l => layers.Contains(l.LayerIndex))];

        if (layers.Length == 0 || chosen.Length != layers.Distinct().Count())
        {
            await Refuse(context, 400,
                $"The layers name a source layer '{sourceName}' does not have as a hosted feature layer. It has "
                + string.Join(", ", hosted.Select(l => l.LayerIndex)) + ".").ConfigureAwait(false);
            return null;
        }

        if (chosen.FirstOrDefault(l => view.LayersAndTables.Any(v => v.LayerIndex == l.LayerIndex)) is { } twice)
        {
            await Refuse(context, 409, $"'{view.Name}' already shows layer {twice.LayerIndex} of '{sourceName}'.").ConfigureAwait(false);
            return null;
        }

        List<(PublishedLayer Layer, string? Written, ParsedWhere Parsed)> plan = [];

        foreach (PublishedLayer layer in chosen)
        {
            string? written = null;
            definitions?.TryGetValue(layer.LayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), out written);

            (ParsedWhere? parsed, string? why) = await ParseViewFilterAsync(contexts, layer, written, cancellation).ConfigureAwait(false);

            if (parsed is null)
            {
                await Refuse(context, 400, $"The filter for layer {layer.LayerIndex} could not be read. {why}").ConfigureAwait(false);
                return null;
            }

            plan.Add((layer, string.IsNullOrWhiteSpace(written) ? null : written.Trim(), parsed.Value));
        }

        bool first = view.LayersAndTables.Count == 0 && plan.Count == 1;

        foreach ((PublishedLayer layer, _, _) in plan)
        {
            string layerName = first ? view.Name : $"{view.Name}_{layer.Definition.Name}";

            if (await HostedDataEndpoints.NameTakenAsync(catalog, layerName, cancellation).ConfigureAwait(false))
            {
                await Refuse(context, 409, $"A layer named '{layerName}' already exists. Nothing was added.").ConfigureAwait(false);
                return null;
            }
        }

        (PublishedService placedIn, List<(Guid LayerId, int LayerIndex, string? Definition)> placed) = await PlaceViewLayersAsync(
            context, source, view.Name, plan, view.Sharing, first, catalog, published, importer, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "service.view", view.Name,
            Detail(new { source = sourceName, folder = source.Folder, layers = plan.Select(p => p.Layer.LayerIndex), added = true }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return [.. placed.Select(p => (p.LayerIndex,
            placedIn.LayersAndTables.FirstOrDefault(l => l.Id == p.LayerId)?.Definition.Name ?? view.Name))];
    }

    /// <summary>
    /// Makes the PostgreSQL view and the catalogue layer for each planned source layer, in the view's service, and
    /// records the service as a view of the source. On any failure nothing it made is left.
    /// </summary>
    private static async Task<(PublishedService View, List<(Guid LayerId, int LayerIndex, string? Definition)> Placed)> PlaceViewLayersAsync(
        HttpContext context,
        PublishedService source,
        string viewName,
        List<(PublishedLayer Layer, string? Written, ParsedWhere Parsed)> plan,
        SharingScope sharing,
        bool firstLayersOfView,
        IAdminCatalog catalog,
        PostgresLayerCatalog published,
        PostGisImporter importer,
        CancellationToken cancellation)
    {
        Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(catalog, cancellation).ConfigureAwait(false);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        List<string> made = [];
        List<(Guid LayerId, int LayerIndex, string? Definition)> placed = [];

        try
        {
            foreach ((PublishedLayer layer, string? written, ParsedWhere parsed) in plan)
            {
                string table = "view_" + Guid.NewGuid().ToString("N")[..16];

                await importer.CreateViewAsync(
                    layer.Definition.TableName, table, layer.Definition.IntegerIdentityColumn!, parsed.Sql, parsed.Parameters,
                    cancellation).ConfigureAwait(false);
                made.Add(table);

                // <b>Named for the view, not the source</b>, because the admin surface finds a layer by its name and a
                // second layer called what the source's is would make every one of the source's pages ambiguous.
                string layerName = firstLayersOfView && plan.Count == 1 ? viewName : $"{viewName}_{layer.Definition.Name}";

                PublishedLayerAddress address = await catalog.PublishLayerAsync(
                    new LayerPublication(
                        layerName,
                        datastore,
                        PostGisImporter.HostedSchema,
                        table,
                        layer.Definition.GeometryColumn,
                        layer.Definition.IdentityColumn,
                        layer.Definition.IntegerIdentityColumn,
                        layer.Definition.Srid,
                        layer.GeometryType,
                        sharing,
                        ServiceName: viewName,
                        Folder: source.Folder),
                    current.Principal.Id,
                    cancellation).ConfigureAwait(false);

                placed.Add((address.Id, layer.LayerIndex, written));

                // The source's field settings come with it; the view's own Fields page changes them from here on.
                if (!layer.FieldOverrides.IsDefaultOrEmpty)
                {
                    await catalog.SetFieldOverridesAsync(address.Id, layer.FieldOverrides, cancellation).ConfigureAwait(false);
                }
            }

            PublishedService view = (await published.FindServiceAsync(source.Folder, viewName, cancellation).ConfigureAwait(false))!;

            await catalog.MakeViewAsync(view.Id, source.Id, placed, cancellation).ConfigureAwait(false);

            return ((await published.FindServiceAsync(source.Folder, viewName, cancellation).ConfigureAwait(false))!, placed);
        }
        catch
        {
            // Nothing half-made is left: the catalogue rows first, because they read the PostgreSQL views.
            foreach ((Guid id, _, _) in placed)
            {
                await catalog.UnpublishLayerAsync(id, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (string table in made)
            {
                await importer.DropViewAsync(table, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Takes back view layers <see cref="PlaceViewLayersAsync"/> placed, when a later step of a create failed.</summary>
    private static async Task RemoveViewLayersAsync(
        IAdminCatalog catalog,
        PostGisImporter importer,
        List<(Guid LayerId, int LayerIndex, string? Definition)> placed,
        PostgresLayerCatalog published)
    {
        IReadOnlyList<PublishedLayer> all = await published.ListAsync(CancellationToken.None).ConfigureAwait(false);

        foreach ((Guid id, _, _) in placed)
        {
            string? table = all.FirstOrDefault(l => l.Id == id)?.Definition.TableName;
            await catalog.UnpublishLayerAsync(id, CancellationToken.None).ConfigureAwait(false);

            if (table is not null)
            {
                await importer.DropViewAsync(table, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A source's views, or a view's source — what the service page links (ADR-113 §5.5).</summary>
    private static async Task ListViewsAsync(
        HttpContext context, string name, string? folder, PostgresLayerCatalog published, CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await ManagesServiceAsync(context, published, at, name, "list the views of", cancellation).ConfigureAwait(false))
        {
            return;
        }

        PublishedService service = (await published.FindServiceAsync(at, name, cancellation).ConfigureAwait(false))!;
        IReadOnlyList<PublishedService> all = await published.ListServicesAsync(cancellation).ConfigureAwait(false);
        PublishedService? source = service.ViewOf is { } of ? all.FirstOrDefault(s => s.Id == of) : null;

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            isView = service.ViewOf is not null,
            source = source is null ? null : new { name = source.Name, folder = source.Folder },
            views = all.Where(s => s.ViewOf == service.Id).Select(s => new
            {
                name = s.Name,
                folder = s.Folder,
                sharing = s.Sharing.ToString().ToLowerInvariant(),
                layers = s.LayersAndTables.Select(l => new { id = l.LayerIndex, definition = l.ViewDefinition }),
            }),
            layers = service.LayersAndTables.Select(l => new { id = l.LayerIndex, name = l.Definition.Name, definition = l.ViewDefinition }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Changes one view layer's filter: remakes its PostgreSQL view, then records it (ADR-113 §5.3).</summary>
    private static async Task SetViewDefinitionAsync(
        HttpContext context,
        string name,
        int index,
        string? folder,
        ViewDefinitionRequest request,
        IAdminCatalog catalog,
        PostgresLayerCatalog published,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await ManagesServiceAsync(context, published, at, name, "change the view", cancellation).ConfigureAwait(false))
        {
            return;
        }

        PublishedService view = (await published.FindServiceAsync(at, name, cancellation).ConfigureAwait(false))!;

        if (view.ViewOf is not { } sourceId
            || view.LayersAndTables.FirstOrDefault(l => l.LayerIndex == index) is not { } layer)
        {
            await Refuse(context, 404, $"'{name}' is not a view with a layer {index}.").ConfigureAwait(false);
            return;
        }

        PublishedLayer? sourceLayer = (await published.ListAsync(cancellation).ConfigureAwait(false))
            .FirstOrDefault(l => l.ServiceId == sourceId && l.LayerIndex == index);

        if (sourceLayer is null)
        {
            await Refuse(context, 409, $"The source of '{name}' no longer has a layer {index}.").ConfigureAwait(false);
            return;
        }

        (ParsedWhere? parsed, string? why) = await ParseViewFilterAsync(contexts, sourceLayer, request.Definition, cancellation)
            .ConfigureAwait(false);

        if (parsed is null)
        {
            await Refuse(context, 400, $"The filter could not be read. {why}").ConfigureAwait(false);
            return;
        }

        await importer.CreateViewAsync(
            sourceLayer.Definition.TableName, layer.Definition.TableName, sourceLayer.Definition.IntegerIdentityColumn!,
            parsed.Value.Sql, parsed.Value.Parameters, cancellation).ConfigureAwait(false);

        string? written = string.IsNullOrWhiteSpace(request.Definition) ? null : request.Definition.Trim();
        await catalog.SetViewDefinitionAsync(layer.Id, written, cancellation).ConfigureAwait(false);

        contexts.Forget(layer);
        tiles.Purge(layer.Id);

        await AuditAsync(context, audit, "service.view.definition", name, Detail(new { folder = at, layer = index, definition = written }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { name, folder = at, layer = index, definition = written }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A view filter parsed against its source layer's columns, as a query's <c>where</c> is.</summary>
    private static async Task<(ParsedWhere? Parsed, string? Why)> ParseViewFilterAsync(
        ServiceContexts contexts, PublishedLayer source, string? written, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(written))
        {
            return (new ParsedWhere(string.Empty, []), null);
        }

        (_, LayerDescription described) = await contexts.GetAsync(source, cancellation).ConfigureAwait(false);
        Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase);

        foreach (FieldDescription field in described.Fields)
        {
            types[field.Name] = field.Type;
        }

        return WhereClause.TryParse(
                written, [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote, out ParsedWhere parsed, out string? error, types)
            ? (parsed, null)
            : (null, error);
    }
}
