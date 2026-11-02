using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The recycle bin through the portal — ADR-214 §5c: a member's bin listed as <c>content/users/{u}?inRecycleBin=true</c>,
/// an entry restored by <c>items/{id}/restore</c>, and one deleted for good by <c>items/{id}/delete</c> with
/// <c>permanentDelete=true</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of v1.0.499.</b> <c>portals/self</c> did not say the organisation has a recycle bin, so
/// the ArcGIS API for Python dropped <c>permanentDelete</c>, warned, and deleted into the bin (ADR-203) a script believed
/// gone for good; and its <c>user.recyclebin</c> had nothing to list and nothing to restore through.
/// </para>
/// <para>
/// <b>Doors, not a second bin.</b> The listing reads Studio's bin and keeps to the entries Studio's own screen would show
/// the member as theirs; a restore and a purge run Studio's own handlers inside the request
/// (<see cref="AdminEndpoints.RestoreFromRecycleBinAsync"/>, <see cref="AdminEndpoints.PurgeFromRecycleBinAsync"/>), so
/// the privilege check, the store call and the <c>recyclebin.restore</c> / <c>recyclebin.purge</c> audit lines are
/// Studio's.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static void MapRecycleBinDoors(WebApplication app)
    {
        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/items/{{id}}/restore", RestoreUserItemAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }
    }

    /// <summary>Whether a <c>content/users/{u}</c> listing asked for the recycle bin, as ArcGIS's <c>inRecycleBin</c>.</summary>
    private static bool AsksForRecycleBin(HttpContext context) =>
        string.Equals(context.Request.Query["inRecycleBin"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The faces whose item ids name a recycled service.</summary>
    private static readonly string[] RecycledFaces = ["FeatureServer", "VectorTileServer", "MapServer", "ImageServer"];

    /// <summary>
    /// The entries of the bin an id names: an entry's item id, or — for a service — any of its faces' item ids, which is
    /// what a client that read the service before deleting it holds.
    /// </summary>
    private static IEnumerable<RecycleEntry> EntriesNamed(IEnumerable<RecycleEntry> entries, string id) =>
        entries.Where(entry =>
            string.Equals(entry.ItemId.ToString("N"), id.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
            || (entry.Kind is "service" or "image service"
                && RecycledFaces.Any(face =>
                    string.Equals(
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes($"{entry.ItemId:N}/{face}")))[..32],
                        id,
                        StringComparison.OrdinalIgnoreCase))));

    /// <summary>Whether an entry is in this member's own bin: theirs, or deleted by them when it had no owner.</summary>
    private static bool InBinOf(RecycleEntry entry, Guid member) =>
        entry.Owner == member || (entry.Owner is null && entry.DeletedBy == member);

    /// <summary>
    /// An entry as a portal item, with the fields the Python API's <c>RecycleItem</c> reads — <c>id</c>, <c>owner</c>,
    /// <c>title</c>, <c>type</c> — and the dates a bin is sorted by.
    /// </summary>
    /// <remarks>
    /// <b>The type is INFERRED from the entry's kind</b>, for the owner to confirm: the bin keeps what was deleted, not
    /// the item document it was shown as, so a service reads as a <c>Feature Service</c> (what every hosted and
    /// registered service's primary item is), and a document or a toolbox by its kind's name.
    /// </remarks>
    private static object RecycledItem(RecycleEntry entry, string ownerName) => new
    {
        id = entry.ItemId.ToString("N"),
        owner = ownerName,
        title = entry.Title,
        type = entry.Kind switch
        {
            "service" => "Feature Service",
            "image service" => "Image Service",
            "web map" => "Web Map",
            "document" => "Document",
            "toolbox" => "Toolbox",
            _ => entry.Kind,
        },
        typeKeywords = Array.Empty<string>(),
        modified = entry.DeletedAt.ToUnixTimeMilliseconds(),
        deleted = entry.DeletedAt.ToUnixTimeMilliseconds(),
        expires = entry.ExpiresAt.ToUnixTimeMilliseconds(),
        size = -1,
        ownerFolder = (string?)null,
    };

    /// <summary>The member's recycle bin, as <c>content/users/{u}?inRecycleBin=true</c> — the Python API's <c>user.recyclebin.content</c>.</summary>
    private static async Task<IResult> RecycleBinListingAsync(HttpContext context, RequestPrincipal current, string username, CancellationToken cancellation)
    {
        RecycleBinService recycle = context.RequestServices.GetRequiredService<RecycleBinService>();

        List<object> items =
        [
            .. (await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false))
                .Where(entry => InBinOf(entry, current.Principal.Id))
                .Select(entry => RecycledItem(entry, current.Principal.Name)),
        ];

        PortalPage.Page page = PortalPage.Of(items, context);

        return Results.Ok(new
        {
            username,
            total = page.Total,
            start = page.Start,
            num = page.Num,
            nextStart = page.NextStart,
            currentFolder = (object?)null,
            items = page.Results,
            folders = Array.Empty<object>(),
        });
    }

    /// <summary>Portal's item <c>restore</c>: Studio's Restore, for an entry of the bin the id names.</summary>
    /// <remarks>
    /// <b>The item goes back where it was.</b> ArcGIS takes an optional <c>folder</c> to restore into; Studio's restore
    /// returns the item to the folder it was deleted from, so a <c>folder</c> is refused before anything is restored
    /// rather than ignored — move it after.
    /// </remarks>
    private static async Task<IResult> RestoreUserItemAsync(HttpContext context, string username, string id, CancellationToken cancellation)
    {
        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        if (First(form, context, "folder") is { Length: > 0 })
        {
            return PortalError(400,
                "An item is restored to the folder it was deleted from; restore it without 'folder' and move it after.");
        }

        RecycleBinService recycle = context.RequestServices.GetRequiredService<RecycleBinService>();

        if (EntriesNamed(await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false), id)
                .FirstOrDefault(entry => AdminEndpoints.MayHandle(current, entry)) is not { } entry)
        {
            return PortalError(400, "Item does not exist in the recycle bin or is inaccessible.");
        }

        IServiceProvider services = context.RequestServices;
        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.RestoreFromRecycleBinAsync(
            context, entry.Id, recycle,
            services.GetRequiredService<ItemAnnotations>(),
            services.GetRequiredService<Graticula.Platform.Admin.IAuditLog>(),
            cancellation)).ConfigureAwait(false);

        return status is >= 200 and < 300
            ? Results.Ok(new { success = true, itemId = entry.ItemId.ToString("N") })
            : PortalError(status, message ?? $"'{entry.Title}' was not restored.");
    }

    /// <summary>
    /// A permanent delete of an id that is already in the bin — the Python API's <c>RecycleItem.delete()</c>: Studio's
    /// Delete for good. Null when the id names nothing in the bin the caller may handle.
    /// </summary>
    private static async Task<(bool Ok, string? Error, int Status)?> PurgeRecycledAsync(
        HttpContext context, RequestPrincipal current, string id, CancellationToken cancellation)
    {
        RecycleBinService recycle = context.RequestServices.GetRequiredService<RecycleBinService>();
        List<RecycleEntry> named =
        [
            .. EntriesNamed(await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false), id)
                .Where(entry => AdminEndpoints.MayHandle(current, entry)),
        ];

        if (named.Count == 0)
        {
            return null;
        }

        IServiceProvider services = context.RequestServices;

        foreach (RecycleEntry entry in named)
        {
            (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.PurgeFromRecycleBinAsync(
                context, entry.Id, recycle, services.GetRequiredService<Graticula.Platform.Admin.IAuditLog>(), cancellation))
                .ConfigureAwait(false);

            if (status is < 200 or >= 300)
            {
                return (false, message ?? $"'{entry.Title}' was not deleted.", status);
            }
        }

        return (true, null, 200);
    }
}
