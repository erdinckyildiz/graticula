using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// A service and the item it was published from — ArcGIS's <c>Service2Data</c> relationship, kept since migration 126 —
/// and the overwrite that follows it (ADR-257 §12).
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of v1.0.499: <c>manager.overwrite(csv)</c> failed.</b> The ArcGIS API for Python
/// overwrites a hosted layer by asking the layer's item for its <c>relatedItems</c> of type <c>Service2Data</c>
/// (<c>features/managers.py</c>, <c>FeatureLayerCollectionManager.overwrite</c>), replacing that data item's file with
/// <c>update</c>, and publishing the data item again with <c>overwrite=true</c>. <c>relatedItems</c> was always empty and
/// the publish refused <c>overwrite</c>, so the call stopped at its first step.
/// </para>
/// <para>
/// <b>Doors, not a second importer.</b> The portal's publish (ADR-257 §5.2) now relates the service it made to the data
/// item; <c>relatedItems</c> reads that, in both directions; and an overwrite runs Studio's own Overwrite
/// (<see cref="HostedDataEndpoints.OverwriteLayerAsync"/>, the handler behind <c>/admin/hosted/{layer}/overwrite</c>) on
/// the related service's one layer with the item's file, so its privilege, its protection rule, its reader and its
/// <c>layer.overwrite</c> audit line are Studio's.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>ArcGIS's name for the relationship between a service and the data item it was published from.</summary>
    internal const string Service2Data = "Service2Data";

    /// <summary>The relationship types this portal keeps; any other is answered with none.</summary>
    /// <summary>
    /// ArcGIS's <c>Service2Service</c>: a hosted feature layer and the hosted scene layer made from it (ADR-270 §5.4), going
    /// from the feature layer to the scene layer — the Maps SDK asks the scene layer's item for it <c>reverse</c>.
    /// </summary>
    internal const string Service2Service = "Service2Service";

    private static readonly HashSet<string> KeptRelationships = new(StringComparer.OrdinalIgnoreCase) { Service2Data, Service2Service };

    /// <summary>
    /// The item an id names, as an item row: a service (by its own id or any of its faces' item ids) or a document the
    /// caller may read; null for anything else.
    /// </summary>
    private static async Task<(Guid Item, PublishedService? Service, Document? Document)?> RelatableAsync(
        HttpContext context, CatalogFallback catalog, string id, CancellationToken cancellation)
    {
        if (await ReadableDocumentAsync(context, id, cancellation).ConfigureAwait(false) is { } document)
        {
            return (Guid.Parse(document.Id), null, document);
        }

        if (await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) is { } visible
            && visible.FirstOrDefault(service =>
                string.Equals(ItemId(service), id, StringComparison.OrdinalIgnoreCase)
                || ListedFacesOf(service).Any(face => string.Equals(ItemIdOf(service, face), id, StringComparison.OrdinalIgnoreCase)))
                is { } found)
        {
            return (found.Id, found, null);
        }

        return null;
    }

    /// <summary>
    /// Portal's <c>relatedItems</c>: the items related to this one by the types asked, <c>forward</c> (the item is the
    /// origin) unless <c>direction=reverse</c> — each as the portal writes it, and only those the caller may see.
    /// </summary>
    /// <remarks>
    /// <b>What ArcGIS documents:</b> <c>relationshipType</c>, one type or several separated by commas, and
    /// <c>direction</c>, <c>forward</c> or <c>reverse</c>; the answer is <c>total</c> and <c>relatedItems</c>. A
    /// <c>Service2Data</c> relationship goes from the service to the data item. Types this portal does not keep answer
    /// none, as an item with no such relationship would; an unknown <c>direction</c> is refused.
    /// </remarks>
    private static async Task<IResult> RelatedItemsAsync(
        HttpContext context, CatalogFallback catalog, IItemAnnotationStore annotations, string id, CancellationToken cancellation)
    {
        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);
        string direction = First(form, context, "direction");

        if (direction.Length > 0 && direction is not ("forward" or "reverse"))
        {
            return PortalError(400, "'direction' is forward or reverse.");
        }

        List<object> related = [];

        if (await RelatableAsync(context, catalog, id, cancellation).ConfigureAwait(false) is { } item)
        {
            IReadOnlyList<PublishedService> visible =
                await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) ?? [];

            foreach (string type in First(form, context, "relationshipType")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(KeptRelationships.Contains)
                         .Select(t => KeptRelationships.First(k => k.Equals(t, StringComparison.OrdinalIgnoreCase)))
                         .Distinct(StringComparer.Ordinal))
            {
                foreach (Guid other in await annotations.RelatedAsync(item.Item, type, direction != "reverse", cancellation)
                             .ConfigureAwait(false))
                {
                    if (visible.FirstOrDefault(s => s.Id == other) is { } service)
                    {
                        related.Add(Item(context, service, PrimaryFace(service)));
                    }
                    else if (await ReadableDocumentAsync(context, other.ToString("N"), cancellation).ConfigureAwait(false) is { } document)
                    {
                        related.Add(DocumentItem(context, document));
                    }
                }
            }
        }

        return Results.Ok(new { total = related.Count, relatedItems = related });
    }

    /// <summary>
    /// Portal's <c>info/publishParameters.json</c> for a service: what this portal knows of how it was published — its
    /// name, and for one published from a data item, that item's file type and the coordinates it was read by.
    /// </summary>
    /// <remarks>
    /// <b>INFERRED, for the owner to confirm.</b> ArcGIS keeps the parameters a service was published with and answers
    /// them here; this portal does not keep them, so it answers the service's <c>name</c> and, for a service with a
    /// <c>Service2Data</c> item, <c>type</c> and <c>locationType: coordinates</c> — the only location type the portal's
    /// publish takes (ADR-257 §5.3). The ArcGIS API for Python reads this before an overwrite and lays it over the
    /// service's own definition.
    /// </remarks>
    private static async Task<IResult> PublishParametersJsonAsync(
        HttpContext context, CatalogFallback catalog, IItemAnnotationStore annotations, string id, CancellationToken cancellation)
    {
        if (await RelatableAsync(context, catalog, id, cancellation).ConfigureAwait(false) is not { Service: { } service })
        {
            return Unknown("Item");
        }

        string? type = null;

        foreach (Guid source in await annotations.RelatedAsync(service.Id, Service2Data, forward: true, cancellation).ConfigureAwait(false))
        {
            if (await ReadableDocumentAsync(context, source.ToString("N"), cancellation).ConfigureAwait(false) is { } document)
            {
                type = DataFileTypes.FirstOrDefault(pair =>
                    string.Equals(pair.Value, document.Kind.ArcGisType, StringComparison.OrdinalIgnoreCase)).Key;
                break;
            }
        }

        return Results.Ok(type is null
            ? new { name = service.Name }
            : (object)new { name = service.Name, type, locationType = "coordinates" });
    }

    /// <summary>
    /// The portal's <c>publish</c> with <c>overwrite=true</c> of a data item: Studio's Overwrite of the one layer of the
    /// service the item was published to, with the item's file as it now is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What ArcGIS documents:</b> <c>overwrite=true</c> on <c>publish</c> replaces the service the item was published
    /// to, keeping its item id, URL and settings. The ArcGIS API for Python replaces the item's file with <c>update</c>
    /// first and then publishes it again; this reads the file the item now holds.
    /// </para>
    /// <para>
    /// <b>One service, one layer.</b> An item related to no service, or to more than one, is refused, as is a service of
    /// several layers: one file replaces one layer here, as Studio's Overwrite does. The file is read as Studio reads an
    /// overwrite (CSV, Excel, GeoJSON, a zipped shapefile), with <c>x</c>, <c>y</c> and <c>srid</c> from
    /// <c>publishParameters</c> where it names them.
    /// </para>
    /// </remarks>
    private static async Task<IResult> OverwriteFromItemAsync(
        HttpContext context, IFormCollection form, Document document, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IItemAnnotationStore annotations = services.GetRequiredService<IItemAnnotationStore>();

        IReadOnlyList<Guid> published = await annotations
            .RelatedAsync(Guid.Parse(document.Id), Service2Data, forward: false, cancellation).ConfigureAwait(false);

        if (published.Count != 1)
        {
            return PortalError(400, published.Count == 0
                ? $"'{document.Title}' has not been published to a service here, so there is nothing to overwrite; publish it without 'overwrite'."
                : $"'{document.Title}' was published to {published.Count} services, and an overwrite replaces one.");
        }

        PostgresLayerCatalog catalog = services.GetRequiredService<PostgresLayerCatalog>();

        if ((await catalog.ListServicesAsync(cancellation).ConfigureAwait(false)).FirstOrDefault(s => s.Id == published[0])
            is not { } service)
        {
            return PortalError(400, $"The service '{document.Title}' was published to no longer exists; publish it without 'overwrite'.");
        }

        if (service.LayersAndTables.Count != 1)
        {
            return PortalError(400,
                $"'{service.Name}' has {service.LayersAndTables.Count} layers, and an overwrite from one file replaces one; "
                + "overwrite each layer in Studio. Nothing was written.");
        }

        if (PublishParametersOf(First(form, context, "publishParameters"), out string? unreadable) is not { } asked)
        {
            return PortalError(400, unreadable!);
        }

        if (await services.GetRequiredService<IDocumentStore>().ContentAsync(document.Id, cancellation).ConfigureAwait(false)
            is not { } content)
        {
            return Unknown("Item");
        }

        Dictionary<string, StringValues> fields = new(StringComparer.OrdinalIgnoreCase);

        if (asked.X is { } x)
        {
            fields["x"] = x;
        }

        if (asked.Y is { } y)
        {
            fields["y"] = y;
        }

        if (asked.Srid is { } srid)
        {
            fields["srid"] = srid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using MemoryStream bytes = new(content, writable: false);
        FormFile file = new(bytes, 0, bytes.Length, "file", document.FileName) { Headers = new HeaderDictionary() };

        (int status, string? message, JsonElement _) = await WithFormAsync(context, fields, file, () =>
            InnerAnswerAsync(context, () => HostedDataEndpoints.OverwriteLayerAsync(
                context, service.LayersAndTables[0].Definition.Name, service.QualifiedName, services, cancellation)))
            .ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return PortalError(status, message ?? $"'{service.Name}' was not overwritten.");
        }

        string url = $"{context.Request.Scheme}://{context.Request.Host}/rest/services/{Uri.EscapeDataString(service.QualifiedName).Replace("%2F", "/", StringComparison.Ordinal)}/FeatureServer";

        return Results.Ok(new
        {
            services = new[]
            {
                new
                {
                    type = "Feature Service",
                    serviceurl = url,
                    encodedServiceURL = url,
                    size = document.Size,
                    jobId = FinishedJob(),
                    serviceItemId = service.Id.ToString("N"),
                    serviceItemType = "Feature Service",
                    success = true,
                },
            },
        });
    }
}
