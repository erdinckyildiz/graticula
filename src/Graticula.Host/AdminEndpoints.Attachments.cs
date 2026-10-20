using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>Where a registered layer keeps its attachments, or null to keep none — ADR-207.</summary>
/// <param name="In"><c>source</c> beside its table in its own database, <c>datastore</c> in Graticula's, or null.</param>
internal sealed record AttachmentsSwitchRequest(string? In);

/// <summary>
/// Switching a registered layer's attachments on and off — ADR-207, by owner decision of 2026-10-20: kept beside the
/// layer's table in the database it is registered from, or in the datastore, by the administrator's choice.
/// </summary>
/// <remarks>
/// <b>Nothing is created until somebody asks</b>, on that layer. Switching on beside the table creates the companion
/// table there at once, so a role that may not create it is told now rather than on the first upload; switching off
/// drops what was made, and says how many attachments went with it.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapAttachments(WebApplication app)
    {
        app.MapGet("/admin/layers/{name}/attachments", AttachmentsStateAsync);
        app.MapPost("/admin/layers/{name}/attachments", SwitchAttachmentsAsync);
    }

    /// <summary>Where a layer keeps its attachments, and how many it holds.</summary>
    private static async Task AttachmentsStateAsync(
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

        if (AttachmentsRefusal(layer) is { } refusal)
        {
            await Results.Json(new { name, available = false, keeps = layer.Definition.IsHosted, reason = refusal })
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        (long count, long bytes) = layer.AttachmentsIn is null
            ? (0, 0)
            : await CountAttachmentsAsync(connections, layer, cancellation).ConfigureAwait(false);

        await Results.Json(new { name, available = true, @in = layer.AttachmentsIn, count, bytes })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Switches a registered layer's attachments on, in the place asked, or off.</summary>
    private static async Task SwitchAttachmentsAsync(
        HttpContext context,
        string name,
        AttachmentsSwitchRequest request,
        PostgresLayerCatalog layers,
        IAdminCatalog catalog,
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

        if (request.In is not null && !AttachmentPlaces.Known(request.In))
        {
            await Refuse(context, 400,
                $"'in' is '{AttachmentPlaces.Source}' (beside the layer's table, in its own database), "
                + $"'{AttachmentPlaces.Datastore}' (in Graticula's datastore) or null to keep none; '{request.In}' is none of them.")
                .ConfigureAwait(false);
            return;
        }

        string what = request.In is null ? "switch off the attachments of" : "switch on the attachments of";

        if (await ManagedLayerAsync(context, layers, name, what, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (AttachmentsRefusal(layer) is { } refusal)
        {
            await Refuse(context, 409, refusal).ConfigureAwait(false);
            return;
        }

        if (string.Equals(layer.AttachmentsIn, request.In, StringComparison.Ordinal))
        {
            await Results.Json(new { name, @in = request.In, changed = false }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // <b>Moving them is not built</b>: switching from one place to the other would leave the attachments behind or
        // lose them. Switching off first says how many go.
        if (layer.AttachmentsIn is not null && request.In is not null)
        {
            await Refuse(context, 409,
                $"'{name}' keeps its attachments {Where(layer.AttachmentsIn)}. Moving them is not built: switch them off "
                + "first, which removes them, and then on in the other place.").ConfigureAwait(false);
            return;
        }

        long removed = 0;

        try
        {
            if (request.In is null)
            {
                (removed, _) = await CountAttachmentsAsync(connections, layer, cancellation).ConfigureAwait(false);
                await DropAttachmentsAsync(connections, layer, cancellation).ConfigureAwait(false);
                await catalog.SetAttachmentsInAsync(layer.Id, null, cancellation).ConfigureAwait(false);
            }
            else
            {
                await catalog.SetAttachmentsInAsync(layer.Id, request.In, cancellation).ConfigureAwait(false);
                PublishedLayer switched = await layers.FindByIdAsync(layer.Id, cancellation).ConfigureAwait(false) ?? layer;

                try
                {
                    await EnsureAttachmentsAsync(connections, switched, cancellation).ConfigureAwait(false);
                }
                catch
                {
                    await catalog.SetAttachmentsInAsync(layer.Id, null, cancellation).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (InvalidOperationException e)
        {
            await Refuse(context, 409, e.Message).ConfigureAwait(false);
            return;
        }
        catch (DbException e)
        {
            await AuditAsync(
                context, audit, "layer.attachments", name, Detail(new { @in = request.In, error = e.Message }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 409,
                $"The database refused to {(request.In is null ? "remove" : "create")} the attachment table "
                + $"{(request.In is null ? Where(layer.AttachmentsIn) : Where(request.In))}: {e.Message}. Keeping attachments beside a "
                + "registered table needs the right to create a table in its schema and to reference the table; keeping them "
                + "in the datastore needs neither (ADR-207).").ConfigureAwait(false);
            return;
        }

        // The layer document's hasAttachments reads the catalogue.
        contexts.Forget(layer);

        await AuditAsync(
            context, audit, "layer.attachments", name, Detail(new { @in = request.In, removed }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            @in = request.In,
            changed = true,
            removed,
            note = request.In is null
                ? $"Attachments are off. The {removed:N0} it held were removed."
                : $"Attachments are on, kept {Where(request.In)}.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Why a layer cannot be given attachments this way, or null when it can.</summary>
    private static string? AttachmentsRefusal(PublishedLayer layer)
    {
        if (layer.Definition.IsHosted)
        {
            return "A hosted layer keeps its attachments beside its table already; there is nothing to switch.";
        }

        if (GeoParquetLocator.Is(layer.ConnectionString))
        {
            return "This layer is a file read in place, and an attachment needs a feature that can be addressed and kept "
                + "(ADR-066).";
        }

        if (layer.Definition.IntegerIdentityColumn is null)
        {
            return "This layer has no integer object id, so an attachment could not name the feature it belongs to.";
        }

        return null;
    }

    private static string Where(string? place) => place switch
    {
        AttachmentPlaces.Source => "beside its table, in its own database",
        AttachmentPlaces.Datastore => "in the datastore",
        _ => "nowhere",
    };

    // ADR-207: every store a registered layer can be given keeps its own tables (IAttachmentTables).
    private static IAttachmentTables Tables(LayerConnections connections, PublishedLayer layer) =>
        connections.AttachmentsFor(layer) as IAttachmentTables
        ?? throw new InvalidOperationException($"Layer '{layer.Definition.Name}'s attachments are kept by a store that makes no tables.");

    private static Task<(long Count, long Bytes)> CountAttachmentsAsync(
        LayerConnections connections, PublishedLayer layer, CancellationToken cancellation) =>
        Tables(connections, layer).CountAsync(cancellation);

    private static Task DropAttachmentsAsync(LayerConnections connections, PublishedLayer layer, CancellationToken cancellation) =>
        Tables(connections, layer).DropAsync(cancellation);

    private static Task EnsureAttachmentsAsync(LayerConnections connections, PublishedLayer layer, CancellationToken cancellation) =>
        Tables(connections, layer).EnsureTablesAsync(cancellation);
}
