using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The recycle bin — ADR-203: what its owner deleted, listed to them; everything, to whoever manages all content.
/// </summary>
internal static partial class AdminEndpoints
{
    private static void MapRecycleBin(WebApplication app)
    {
        app.MapGet("/admin/recycle-bin", ListRecycleBinAsync);
        app.MapPost("/admin/recycle-bin/{id:guid}/restore", RestoreFromRecycleBinAsync);
        app.MapDelete("/admin/recycle-bin/{id:guid}", PurgeFromRecycleBinAsync);
    }

    /// <summary>Whether the caller may see and act on an entry: its owner, who deleted it, or whoever manages all content.</summary>
    private static bool MayHandle(RequestPrincipal current, RecycleEntry entry) =>
        current.Authorization.Allows(Privilege.AdminManageAllContent)
        || entry.Owner == current.Principal.Id
        || entry.DeletedBy == current.Principal.Id;

    private static async Task ListRecycleBinAsync(HttpContext context, RecycleBinService recycle, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to see what you have deleted.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<RecycleEntry> entries = await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            retentionDays = (int)recycle.Retention.TotalDays,
            entries = entries.Where(e => MayHandle(current, e)).Select(e => new
            {
                id = e.Id,
                item = e.ItemId.ToString("N"),
                kind = e.Kind,
                title = e.Title,
                deleted = e.DeletedAt,
                expires = e.ExpiresAt,
                tables = e.Relations.Count(r => r.Kind != "function"),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RestoreFromRecycleBinAsync(
        HttpContext context, Guid id, RecycleBinService recycle, ItemAnnotations annotations, IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await recycle.Bin.FindAsync(id, cancellation).ConfigureAwait(false) is not { } entry || !MayHandle(current, entry))
        {
            await Refuse(context, 404, $"Nothing in the recycle bin as {id} that you may restore.").ConfigureAwait(false);
            return;
        }

        string? refused = await recycle.RestoreAsync(entry, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "recyclebin.restore", entry.Title,
            Detail(new { entry = id, kind = entry.Kind, item = entry.ItemId, refused }), succeeded: refused is null, cancellation)
            .ConfigureAwait(false);

        if (refused is not null)
        {
            await Refuse(context, 409, refused).ConfigureAwait(false);
            return;
        }

        await annotations.RefreshAsync(cancellation).ConfigureAwait(false);
        await Results.Json(new { restored = entry.ItemId.ToString("N"), kind = entry.Kind, title = entry.Title })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task PurgeFromRecycleBinAsync(
        HttpContext context, Guid id, RecycleBinService recycle, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await recycle.Bin.FindAsync(id, cancellation).ConfigureAwait(false) is not { } entry || !MayHandle(current, entry))
        {
            await Refuse(context, 404, $"Nothing in the recycle bin as {id} that you may delete.").ConfigureAwait(false);
            return;
        }

        await recycle.PurgeAsync(entry, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "recyclebin.purge", entry.Title, Detail(new { entry = id, kind = entry.Kind }),
            succeeded: true, cancellation).ConfigureAwait(false);
        context.Response.StatusCode = 204;
    }
}
