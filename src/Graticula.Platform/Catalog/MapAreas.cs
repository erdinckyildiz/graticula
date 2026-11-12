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
    string CreatorName);

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
    string Format = MapAreas.SqliteFormat);

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

    /// <summary>The status of an area whose packages are all written. INFERRED: the value is not measured.</summary>
    public const string Complete = "complete";
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

    /// <summary>Deletes an area, its packages and their items by cascade.</summary>
    /// <param name="id">The area.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>False when there was none.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
