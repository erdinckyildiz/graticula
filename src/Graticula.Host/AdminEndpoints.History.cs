using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Graticula.Host;

/// <summary>Turns a layer's history on or off.</summary>
/// <param name="Enabled">Whether the layer keeps its history.</param>
internal sealed record HistorySwitchRequest(bool Enabled);

/// <summary>Which version to put a feature back as.</summary>
/// <param name="Version">The version's id, from the feature's history.</param>
internal sealed record HistoryRestoreRequest(long Version);

/// <summary>
/// A hosted layer's history — ADR-078.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may do what.</b> Switching history on or off is the layer's owner's, under
/// <c>content:publishFeatures</c>, like its symbology and its visible range: it decides what the
/// datastore keeps about the layer and what every write to it costs. Reading the history is anybody's
/// who may read the layer, because an ArcGIS client that may query the layer may ask it for any
/// <c>historicMoment</c> — a stricter rule here would be a rule the other door does not keep.
/// Restoring is an edit and asks what any edit asks (ADR-075).
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>The most changes one page of a layer's history holds.</summary>
    private const int HistoryPage = 100;

    private static void MapHistory(WebApplication app)
    {
        app.MapGet("/admin/layers/{name}/history", HistoryAsync);
        app.MapPost("/admin/layers/{name}/history", SwitchHistoryAsync);
        app.MapGet("/admin/layers/{name}/history/{objectId:long}", FeatureHistoryAsync);
        app.MapPost("/admin/layers/{name}/history/{objectId:long}/restore", RestoreFeatureAsync);
    }

    /// <summary>Whether the layer keeps its history, and its newest changes.</summary>
    private static async Task HistoryAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        if (await ReadableLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (HistoryRefusal(layer) is { } refusal)
        {
            await Results.Json(new { name, available = false, enabled = false, reason = refusal })
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        DateTimeOffset? before = null;

        if (context.Request.Query["before"].ToString() is { Length: > 0 } raw)
        {
            if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                await Refuse(context, 400, $"'before' must be a date and time; '{raw}' is not.").ConfigureAwait(false);
                return;
            }

            before = parsed;
        }

        PostGisFeatureHistory history = connections.HistoryFor(layer);

        try
        {
            if (!await history.IsOnAsync(cancellation).ConfigureAwait(false))
            {
                await Results.Json(new { name, available = true, enabled = false, changes = Array.Empty<object>() })
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<FeatureChange> changes =
                await history.ChangesAsync(before, HistoryPage + 1, cancellation).ConfigureAwait(false);

            bool more = changes.Count > HistoryPage;
            List<FeatureChange> page = changes.Take(HistoryPage).ToList();

            await Results.Json(new
            {
                name,
                available = true,
                enabled = true,
                changes = page.Select(c => new
                {
                    version = c.HistoryId,
                    objectId = c.ObjectId,
                    at = c.At,
                    kind = c.Kind,
                    editor = c.Editor,
                    direct = c.Direct,
                }),
                next = more ? page[^1].At.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null,
            }).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (NpgsqlException e)
        {
            await Refuse(context, 502, $"The layer's history could not be read: {e.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Turns history on or off.</summary>
    private static async Task SwitchHistoryAsync(
        HttpContext context,
        string name,
        HistorySwitchRequest request,
        PostgresLayerCatalog layers,
        LayerConnections connections,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        string what = request.Enabled ? "turn on the history of" : "turn off the history of";

        if (await ManagedLayerAsync(context, layers, name, what, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (HistoryRefusal(layer) is { } refusal)
        {
            await Refuse(context, 409, refusal).ConfigureAwait(false);
            return;
        }

        PostGisFeatureHistory history = connections.HistoryFor(layer);
        long features = 0;

        try
        {
            if (request.Enabled)
            {
                features = await history.EnableAsync(cancellation).ConfigureAwait(false);
            }
            else
            {
                await history.DisableAsync(cancellation).ConfigureAwait(false);
            }
        }
        catch (NpgsqlException e)
        {
            await AuditAsync(
                context, audit, "layer.history", name, Detail(new { enabled = request.Enabled, error = e.Message }),
                succeeded: false, cancellation).ConfigureAwait(false);
            await Refuse(context, 502, $"The datastore refused: {e.Message}").ConfigureAwait(false);
            return;
        }

        // The layer document's isDataArchived and the query's historicMoment both read the description.
        contexts.Forget(layer);

        await AuditAsync(
            context, audit, "layer.history", name, Detail(new { enabled = request.Enabled, features }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            enabled = request.Enabled,
            features,
            note = request.Enabled
                ? $"History is on. It begins now, with the {features:N0} features the layer holds; every change from here is kept, "
                    + "including changes made directly in the database."
                : "History is off, and the history it had is gone.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One feature's versions, oldest first.</summary>
    private static async Task FeatureHistoryAsync(
        HttpContext context,
        string name,
        long objectId,
        PostgresLayerCatalog layers,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        if (await ReadableLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (HistoryRefusal(layer) is { } refusal)
        {
            await Refuse(context, 409, refusal).ConfigureAwait(false);
            return;
        }

        PostGisFeatureHistory history = connections.HistoryFor(layer);

        if (!await history.IsOnAsync(cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 409, $"Layer '{name}' does not keep its history.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<FeatureVersion> versions = await history.VersionsAsync(objectId, cancellation).ConfigureAwait(false);

        if (versions.Count == 0)
        {
            await Refuse(context, 404, $"Feature {objectId} of '{name}' has no history.").ConfigureAwait(false);
            return;
        }

        string identity = layer.Definition.IntegerIdentityColumn ?? layer.Definition.IdentityColumn;

        await Results.Json(new
        {
            name,
            objectId,
            current = versions[^1].To is null,
            versions = versions.Select(v => new
            {
                version = v.HistoryId,
                from = v.From,
                to = v.To,
                opened = v.OpenedBy,
                closed = v.ClosedBy,
                editor = v.Editor,
                direct = v.Direct,
                endedBy = v.EndedBy,
                endedDirect = v.EndedDirect,
                restoredFrom = v.RestoredFrom,
                attributes = Without(v.Attributes, identity),
                geometry = v.GeoJson is null ? (JsonElement?)null : JsonDocument.Parse(v.GeoJson).RootElement.Clone(),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Puts a feature back as one of its versions was.</summary>
    private static async Task RestoreFeatureAsync(
        HttpContext context,
        string name,
        long objectId,
        HistoryRestoreRequest request,
        PostgresLayerCatalog layers,
        LayerConnections connections,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ReadableLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (HistoryRefusal(layer) is { } refusal)
        {
            await Refuse(context, 409, refusal).ConfigureAwait(false);
            return;
        }

        // An edit, and asked as one: the owner, an administrator, or a group shared for editing (ADR-075).
        if (!await Authorize.RequireEditAsync(context, Privilege.FeaturesEdit, layer).ConfigureAwait(false))
        {
            return;
        }

        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        if (!description.Archived)
        {
            await Refuse(context, 409, $"Layer '{name}' does not keep its history.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        RestoreOutcome outcome;

        try
        {
            outcome = await connections.HistoryFor(layer)
                .RestoreAsync(objectId, request.Version, current.Principal.Name, description.Tracking, cancellation)
                .ConfigureAwait(false);
        }
        catch (PostgresException e)
        {
            await AuditAsync(
                context, audit, "layer.history.restore", name,
                Detail(new { objectId, version = request.Version, error = e.MessageText }),
                succeeded: false, cancellation).ConfigureAwait(false);
            await Refuse(context, 409, $"The version could not be written back: {e.MessageText}").ConfigureAwait(false);
            return;
        }

        if (outcome.Result == RestoreResult.NoSuchVersion)
        {
            await Refuse(context, 404, $"Feature {objectId} of '{name}' has no version {request.Version}.").ConfigureAwait(false);
            return;
        }

        // A restore changes what the layer draws, like any edit does (ADR-069).
        tiles.Purge(layer.Id);

        await AuditAsync(
            context, audit, "layer.history.restore", name,
            Detail(new { objectId, version = request.Version, result = outcome.Result.ToString() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            objectId,
            restored = request.Version,
            result = outcome.Result == RestoreResult.Recreated ? "recreated" : "updated",
            note = outcome.AttachmentsNotRestored
                ? "The feature is back under its old object id. Attachments are not kept in history, so any it had are not."
                : "The feature is back as that version was. The restore is itself a new version in the history.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Why a layer can have no history, or null when it can.</summary>
    /// <remarks>
    /// <b>Hosted, in the hosted schema, with an integer object id</b> — ADR-002 §4.2 and ADR-078 §5.1.
    /// The refusal says which, because "not available" with no reason is a switch that looks broken.
    /// </remarks>
    private static string? HistoryRefusal(PublishedLayer layer)
    {
        if (!HostedDataEndpoints.AlterableSchema(layer.Definition.IsHosted, layer.Definition.SchemaName))
        {
            return "History is kept by a trigger and a table beside the layer's, and this server runs that kind of "
                + "change only in the datastore it owns. This layer's data lives in a database it does not "
                + "(ADR-002 §4.2), so it cannot keep a history here.";
        }

        if (layer.Definition.IntegerIdentityColumn is null)
        {
            return "This layer has no integer object id, so a version could not name the feature it belongs to.";
        }

        return null;
    }

    /// <summary>A version's attributes without the object id, which the response carries once.</summary>
    private static Dictionary<string, JsonElement> Without(JsonElement attributes, string identity)
    {
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (JsonProperty property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, identity, StringComparison.Ordinal))
            {
                values[property.Name] = property.Value.Clone();
            }
        }

        return values;
    }
}
