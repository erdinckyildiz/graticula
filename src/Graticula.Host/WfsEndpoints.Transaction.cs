using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Wfs;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// WFS-T — ADR-169: <c>Transaction</c> with <c>Insert</c>, <c>Update</c>, <c>Replace</c> and <c>Delete</c>, in WFS
/// 2.0.0 and 1.1.0, through the writer ArcGIS <c>applyEdits</c> and OGC API Features' writes go through.
/// </summary>
/// <remarks>
/// <para>
/// <b>One write path.</b> Each layer's actions become one <see cref="EditBatch"/> on its
/// <see cref="IFeatureWriter"/>, with the same rollback, the same editing rights (ADR-075), the same capability ceiling
/// (D-179), the same tracking of who edited (ADR-064) and the same tiles emptied afterwards.
/// </para>
/// <para>
/// <b>Atomic for each layer, in the order the layers first appear.</b> The writers are a layer's each; a transaction
/// over two layers whose second fails has applied the first, and the answer says which failed.
/// </para>
/// <para>
/// <b>An Update, Replace or Delete names its features by a filter</b>, read by the same reader and compiled by the same
/// query GetFeature uses, up to the server's record ceiling; more than that is refused rather than half applied.
/// </para>
/// </remarks>
internal static partial class WfsEndpoints
{
    /// <summary>What one layer's share of a transaction does.</summary>
    private sealed class TransactionPlan(PublishedLayer layer, LayerDescription described, IFeatureSource source)
    {
        public PublishedLayer Layer { get; } = layer;

        public LayerDescription Described { get; } = described;

        public IFeatureSource Source { get; } = source;

        public List<FeatureAdd> Adds { get; } = [];

        public List<FeatureUpdate> Updates { get; } = [];

        public List<long> Deletes { get; } = [];

        public int Replaced { get; set; }
    }

    /// <summary>Answers a <c>wfs:Transaction</c> document.</summary>
    private static async Task TransactionAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, HostSettings settings, XElement document,
        CancellationToken cancellation)
    {
        WfsDialect dialect = document.Name.Namespace == XNamespace.Get(WfsDialect.V110.Wfs) ? WfsDialect.V110 : WfsDialect.V200;
        context.Items[DialectKey] = dialect;
        XNamespace wfs = dialect.Wfs;
        XNamespace fes = WfsNames.Fes;

        // OGC Filter 1.1 and GML 3.1.1 read as FES 2.0 and GML 3.2, as a 1.1.0 query is (ADR-168).
        XElement root = WfsDialect.ToFes20(document);

        Dictionary<string, string> namespaces = new(StringComparer.Ordinal);

        foreach (XAttribute declared in root.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            namespaces.TryAdd(declared.Name.LocalName == "xmlns" ? string.Empty : declared.Name.LocalName, declared.Value);
        }

        IReadOnlyList<PublishedLayer>? visible = await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        if (visible is null)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        IProjector projector = context.RequestServices.GetRequiredService<IProjector>();
        LayerConnections connections = context.RequestServices.GetRequiredService<LayerConnections>();
        List<TransactionPlan> plans = [];
        List<(TransactionPlan Plan, string? Handle)> inserted = [];
        List<(string Id, string? Handle)> replacedIds = [];

        async Task<TransactionPlan?> PlanOfAsync(string typeName, string capability)
        {
            if (!TryFind(visible, typeName, namespaces, out PublishedLayer? layer, out WfsFault? notFound))
            {
                // <b>In an Insert the feature is the value, so a type this server does not serve is InvalidValue</b> — WFS
                // 2.0.0 Table 3, which OGC's Transactional class checks with a tns:Airport. Elsewhere it is still the
                // typeNames parameter that is wrong.
                await RefuseAsync(context, capability == "Create" ? ValueFault(context, notFound!.Locator ?? "typeNames", notFound.Text) : notFound!,
                    cancellation).ConfigureAwait(false);
                return null;
            }

            if (Authorize.EditRightOf(current, layer!) == LayerAccess.EditRight.None)
            {
                await RefuseAsync(context, new WfsFault(WfsFaultCode.OperationNotSupported, "Transaction",
                    $"'{layer!.Definition.Name}' is edited by its owner, an administrator, and a group its owner has shared it "
                    + "with for editing. Sign in as one of them."), cancellation, StatusCodes.Status403Forbidden).ConfigureAwait(false);
                return null;
            }

            if (CapabilityCeilings.Refuses(layer!, capability))
            {
                await RefuseAsync(context, new WfsFault(WfsFaultCode.OperationNotSupported, "Transaction",
                    CapabilityCeilings.Explain(layer!, capability)), cancellation, StatusCodes.Status403Forbidden).ConfigureAwait(false);
                return null;
            }

            if (!layer!.Definition.HasIntegerIdentity)
            {
                await RefuseAsync(context, WfsFault.Invalid("Transaction",
                    $"'{layer.Definition.Name}' has no integer identity column, so its features cannot be named for editing."),
                    cancellation).ConfigureAwait(false);
                return null;
            }

            if (plans.FirstOrDefault(p => p.Layer == layer) is { } known)
            {
                return known;
            }

            (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            TransactionPlan plan = new(layer, described, source);
            plans.Add(plan);
            return plan;
        }

        foreach (XElement action in root.Elements())
        {
            string verb = action.Name.LocalName;

            if (action.Name.Namespace != wfs || verb is not ("Insert" or "Update" or "Replace" or "Delete"))
            {
                await RefuseAsync(context, new WfsFault(WfsFaultCode.OperationNotSupported, verb,
                    $"'{verb}' is not an action this server applies. It applies Insert, Update, Replace and Delete."),
                    cancellation).ConfigureAwait(false);
                return;
            }

            if (verb == "Insert")
            {
                foreach (XElement feature in action.Elements())
                {
                    if (await PlanOfAsync(Qualified(feature.Name, namespaces), "Create").ConfigureAwait(false) is not { } plan)
                    {
                        return;
                    }

                    if (await FeatureOfAsync(context, plan, feature, projector, cancellation).ConfigureAwait(false) is not { } read)
                    {
                        return;
                    }

                    plan.Adds.Add(new FeatureAdd(read.Attributes, read.Geometry));
                    inserted.Add((plan, (string?)action.Attribute("handle")));
                }

                continue;
            }

            string? typeName = (string?)action.Attribute("typeName") ?? (string?)action.Attribute("typeNames");

            if (verb == "Replace" && action.Elements().FirstOrDefault(e => e.Name.Namespace != fes) is { } replacement)
            {
                typeName ??= Qualified(replacement.Name, namespaces);
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                await RefuseAsync(context, WfsFault.Missing("typeName"), cancellation).ConfigureAwait(false);
                return;
            }

            if (await PlanOfAsync(typeName, verb == "Delete" ? "Delete" : "Update").ConfigureAwait(false) is not { } target)
            {
                return;
            }

            // <b>What an Update or Replace sets is read before what it selects — 2026-10-06.</b> A value the type cannot
            // hold is InvalidValue whatever the filter says, and OGC's WFS 2.0 Transactional class sends one with no
            // filter at all; checking the filter first answered MissingParameterValue to a request whose fault was its
            // value. The filter is still required: 2.0 lets an Update without one change every feature of the type,
            // and this server does not take that from an omission.
            Dictionary<string, object?> attributes = new(StringComparer.Ordinal);
            Geometry? geometry = null;

            if (verb == "Replace")
            {
                if (await FeatureOfAsync(context, target, action.Elements().First(e => e.Name.Namespace != fes), projector, cancellation)
                        .ConfigureAwait(false) is not { } whole)
                {
                    return;
                }

                attributes = whole.Attributes;
                geometry = whole.Geometry;
            }
            else if (verb == "Update")
            {
                foreach (XElement property in action.Elements(wfs + "Property"))
                {
                    string? name = (property.Element(wfs + "ValueReference") ?? property.Element(wfs + "Name"))?.Value.Trim();

                    if (string.IsNullOrEmpty(name))
                    {
                        await RefuseAsync(context, WfsFault.Missing("ValueReference"), cancellation).ConfigureAwait(false);
                        return;
                    }

                    name = name[(name.LastIndexOf(':') + 1)..];
                    XElement? value = property.Element(wfs + "Value");

                    (bool ok, Geometry? moved) = await TryPropertyAsync(
                        context, target, name, value, attributes, projector, cancellation).ConfigureAwait(false);

                    if (!ok)
                    {
                        return;
                    }

                    geometry = moved ?? geometry;
                }
            }

            if (action.Element(fes + "Filter") is not { } filter)
            {
                await RefuseAsync(context, WfsFault.Missing("Filter"), cancellation).ConfigureAwait(false);
                return;
            }

            List<long>? matched = await MatchingAsync(context, target, filter, settings, cancellation).ConfigureAwait(false);

            if (matched is null)
            {
                return;
            }

            if (verb == "Delete")
            {
                target.Deletes.AddRange(matched);
                continue;
            }

            if (verb == "Replace")
            {
                target.Replaced += matched.Count;

                // A replaced feature keeps its identity, and 2.0 names it in ReplaceResults (§15.3.6).
                replacedIds.AddRange(matched.Select(id =>
                    ($"{target.Layer.Definition.Name}.{id.ToString(CultureInfo.InvariantCulture)}", (string?)action.Attribute("handle"))));
            }

            target.Updates.AddRange(matched.Select(id => new FeatureUpdate(id, attributes, geometry)));
        }

        // Each layer's batch applied, in the order the layers were first named.
        int totalInserted = 0, totalUpdated = 0, totalDeleted = 0, totalReplaced = 0;
        Dictionary<TransactionPlan, Queue<long>> newIds = [];

        foreach (TransactionPlan plan in plans)
        {
            IFeatureWriter writer = connections.WriterFor(plan.Layer, plan.Described.Fields, plan.Described.Tracking, plan.Described.Subtypes);
            EditOutcome outcome = await writer.ApplyAsync(
                    new EditBatch(plan.Adds, plan.Updates, plan.Deletes,
                        Editor: current.Principal.Name,
                        OwnOnly: Program.OwnOnlyFor(context, plan.Layer, plan.Described)),
                    cancellation)
                .ConfigureAwait(false);

            await RecordAsync(context, plan, outcome, cancellation).ConfigureAwait(false);

            if (!outcome.AllSucceeded || outcome.RolledBack)
            {
                string why = outcome.Adds.Concat(outcome.Updates).Concat(outcome.Deletes)
                    .Where(r => !r.Succeeded).Select(r => r.Error).FirstOrDefault(e => e is { Length: > 0 }) ?? "The edits were not applied.";
                await RefuseAsync(context, new WfsFault(WfsFaultCode.OperationProcessingFailed, plan.Layer.Definition.Name,
                    $"'{plan.Layer.Definition.Name}' was not changed: {why}"
                    + (plans.IndexOf(plan) > 0 ? " The layers named before it in this transaction were changed." : string.Empty)),
                    cancellation).ConfigureAwait(false);
                return;
            }

            newIds[plan] = new Queue<long>(outcome.Adds.Select(a => a.Identity));
            totalInserted += plan.Adds.Count;
            totalReplaced += plan.Replaced;
            totalUpdated += plan.Updates.Count - plan.Replaced;
            totalDeleted += plan.Deletes.Count;
        }

        List<(string Id, string? Handle)> ids = [.. inserted.Select(i =>
            ($"{i.Plan.Layer.Definition.Name}.{newIds[i.Plan].Dequeue().ToString(CultureInfo.InvariantCulture)}", i.Handle))];

        await AnswerTransactionAsync(context, dialect, totalInserted, totalUpdated, totalReplaced, totalDeleted, ids, cancellation, replacedIds)
            .ConfigureAwait(false);
    }

    /// <summary>An element's name as a qualified type name: <c>graticula:roads</c>.</summary>
    private static string Qualified(XName name, IReadOnlyDictionary<string, string> namespaces) =>
        namespaces.FirstOrDefault(p => p.Value == name.NamespaceName && p.Key.Length > 0).Key is { } prefix
            ? $"{prefix}:{name.LocalName}"
            : name.LocalName;

    /// <summary>A feature element's properties and geometry, converted to the layer's columns and reference.</summary>
    private static async Task<(Dictionary<string, object?> Attributes, Geometry? Geometry)?> FeatureOfAsync(
        HttpContext context, TransactionPlan plan, XElement feature, IProjector projector, CancellationToken cancellation)
    {
        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);
        Geometry? geometry = null;

        foreach (XElement property in feature.Elements())
        {
            // <b>A whole feature carries what GML gives every feature, and what the server gave this one.</b>
            // gml:identifier, gml:name, gml:description and gml:boundedBy are properties of every GML feature, not
            // columns of this type, and the identity and GlobalID columns are the server's to assign. A feature read
            // with GetFeature and sent back in an Insert or a Replace carries all of them, so they are passed over
            // rather than refused — OGC's WFS 2.0 suite inserts exactly such features, and every Insert it sent was
            // refused for its gml:identifier until 2026-10-06. Naming one of them in an Update's ValueReference is
            // still an attempt to set it, and is still refused.
            if (property.Name.NamespaceName is GmlNamespace32 or GmlNamespace311
                || string.Equals(property.Name.LocalName, plan.Layer.Definition.IdentityColumn, StringComparison.OrdinalIgnoreCase)
                || IsGlobalIdColumn(plan.Described, property.Name.LocalName))
            {
                continue;
            }

            (bool ok, Geometry? moved) = await TryPropertyAsync(
                context, plan, property.Name.LocalName, property, attributes, projector, cancellation).ConfigureAwait(false);

            if (!ok)
            {
                return null;
            }

            geometry = moved ?? geometry;
        }

        return (attributes, geometry);
    }

    private const string GmlNamespace32 = "http://www.opengis.net/gml/3.2";
    private const string GmlNamespace311 = "http://www.opengis.net/gml";

    /// <summary>Whether a column is the layer's GlobalID, which the server assigns.</summary>
    private static bool IsGlobalIdColumn(LayerDescription described, string name) =>
        GlobalIds.FieldOf(described.Fields) is { } globalId && string.Equals(globalId, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A property's value refused: InvalidValue in a 2.0 transaction (WFS 2.0.0 Table 3), InvalidParameterValue in a 1.1.0
    /// one, whose code list has no InvalidValue.
    /// </summary>
    private static WfsFault ValueFault(HttpContext context, string name, string why) =>
        context.Items.TryGetValue(DialectKey, out object? dialect) && ReferenceEquals(dialect, WfsDialect.V110)
            ? WfsFault.Invalid(name, why)
            : new WfsFault(WfsFaultCode.InvalidValue, name, why);

    /// <summary>
    /// One property's value into the attributes, or — for the geometry column — the geometry it holds, moved into the
    /// layer's reference. The identity column is refused, as every write path here refuses it.
    /// </summary>
    private static async Task<(bool Ok, Geometry? Geometry)> TryPropertyAsync(
        HttpContext context, TransactionPlan plan, string name, XElement? value, Dictionary<string, object?> attributes,
        IProjector projector, CancellationToken cancellation)
    {
        PublishedLayer layer = plan.Layer;
        XNamespace xsi = WfsNames.Xsi;
        bool nil = value is null || (string?)value.Attribute(xsi + "nil") == "true";

        if (string.Equals(name, layer.Definition.GeometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            if (nil || value!.Elements().FirstOrDefault() is not { } shape)
            {
                return (true, null);
            }

            if (!GmlGeometryReader.TryRead(shape, layer.PublishedSrid, 0, out Geometry? read, out int srid, out WfsFault? bad))
            {
                await RefuseAsync(context, bad!, cancellation).ConfigureAwait(false);
                return (false, null);
            }

            if (srid != layer.Definition.Srid)
            {
                (IReadOnlyList<Geometry> moved, _) =
                    await projector.ProjectAsync([read!], srid, layer.Definition.Srid, cancellation).ConfigureAwait(false);
                read = moved[0];
            }

            return (true, read);
        }

        if (string.Equals(name, layer.Definition.IdentityColumn, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, WfsFault.Invalid(name,
                $"'{name}' is the identity column, which the server assigns and a transaction does not set."), cancellation)
                .ConfigureAwait(false);
            return (false, null);
        }

        if (plan.Described.Find(name) is not { } field)
        {
            await RefuseAsync(context, ValueFault(context, name, $"'{name}' is not a property of '{layer.Definition.Name}'."), cancellation)
                .ConfigureAwait(false);
            return (false, null);
        }

        if (nil)
        {
            attributes[field.Name] = null;
            return (true, null);
        }

        if (!OgcFeaturesEndpoints.TryValue(plan.Described, field.Name, value!.Value, out object? converted, out OgcProblem? problem))
        {
            await RefuseAsync(context, ValueFault(context, name, problem?.Detail ?? $"'{value.Value}' is not a value of '{name}'."), cancellation)
                .ConfigureAwait(false);
            return (false, null);
        }

        attributes[field.Name] = converted;
        return (true, null);
    }

    /// <summary>
    /// The object ids a filter selects in a layer, through GetFeature's own query; null when refused — a filter that
    /// cannot be read, or one selecting more than the server's record ceiling.
    /// </summary>
    private static async Task<List<long>?> MatchingAsync(
        HttpContext context, TransactionPlan plan, XElement filter, HostSettings settings, CancellationToken cancellation)
    {
        WfsRequest asked = new(
            WfsOperation.GetFeature, [plan.Layer.Definition.Name], WfsOutputFormat.Gml, settings.MaximumRecordCount + 1, 0,
            null, [], filter.ToString(SaveOptions.DisableFormatting), null, [], [plan.Layer.Definition.IdentityColumn],
            new Dictionary<string, string>(), false, null, null);

        if (!TryQuery(asked, plan.Layer, plan.Described, [], settings, settings.MaximumRecordCount + 1,
                out FeatureQuery? query, out _, out WfsFault? fault))
        {
            await RefuseAsync(context, fault!, cancellation).ConfigureAwait(false);
            return null;
        }

        List<long> ids = [];

        await foreach (Feature feature in plan.Source.ReadAsync(query!, cancellation).ConfigureAwait(false))
        {
            if (long.TryParse(Convert.ToString(feature.Id, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count > settings.MaximumRecordCount)
        {
            await RefuseAsync(context, WfsFault.Invalid("Filter",
                $"The filter selects more than {settings.MaximumRecordCount} features of '{plan.Layer.Definition.Name}', "
                + "which is more than one transaction changes. Narrow it."), cancellation).ConfigureAwait(false);
            return null;
        }

        return ids;
    }

    private static Task RecordAsync(HttpContext context, TransactionPlan plan, EditOutcome outcome, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        IAuditLog audit = context.RequestServices.GetRequiredService<IAuditLog>();

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                "wfs.transaction",
                plan.Layer.Definition.Name,
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

    /// <summary><c>wfs:TransactionResponse</c>, in the version the transaction was in.</summary>
    private static async Task AnswerTransactionAsync(
        HttpContext context, WfsDialect dialect, int inserted, int updated, int replaced, int deleted,
        List<(string Id, string? Handle)> ids, CancellationToken cancellation, List<(string Id, string? Handle)>? replacedIds = null)
    {
        context.Response.ContentType = "text/xml; charset=utf-8";
        using MemoryStream buffer = new();
        XmlWriter xml = XmlWriter.Create(buffer, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            string filterNs = dialect.IsLegacy ? WfsDialect.V110.Filter : WfsNames.Fes;
            await xml.WriteStartElementAsync("wfs", "TransactionResponse", dialect.Wfs).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", dialect.IsLegacy ? "ogc" : "fes", null, filterNs).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync(null, "version", null, dialect.Version).ConfigureAwait(false);
            await xml.WriteStartElementAsync("wfs", "TransactionSummary", dialect.Wfs).ConfigureAwait(false);
            await xml.WriteElementStringAsync("wfs", "totalInserted", dialect.Wfs, inserted.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await xml.WriteElementStringAsync("wfs", "totalUpdated", dialect.Wfs, updated.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

            if (!dialect.IsLegacy)
            {
                await xml.WriteElementStringAsync("wfs", "totalReplaced", dialect.Wfs, replaced.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }

            await xml.WriteElementStringAsync("wfs", "totalDeleted", dialect.Wfs, deleted.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);

            foreach ((string results, List<(string Id, string? Handle)> named) in
                new[] { ("InsertResults", ids), ("ReplaceResults", dialect.IsLegacy ? [] : replacedIds ?? []) })
            {
                if (named.Count == 0)
                {
                    continue;
                }

                await xml.WriteStartElementAsync("wfs", results, dialect.Wfs).ConfigureAwait(false);

                foreach ((string id, string? handle) in named)
                {
                    await xml.WriteStartElementAsync("wfs", "Feature", dialect.Wfs).ConfigureAwait(false);

                    if (handle is { Length: > 0 })
                    {
                        await xml.WriteAttributeStringAsync(null, "handle", null, handle).ConfigureAwait(false);
                    }

                    if (dialect.IsLegacy)
                    {
                        await xml.WriteStartElementAsync("ogc", "FeatureId", filterNs).ConfigureAwait(false);
                        await xml.WriteAttributeStringAsync(null, "fid", null, id).ConfigureAwait(false);
                    }
                    else
                    {
                        await xml.WriteStartElementAsync("fes", "ResourceId", filterNs).ConfigureAwait(false);
                        await xml.WriteAttributeStringAsync(null, "rid", null, id).ConfigureAwait(false);
                    }

                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellation).ConfigureAwait(false);
    }
}
