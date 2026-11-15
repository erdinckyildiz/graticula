using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>
/// A preplanned map area — ADR-288 §11a: an extent of a web map that has been packaged for taking offline. An item of its
/// own (<c>Map Area</c>), related to its map by <c>Map2Area</c>.
/// </summary>
/// <param name="Id">The area's id, which is its item's.</param>
/// <param name="MapId">The web map it is an area of (32 lower-case hexadecimal characters).</param>
/// <param name="Title">What it is called.</param>
/// <param name="XMin">The extent's west edge, in <paramref name="Wkid"/>.</param>
/// <param name="YMin">The extent's south edge.</param>
/// <param name="XMax">The extent's east edge.</param>
/// <param name="YMax">The extent's north edge.</param>
/// <param name="Wkid">The reference the extent was sent in.</param>
/// <param name="Status">Where the area is; <see cref="MapAreas.Complete"/> once every package is written.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Refreshed">When its packages were last written.</param>
/// <param name="Creator">Who made it; the owner of its packages' replicas.</param>
/// <param name="CreatorName">That principal's name.</param>
/// <param name="PackageRefreshSchedule">A Quartz cron expression, ArcGIS's <c>packageRefreshSchedule</c>, or null for never (D-405).</param>
/// <param name="RefreshParams">ArcGIS's <c>mapAreaRefreshParams</c> as JSON text, or null for <c>{"type":"never"}</c>.</param>
/// <param name="ScheduledUpdates">ArcGIS's <c>mapAreasScheduledUpdatesEnabled</c>: whether the schedule runs.</param>
/// <param name="TileScaleMin">ArcGIS's <c>mapAreaTileScale.minScale</c> (the coarsest scale packaged), or null for the server's levels.</param>
/// <param name="TileScaleMax">ArcGIS's <c>mapAreaTileScale.maxScale</c> (the finest scale packaged), or null.</param>
/// <param name="Origin">The address of this server the area was last made or refreshed through, which a background refresh writes from.</param>
public sealed record MapArea(
    Guid Id,
    string MapId,
    string Title,
    double XMin,
    double YMin,
    double XMax,
    double YMax,
    int Wkid,
    string Status,
    DateTimeOffset Created,
    DateTimeOffset Refreshed,
    Guid Creator,
    string CreatorName,
    string? PackageRefreshSchedule = null,
    string? RefreshParams = null,
    bool ScheduledUpdates = false,
    double? TileScaleMin = null,
    double? TileScaleMax = null,
    string? Origin = null);

/// <summary>What an area keeps about when and how deep its packages are refreshed (D-405).</summary>
/// <param name="PackageRefreshSchedule">A Quartz cron expression, or null.</param>
/// <param name="RefreshParams"><c>mapAreaRefreshParams</c> as JSON text, or null.</param>
/// <param name="ScheduledUpdates">Whether the schedule runs.</param>
/// <param name="TileScaleMin">The coarsest scale packaged, or null.</param>
/// <param name="TileScaleMax">The finest scale packaged, or null.</param>
public sealed record MapAreaSettings(
    string? PackageRefreshSchedule, string? RefreshParams, bool ScheduledUpdates, double? TileScaleMin, double? TileScaleMax);

/// <summary>
/// A package taken from a map area: either one registered replica of one feature service, written once as a mobile
/// geodatabase (<c>SQLite Geodatabase</c>), or a vector tile package of the map's basemap (<c>Vector Tile Package</c>).
/// An item of its own, related to its area by <c>Area2Package</c>.
/// </summary>
/// <param name="Id">The package's id, which is its item's, and which a refresh keeps.</param>
/// <param name="AreaId">The area it belongs to.</param>
/// <param name="MapId">The area's map.</param>
/// <param name="ServiceId">The feature service, or the vector tile service, it was taken from.</param>
/// <param name="ReplicaId">The replica registered when it was written; null for a vector tile package, which has none.</param>
/// <param name="FileName">The file's name as a client is told it, ending in <c>.geodatabase</c> or <c>.vtpk</c>.</param>
/// <param name="FilePath">Where the file is, relative to the server directories.</param>
/// <param name="Sha256">The file's SHA-256 in lower-case hexadecimal.</param>
/// <param name="Size">The file's length in bytes.</param>
/// <param name="Properties">The item's <c>properties</c> as JSON text: what the replica was made of.</param>
/// <param name="Created">When the package was first written.</param>
/// <param name="Refreshed">When it was last written.</param>
/// <param name="Format"><see cref="MapAreas.SqliteFormat"/> or <see cref="MapAreas.VtpkFormat"/>.</param>
/// <param name="Status">
/// <see cref="MapAreas.PackageComplete"/>, <see cref="MapAreas.PackageProcessing"/> while its export job runs, or
/// <see cref="MapAreas.PackageFailed"/> when the job ended without a file (D-410).
/// </param>
/// <param name="ExportJobId">The export job writing the file (a basemap's package), or null.</param>
/// <param name="Error">Why the last write failed, or null.</param>
public sealed record MapAreaPackage(
    Guid Id,
    Guid AreaId,
    string MapId,
    Guid ServiceId,
    Guid? ReplicaId,
    string FileName,
    string FilePath,
    string Sha256,
    long Size,
    string Properties,
    DateTimeOffset Created,
    DateTimeOffset Refreshed,
    string Format = MapAreas.SqliteFormat,
    string Status = MapAreas.PackageComplete,
    Guid? ExportJobId = null,
    string? Error = null);

/// <summary>The words a map area uses.</summary>
public static class MapAreas
{
    /// <summary>The item kind of an area.</summary>
    public const string AreaKind = "map area";

    /// <summary>The item kind of a package.</summary>
    public const string PackageKind = "map area package";

    /// <summary>ArcGIS's relationship from a web map to an area of it.</summary>
    public const string Map2Area = "Map2Area";

    /// <summary>ArcGIS's relationship from an area to a package taken from it.</summary>
    public const string Area2Package = "Area2Package";

    /// <summary>A package that is a mobile geodatabase: a replica of a feature service.</summary>
    public const string SqliteFormat = "sqlite";

    /// <summary>A package that is a vector tile package of a basemap.</summary>
    public const string VtpkFormat = "vtpk";

    /// <summary>
    /// The status of an area whose packages are all written - one of the three words an Enterprise portal uses
    /// (<c>complete</c>, <c>processing</c>, <c>failed</c>; research note section 5a.1).
    /// </summary>
    public const string Complete = "complete";

    /// <summary>
    /// The scales a package covers when an area states none, as <c>mapAreaTileScale</c>: the grid's first level down to
    /// 1:100, which is what Field Maps asks for by default (D-403). INFERRED: the portal always states a scale; this is only
    /// what an area made without one answers.
    /// </summary>
    public const double DefaultTileScaleMin = 591657527.591555;

    /// <summary>The finest scale an area covers when it states none; see <see cref="DefaultTileScaleMin"/>.</summary>
    public const double DefaultTileScaleMax = 100;

    /// <summary>The status of an area while a package of it is still being written by its export job.</summary>
    public const string Processing = "processing";

    /// <summary>The status of an area one of whose packages could not be written.</summary>
    public const string Failed = "failed";

    /// <summary>A package whose file is written.</summary>
    public const string PackageComplete = "complete";

    /// <summary>A package whose export job has not finished. INFERRED: the words follow the area's.</summary>
    public const string PackageProcessing = "processing";

    /// <summary>A package whose export job ended without a file.</summary>
    public const string PackageFailed = "failed";

    /// <summary>
    /// The status of an area whose packages' files and replicas were put away since they were written - the map or the service
    /// went to the recycle bin and was brought back, or the service's sync was turned off (D-407). INFERRED: the word is
    /// ours; refreshing the area writes the packages again and sets <see cref="Complete"/>.
    /// </summary>
    public const string NeedsRefresh = "needs refresh";
}

/// <summary>
/// Where preplanned map areas and their packages are kept — ADR-288 §11a, migration 139.
/// </summary>
/// <remarks>
/// <b>An area's and a package's item are written with them</b>, with the relationship to the map or the area, in one
/// transaction; deleting a map or an area deletes the rest by cascade. <b>The store does not touch files or replicas</b>:
/// the caller reads what a delete will take (<see cref="PackagesOfMapAsync"/>) before it deletes, and removes the files
/// and unregisters the replicas after.
/// </remarks>
public interface IMapAreaStore
{
    /// <summary>
    /// Records an area, its item (owned and shared as the map's item is) and its <c>Map2Area</c> relationship.
    /// </summary>
    /// <param name="area">The area; its <see cref="MapArea.MapId"/> must be a saved map.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was recorded; false when the map does not exist.</returns>
    Task<bool> CreateAsync(MapArea area, CancellationToken cancellationToken);

    /// <summary>
    /// Records a package, its item (owned and shared as the area's) and its <c>Area2Package</c> relationship.
    /// </summary>
    /// <param name="package">The package; its area must exist.</param>
    /// <param name="title">The package item's title.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was recorded; false when the area does not exist.</returns>
    Task<bool> AddPackageAsync(MapAreaPackage package, string title, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces what a package holds — its replica, file and properties — keeping its id, and notes when its area was
    /// refreshed.
    /// </summary>
    /// <param name="package">The package as it is now.</param>
    /// <param name="refreshed">When.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there is no such package.</returns>
    Task<bool> ReplacePackageAsync(MapAreaPackage package, DateTimeOffset refreshed, CancellationToken cancellationToken);

    /// <summary>Sets an area's status and refreshed time.</summary>
    /// <param name="id">The area.</param>
    /// <param name="status">The status.</param>
    /// <param name="refreshed">When its packages were last written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there is no such area.</returns>
    Task<bool> SetStatusAsync(Guid id, string status, DateTimeOffset refreshed, CancellationToken cancellationToken);

    /// <summary>Whether an id is a map area's or a package's item, in one lookup by key.</summary>
    /// <param name="id">The item.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see cref="MapAreas.AreaKind"/>, <see cref="MapAreas.PackageKind"/>, or null for any other item or none.</returns>
    Task<string?> ItemKindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One area, or null.</summary>
    /// <param name="id">The area.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The area, or null.</returns>
    Task<MapArea?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The areas of these maps, oldest first.</summary>
    /// <param name="mapIds">The maps.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Their areas.</returns>
    Task<IReadOnlyList<MapArea>> ListAsync(IReadOnlyCollection<string> mapIds, CancellationToken cancellationToken);

    /// <summary>One package, or null.</summary>
    /// <param name="id">The package.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The package, or null.</returns>
    Task<MapAreaPackage?> FindPackageAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The packages of these areas, in the order they were written.</summary>
    /// <param name="areaIds">The areas.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Their packages.</returns>
    Task<IReadOnlyList<MapAreaPackage>> PackagesAsync(IReadOnlyCollection<Guid> areaIds, CancellationToken cancellationToken);

    /// <summary>Every package of every area of a map — what deleting the map will take with it.</summary>
    /// <param name="mapId">The map.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The packages.</returns>
    Task<IReadOnlyList<MapAreaPackage>> PackagesOfMapAsync(string mapId, CancellationToken cancellationToken);

    /// <summary>Every package of a service, in any area of any map - what deleting the service, or turning its sync off, puts away.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The packages.</returns>
    Task<IReadOnlyList<MapAreaPackage>> PackagesOfServiceAsync(Guid serviceId, CancellationToken cancellationToken);

    /// <summary>Every package of every area of the web maps a member owns - what removing the member takes with them.</summary>
    /// <param name="memberName">The member's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The packages.</returns>
    Task<IReadOnlyList<MapAreaPackage>> PackagesOfOwnerAsync(string memberName, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <see cref="MapAreas.NeedsRefresh"/> on the areas of a map, or the areas holding a package of a service - what
    /// restoring either from the recycle bin, or turning sync off, leaves behind: rows whose files and replicas are gone.
    /// </summary>
    /// <param name="mapId">The map whose areas to mark, or null.</param>
    /// <param name="serviceId">The service whose packages' areas to mark, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many areas were marked.</returns>
    Task<int> MarkNeedsRefreshAsync(string? mapId, Guid? serviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Every file name a package row names, in any organisation's rows and in the recycle bin's copies of them - what
    /// the orphan sweep (D-407) must not remove.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The recorded names.</returns>
    Task<IReadOnlySet<string>> FilesAsync(CancellationToken cancellationToken);

    /// <summary>The package a running export job is writing, or null (D-410).</summary>
    /// <param name="job">The export job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The package.</returns>
    Task<MapAreaPackage?> FindPackageByJobAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a package's export job is running again for it: the new job, the properties the job will write, and
    /// <see cref="MapAreas.PackageProcessing"/> - keeping the old file, which stays what a client downloads until the new one
    /// is written (D-410).
    /// </summary>
    /// <param name="id">The package.</param>
    /// <param name="job">The export job.</param>
    /// <param name="properties">The item's <c>properties</c> as JSON text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there is no such package.</returns>
    Task<bool> MarkPackageProcessingAsync(Guid id, Guid job, string properties, CancellationToken cancellationToken);

    /// <summary>
    /// Records the file an export job wrote for a package: complete, with the file's name, size and hash, and the area's status
    /// worked out again from its packages (D-410).
    /// </summary>
    /// <param name="job">The export job.</param>
    /// <param name="filePath">The recorded name of the file.</param>
    /// <param name="sha256">Its hash.</param>
    /// <param name="size">Its length.</param>
    /// <param name="written">When it was written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The package as it was before, whose old file the caller removes; null when no package waits for the job.</returns>
    Task<MapAreaPackage?> CompletePackageAsync(
        Guid job, string filePath, string sha256, long size, DateTimeOffset written, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a package's export job ended without a file. A package with an older file keeps it and stays complete, with
    /// the reason noted; one with none is failed (D-410).
    /// </summary>
    /// <param name="job">The export job.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether a package was waiting for the job.</returns>
    Task<bool> FailPackageAsync(Guid job, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Fails the packages whose export job ended without telling them (a lease lost and a job reclaimed as failed, a job
    /// removed) - the hourly sweep's pass (D-410).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many were failed.</returns>
    Task<int> FailStrandedPackagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Works an area's status out from its packages - <see cref="MapAreas.Processing"/> while any is, <see cref="MapAreas.Failed"/>
    /// when one failed, otherwise <see cref="MapAreas.Complete"/> - and notes when its packages were last written.
    /// </summary>
    /// <param name="id">The area.</param>
    /// <param name="refreshed">When.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status, or null when there is no such area.</returns>
    Task<string?> SettleStatusAsync(Guid id, DateTimeOffset refreshed, CancellationToken cancellationToken);

    /// <summary>Replaces an area's refresh schedule and tile scale (D-405).</summary>
    /// <param name="id">The area.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there is no such area.</returns>
    Task<bool> SetSettingsAsync(Guid id, MapAreaSettings settings, CancellationToken cancellationToken);

    /// <summary>Records the address of this server an area was last reached through.</summary>
    /// <param name="id">The area.</param>
    /// <param name="origin">The address.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there is no such area.</returns>
    Task<bool> SetOriginAsync(Guid id, string origin, CancellationToken cancellationToken);

    /// <summary>Every area of every organisation whose schedule is set and on, with the organisation it belongs to.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The areas.</returns>
    Task<IReadOnlyList<(MapArea Area, Guid Tenant)>> ScheduledAsync(CancellationToken cancellationToken);

    /// <summary>Deletes an area, its packages and their items by cascade.</summary>
    /// <param name="id">The area.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there was none.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
