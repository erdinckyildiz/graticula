using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// The basemap gallery an administrator chose, and the default basemap in it — ADR-184.
/// </summary>
/// <remarks>
/// <para>
/// <b>A group, as ArcGIS Enterprise's gallery is.</b> Every vector tile service shared with it is a basemap, and so
/// is every saved web map; the portal names the group to its clients as <c>basemapGalleryGroupQuery</c>.
/// </para>
/// <para>
/// <b>Read from the store on every ask</b>, for the reason <see cref="ServerGround"/> gives.
/// </para>
/// </remarks>
internal sealed class BasemapGallery
{
    /// <summary>The gallery group's setting in the store: the group's id.</summary>
    public const string GalleryName = "basemap_gallery";

    /// <summary>The default basemap's setting in the store: <see cref="OpenStreetMap"/> or a gallery item's id.</summary>
    public const string DefaultName = "default_basemap";

    /// <summary>The default that is OpenStreetMap's tiles, whatever else is set.</summary>
    public const string OpenStreetMap = "osm";

    private readonly IServerSettingStore _store;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">Where the choice is kept.</param>
    public BasemapGallery(IServerSettingStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>What is chosen.</summary>
    /// <param name="Group">The gallery group, or null for none.</param>
    /// <param name="Default">The default: <see cref="OpenStreetMap"/>, an item id, or null for ADR-086's order.</param>
    /// <param name="ChangedAt">When the gallery was last set, or null.</param>
    internal sealed record Reading(Guid? Group, string? Default, DateTimeOffset? ChangedAt)
    {
        /// <summary>The query a portal client finds the gallery group by, or empty.</summary>
        public string Query => Group is { } id ? $"id:{id:N}" : string.Empty;
    }

    /// <summary>What is chosen. An unreadable store reads as nothing chosen, as the ground does.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The reading.</returns>
    public async Task<Reading> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            StoredSetting? gallery = await _store.ReadAsync(GalleryName, cancellation).ConfigureAwait(false);
            StoredSetting? chosen = await _store.ReadAsync(DefaultName, cancellation).ConfigureAwait(false);

            Guid? group = Guid.TryParse(gallery?.Value, out Guid id) ? id : null;
            string? value = string.IsNullOrWhiteSpace(chosen?.Value) ? null : chosen.Value.Trim();

            return new Reading(group, value, gallery?.ChangedAt ?? chosen?.ChangedAt);
        }
        catch (System.Data.Common.DbException)
        {
            return new Reading(null, null, null);
        }
    }

    /// <summary>Stores both, a null removing its row.</summary>
    /// <param name="group">The gallery group, or null.</param>
    /// <param name="chosen">The default, or null.</param>
    /// <param name="changedBy">Who changed it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>What was stored before.</returns>
    public async Task<Reading> SetAsync(Guid? group, string? chosen, Guid? changedBy, CancellationToken cancellation)
    {
        StoredSetting? galleryBefore = await _store
            .WriteAsync(GalleryName, group?.ToString("N"), changedBy, cancellation)
            .ConfigureAwait(false);
        StoredSetting? chosenBefore = await _store
            .WriteAsync(DefaultName, chosen, changedBy, cancellation)
            .ConfigureAwait(false);

        return new Reading(
            Guid.TryParse(galleryBefore?.Value, out Guid id) ? id : null,
            chosenBefore?.Value,
            galleryBefore?.ChangedAt);
    }
}
