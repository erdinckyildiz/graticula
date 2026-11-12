using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Preplanned map areas and their packages in the platform store — migrations 139 and 140, ADR-288 §11a.
/// </summary>
/// <remarks>
/// <b>Each is written with its item and its relationship in one transaction</b>, the item taking its owner and sharing from
/// the map's (an area) or the area's (a package) at the moment it is written. What a reader is shown is decided by the map
/// at read time, not by the copy, so a map shared later shows its areas without anything here being rewritten.
/// </remarks>
public sealed class PostgresMapAreaStore : IMapAreaStore
{
    private const string AreaColumns =
        "a.id, a.map_id, a.title, a.x_min, a.y_min, a.x_max, a.y_max, a.wkid, a.status, a.created_at, a.refreshed_at, "
        + "a.creator_principal_id, a.creator_name";

    private const string PackageColumns =
        "p.id, p.area_id, a.map_id, p.service_id, p.replica_id, p.file_name, p.file_path, p.sha256, p.size, p.properties::text, "
        + "p.created_at, p.refreshed_at, p.format";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store over the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresMapAreaStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<bool> CreateAsync(MapArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);

        if (!WebMaps.IsId(area.MapId))
        {
            return false;
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand insert = new(
            """
            insert into map_area (id, map_id, title, x_min, y_min, x_max, y_max, wkid, status, created_at, refreshed_at,
                                  creator_principal_id, creator_name)
            select @id, @map, @title, @xmin, @ymin, @xmax, @ymax, @wkid, @status, @created, @refreshed, @creator, @creatorname
             where exists (select 1 from web_map where id = @map)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", area.Id);
            insert.Parameters.AddWithValue("map", area.MapId);
            insert.Parameters.AddWithValue("title", area.Title);
            insert.Parameters.AddWithValue("xmin", area.XMin);
            insert.Parameters.AddWithValue("ymin", area.YMin);
            insert.Parameters.AddWithValue("xmax", area.XMax);
            insert.Parameters.AddWithValue("ymax", area.YMax);
            insert.Parameters.AddWithValue("wkid", area.Wkid);
            insert.Parameters.AddWithValue("status", area.Status);
            insert.Parameters.AddWithValue("created", area.Created);
            insert.Parameters.AddWithValue("refreshed", area.Refreshed);
            insert.Parameters.AddWithValue("creator", area.Creator);
            insert.Parameters.AddWithValue("creatorname", area.CreatorName);

            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await using (NpgsqlCommand item = new(
            """
            insert into item (id, kind, title, owner_principal_id, sharing, map_area_id)
            select @id, 'map area', @title, m.owner_principal_id, m.sharing, @id from item m where m.id = @map::uuid
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("id", area.Id);
            item.Parameters.AddWithValue("title", area.Title);
            item.Parameters.AddWithValue("map", area.MapId);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand relate = new(
            "insert into item_relationship (origin_item_id, destination_item_id, relationship_type) values (@map::uuid, @id, 'Map2Area')",
            connection, transaction))
        {
            relate.Parameters.AddWithValue("map", area.MapId);
            relate.Parameters.AddWithValue("id", area.Id);
            await relate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> AddPackageAsync(MapAreaPackage package, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand insert = new(
            """
            insert into map_area_package (id, area_id, service_id, replica_id, file_name, file_path, sha256, size, properties,
                                          created_at, refreshed_at, format)
            select @id, @area, @service, @replica, @name, @path, @sha, @size, @properties, @created, @refreshed, @format
             where exists (select 1 from map_area where id = @area)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", package.Id);
            insert.Parameters.AddWithValue("area", package.AreaId);
            insert.Parameters.AddWithValue("service", package.ServiceId);
            insert.Parameters.Add(new NpgsqlParameter("replica", NpgsqlDbType.Uuid) { Value = (object?)package.ReplicaId ?? DBNull.Value });
            insert.Parameters.AddWithValue("format", package.Format);
            insert.Parameters.AddWithValue("name", package.FileName);
            insert.Parameters.AddWithValue("path", package.FilePath);
            insert.Parameters.AddWithValue("sha", package.Sha256);
            insert.Parameters.AddWithValue("size", package.Size);
            insert.Parameters.Add(new NpgsqlParameter("properties", NpgsqlDbType.Jsonb) { Value = package.Properties });
            insert.Parameters.AddWithValue("created", package.Created);
            insert.Parameters.AddWithValue("refreshed", package.Refreshed);

            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await using (NpgsqlCommand item = new(
            """
            insert into item (id, kind, title, owner_principal_id, sharing, map_area_package_id)
            select @id, 'map area package', @title, a.owner_principal_id, a.sharing, @id from item a where a.id = @area
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("id", package.Id);
            item.Parameters.AddWithValue("title", title);
            item.Parameters.AddWithValue("area", package.AreaId);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand relate = new(
            "insert into item_relationship (origin_item_id, destination_item_id, relationship_type) values (@area, @id, 'Area2Package')",
            connection, transaction))
        {
            relate.Parameters.AddWithValue("area", package.AreaId);
            relate.Parameters.AddWithValue("id", package.Id);
            await relate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ReplacePackageAsync(MapAreaPackage package, DateTimeOffset refreshed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update map_area_package
               set replica_id = @replica, file_name = @name, file_path = @path, sha256 = @sha, size = @size,
                   properties = @properties, refreshed_at = @refreshed
             where id = @id
            """);
        command.Parameters.AddWithValue("id", package.Id);
        command.Parameters.Add(new NpgsqlParameter("replica", NpgsqlDbType.Uuid) { Value = (object?)package.ReplicaId ?? DBNull.Value });
        command.Parameters.AddWithValue("name", package.FileName);
        command.Parameters.AddWithValue("path", package.FilePath);
        command.Parameters.AddWithValue("sha", package.Sha256);
        command.Parameters.AddWithValue("size", package.Size);
        command.Parameters.Add(new NpgsqlParameter("properties", NpgsqlDbType.Jsonb) { Value = package.Properties });
        command.Parameters.AddWithValue("refreshed", refreshed);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetStatusAsync(Guid id, string status, DateTimeOffset refreshed, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update map_area set status = @status, refreshed_at = @refreshed where id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("refreshed", refreshed);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<string?> ItemKindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select kind from item where id = @id and kind in ('map area', 'map area package')");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <inheritdoc/>
    public async Task<MapArea?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {AreaColumns} from map_area a where a.id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadArea(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MapArea>> ListAsync(IReadOnlyCollection<string> mapIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapIds);
        string[] keys = [.. mapIds.Where(WebMaps.IsId)];

        if (keys.Length == 0)
        {
            return [];
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {AreaColumns} from map_area a where a.map_id = any(@maps) order by a.created_at, a.id");
        command.Parameters.AddWithValue("maps", keys);

        List<MapArea> areas = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            areas.Add(ReadArea(reader));
        }

        return areas;
    }

    /// <inheritdoc/>
    public async Task<MapAreaPackage?> FindPackageAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PackageColumns} from map_area_package p join map_area a on a.id = p.area_id where p.id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MapAreaPackage>> PackagesAsync(IReadOnlyCollection<Guid> areaIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(areaIds);

        if (areaIds.Count == 0)
        {
            return [];
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PackageColumns} from map_area_package p join map_area a on a.id = p.area_id "
            + "where p.area_id = any(@areas) order by p.created_at, p.id");
        command.Parameters.AddWithValue("areas", areaIds.ToArray());

        return await ReadPackagesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MapAreaPackage>> PackagesOfMapAsync(string mapId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapId);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PackageColumns} from map_area_package p join map_area a on a.id = p.area_id where a.map_id = @map "
            + "order by p.created_at, p.id");
        command.Parameters.AddWithValue("map", mapId);

        return await ReadPackagesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // The area's item goes with it by cascade, and the packages and their items with it.
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from map_area where id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async Task<IReadOnlyList<MapAreaPackage>> ReadPackagesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<MapAreaPackage> packages = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(ReadPackage(reader));
        }

        return packages;
    }

    private static MapArea ReadArea(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDouble(3),
        reader.GetDouble(4),
        reader.GetDouble(5),
        reader.GetDouble(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.GetGuid(11),
        reader.GetString(12));

    private static MapAreaPackage ReadPackage(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetGuid(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetString(12));
}
