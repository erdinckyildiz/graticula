using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>Reads and writes registered coverages in the platform store.</summary>
/// <remarks>
/// <para>
/// <b>Its own class beside <see cref="PostgresLayerCatalog"/></b>, for the reason
/// <see cref="PublishedCoverage"/> is its own type: a coverage is a file registered in
/// place and a layer is a table, and one query cannot read both without every column
/// of each being nullable in the other's half.
/// </para>
/// <para>
/// <b>The sharing, status and owner come from <c>service</c>, exactly as they do for a
/// layer.</b> That is not a convenience — it is what makes a private coverage private
/// by the mechanism that is already tested. A second sharing column here would be a
/// second thing to get right, and the first one to drift.
/// </para>
/// </remarks>
public sealed class PostgresCoverageCatalog : ICoverageCatalog
{
    private const string Columns = """
        c.id, c.service_id, s.name, s.folder, c.name, c.path, c.srid,
        c.width, c.height, c.band_count, c.sample_kind, c.no_data,
        c.min_x, c.min_y, c.max_x, c.max_y,
        c.tile_width, c.tile_height, c.overview_count, c.style,
        s.sharing, s.status, s.owner_principal_id,
        s.created_at, s.updated_at,
        (select coalesce(array_agg(gi.group_id), '{}')
           from sharing_group_item gi where gi.service_id = s.id) as shared_with_groups,
        s.description, s.tags, s.content_folder_id, c.download, c.images, c.classes
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads coverages from a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresCoverageCatalog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PublishedCoverage>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            select {Columns}
            from coverage c
            join service s on s.id = c.service_id
            order by coalesce(s.folder, ''), lower(s.name)
            """);

        List<PublishedCoverage> found = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<PublishedCoverage?> FindAsync(
        string? folder, string serviceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            select {Columns}
            from coverage c
            join service s on s.id = c.service_id
            where lower(s.name) = lower(@name)
              and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc/>
    public async Task<PublishedCoverage> RegisterAsync(
        string? folder,
        string serviceName,
        string path,
        CoverageInfo info,
        Guid? owner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(info);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // <b>One transaction, because a service with no coverage is a service that
        // answers with nothing and cannot say why.</b> The `on delete cascade` handles
        // the other direction; this handles the moment between the two inserts.
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid serviceId = Guid.NewGuid();
        Guid coverageId = Guid.NewGuid();

        await using (NpgsqlCommand service = new(
            """
            insert into service (id, name, folder, kind, owner_principal_id, sharing, status)
            values (@id, @name, @folder, 'ImageServer', @owner, 'private', 'started')
            """,
            connection,
            transaction))
        {
            service.Parameters.AddWithValue("id", serviceId);
            service.Parameters.AddWithValue("name", serviceName);
            service.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
            service.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);

            await service.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand coverage = new(
            """
            insert into coverage (
                id, service_id, name, path, srid, width, height, band_count, sample_kind,
                no_data, min_x, min_y, max_x, max_y, tile_width, tile_height, overview_count)
            values (
                @id, @service, @name, @path, @srid, @width, @height, @bands, @kind,
                @noData, @minX, @minY, @maxX, @maxY, @tileWidth, @tileHeight, @overviews)
            """,
            connection,
            transaction))
        {
            coverage.Parameters.AddWithValue("id", coverageId);
            coverage.Parameters.AddWithValue("service", serviceId);
            coverage.Parameters.AddWithValue("name", serviceName);
            coverage.Parameters.AddWithValue("path", path);
            coverage.Parameters.AddWithValue("srid", info.Srid);
            coverage.Parameters.AddWithValue("width", info.Width);
            coverage.Parameters.AddWithValue("height", info.Height);
            coverage.Parameters.AddWithValue("bands", info.Bands.Count);
            coverage.Parameters.AddWithValue("kind", (int)info.Bands[0].Kind);
            coverage.Parameters.AddWithValue(
                "noData", (object?)info.Bands[0].NoData ?? DBNull.Value);
            coverage.Parameters.AddWithValue("minX", info.Extent.MinX);
            coverage.Parameters.AddWithValue("minY", info.Extent.MinY);
            coverage.Parameters.AddWithValue("maxX", info.Extent.MaxX);
            coverage.Parameters.AddWithValue("maxY", info.Extent.MaxY);
            coverage.Parameters.AddWithValue("tileWidth", info.TileWidth);
            coverage.Parameters.AddWithValue("tileHeight", info.TileHeight);
            coverage.Parameters.AddWithValue("overviews", info.Overviews.Count);

            await coverage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PublishedCoverage(
            coverageId,
            serviceId,
            serviceName,
            folder,
            serviceName,
            path,
            info,
            null,
            SharingScope.Private,
            ServiceStatus.Started,
            owner);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(
        string? folder, string serviceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // <b>The service goes and the coverage follows it.</b> `on delete cascade` is
        // declared on the coverage's foreign key, so deleting the container is one
        // statement rather than two with a window between them — the same reasoning
        // registration uses for its transaction, in the other direction.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            delete from service s
             using coverage c
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetImagesAsync(
        string? folder, string serviceName, IReadOnlyList<CoverageImageEntry> images, int? nextId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(images);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update coverage c
               set images = @images::jsonb
              from service s
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        int next = Math.Max(nextId ?? 1, images.Count == 0 ? 1 : images.Max(i => i.Id) + 1);
        command.Parameters.AddWithValue("images", System.Text.Json.JsonSerializer.Serialize(new
        {
            next,
            images = images.Select(i => new { id = i.Id, file = i.File, name = i.Name, acquired = i.Acquired }),
        }));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetClassesAsync(
        string? folder, string serviceName, IReadOnlyList<CoverageClassEntry>? classes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update coverage c
               set classes = @classes::jsonb
              from service s
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("classes", classes is null
            ? DBNull.Value
            : System.Text.Json.JsonSerializer.Serialize(classes.Select(c => new { value = c.Value, name = c.Name, colour = c.Colour })));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static List<CoverageClassEntry> ClassesFrom(string json)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(c => new CoverageClassEntry(
            c.GetProperty("value").GetDouble(),
            c.GetProperty("name").GetString() ?? string.Empty,
            c.TryGetProperty("colour", out System.Text.Json.JsonElement colour) && colour.ValueKind == System.Text.Json.JsonValueKind.String
                ? colour.GetString() : null))];
    }

    private static int? NextFrom(string json)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && document.RootElement.TryGetProperty("next", out System.Text.Json.JsonElement next) ? next.GetInt32() : null;
    }

    private static List<CoverageImageEntry> ImagesFrom(string json, out int? next)
    {
        List<CoverageImageEntry> images = [];
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement list = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            ? document.RootElement.GetProperty("images") : document.RootElement;
        next = NextFrom(json);

        foreach (System.Text.Json.JsonElement image in list.EnumerateArray())
        {
            images.Add(new CoverageImageEntry(
                image.GetProperty("id").GetInt32(),
                image.GetProperty("file").GetString() ?? string.Empty,
                image.GetProperty("name").GetString() ?? string.Empty,
                image.TryGetProperty("acquired", out System.Text.Json.JsonElement when) && when.ValueKind == System.Text.Json.JsonValueKind.String
                    ? when.GetDateTimeOffset() : null));
        }

        return images;
    }

    /// <inheritdoc/>
    public async Task<bool> SetDownloadAsync(
        string? folder, string serviceName, bool download, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update coverage c
               set download = @download
              from service s
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("download", download);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ReplaceImageAsync(
        string? folder, string serviceName, string path, CoverageInfo info, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(info);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update coverage c
               set path = @path, srid = @srid, width = @width, height = @height, band_count = @bands,
                   sample_kind = @kind, no_data = @noData, min_x = @minX, min_y = @minY, max_x = @maxX, max_y = @maxY,
                   tile_width = @tileWidth, tile_height = @tileHeight, overview_count = @overviews
              from service s
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("srid", info.Srid);
        command.Parameters.AddWithValue("width", info.Width);
        command.Parameters.AddWithValue("height", info.Height);
        command.Parameters.AddWithValue("bands", info.Bands.Count);
        command.Parameters.AddWithValue("kind", (int)info.Bands[0].Kind);
        command.Parameters.AddWithValue("noData", (object?)info.Bands[0].NoData ?? DBNull.Value);
        command.Parameters.AddWithValue("minX", info.Extent.MinX);
        command.Parameters.AddWithValue("minY", info.Extent.MinY);
        command.Parameters.AddWithValue("maxX", info.Extent.MaxX);
        command.Parameters.AddWithValue("maxY", info.Extent.MaxY);
        command.Parameters.AddWithValue("tileWidth", info.TileWidth);
        command.Parameters.AddWithValue("tileHeight", info.TileHeight);
        command.Parameters.AddWithValue("overviews", info.Overviews.Count);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetStyleAsync(
        string? folder, string serviceName, string? style, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update coverage c
               set style = @style
              from service s
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("style", (object?)style ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetStatusAsync(
        string? folder, string serviceName, ServiceStatus status, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // The join to `coverage` is what keeps this route from reaching a feature
        // service that happens to share a name in another folder's spelling: this
        // endpoint administers coverages and should refuse anything else rather than
        // quietly succeed against it.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update service s
               set status = @status, updated_at = now()
              from coverage c
             where c.service_id = s.id
               and lower(s.name) = lower(@name)
               and coalesce(s.folder, '') = coalesce(@folder, '')
            """);

        command.Parameters.AddWithValue("name", serviceName);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "status", status == ServiceStatus.Started ? "started" : "stopped");

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Rebuilds what registration read, without opening the file.
    /// </summary>
    /// <remarks>
    /// <b>The overviews are reconstructed by halving rather than stored one by one.</b>
    /// A COG's pyramid is powers of two by construction, so the count is the whole of
    /// the information and a table of levels would be four columns saying what one
    /// integer already says. If a format arrives whose overviews are not halvings, this
    /// is the assumption that has to be paid for — stated here rather than discovered.
    /// </remarks>
    private static PublishedCoverage Read(NpgsqlDataReader reader)
    {
        int width = reader.GetInt32(7);
        int height = reader.GetInt32(8);
        int bandCount = reader.GetInt32(9);
        SampleKind kind = (SampleKind)reader.GetInt32(10);
        double? noData = reader.IsDBNull(11) ? null : reader.GetDouble(11);
        int overviewCount = reader.GetInt32(18);

        List<BandInfo> bands = new(bandCount);

        for (int i = 0; i < bandCount; i++)
        {
            bands.Add(new BandInfo(i, kind, noData, null, null));
        }

        List<OverviewInfo> overviews = new(overviewCount);

        for (int i = 1; i <= overviewCount; i++)
        {
            int divisor = 1 << i;

            overviews.Add(new OverviewInfo(
                i, Math.Max(1, width / divisor), Math.Max(1, height / divisor)));
        }

        CoverageInfo info = new(
            width,
            height,
            reader.GetInt32(6),
            new Envelope(
                reader.GetDouble(12), reader.GetDouble(13),
                reader.GetDouble(14), reader.GetDouble(15)),
            bands,
            overviews,
            reader.GetInt32(16),
            reader.GetInt32(17));

        return new PublishedCoverage(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            info,
            reader.IsDBNull(19) ? null : reader.GetString(19),
            ParseSharing(reader.GetString(20)),
            ParseStatus(reader.GetString(21)),
            reader.IsDBNull(22) ? null : reader.GetGuid(22),
            reader.GetFieldValue<DateTimeOffset>(23),
            reader.GetFieldValue<DateTimeOffset>(24),
            reader.GetFieldValue<Guid[]>(25))
        {
            // ADR-126: what its owner wrote about it, kept on its service row as a feature service's is.
            Description = reader.IsDBNull(26) ? null : reader.GetString(26),
            Tags = reader.IsDBNull(27) ? [] : reader.GetFieldValue<string[]>(27),
            ContentFolder = reader.IsDBNull(28) ? null : reader.GetGuid(28),
            Download = !reader.IsDBNull(29) && reader.GetBoolean(29),
            Images = reader.IsDBNull(30) ? null : ImagesFrom(reader.GetString(30), out _),
            NextImageId = reader.IsDBNull(30) ? null : NextFrom(reader.GetString(30)),
            Classes = reader.IsDBNull(31) ? null : ClassesFrom(reader.GetString(31)),
        };
    }

    /// <summary>Reads the status, refusing an unknown one.</summary>
    /// <remarks>
    /// The same rule <see cref="PostgresLayerCatalog"/> applies, for the same reason:
    /// a value outside the check constraint means the store was written by a version
    /// this one does not understand, and defaulting to started would serve a service
    /// somebody deliberately stopped.
    /// </remarks>
    private static ServiceStatus ParseStatus(string status) => status switch
    {
        "started" => ServiceStatus.Started,
        "stopped" => ServiceStatus.Stopped,
        _ => throw new InvalidOperationException(
            $"The service status '{status}' is not one this build knows. The platform store has "
            + "been written by a different version of the server."),
    };

    private static SharingScope ParseSharing(string sharing) => sharing switch
    {
        "private" => SharingScope.Private,
        "organization" => SharingScope.Organization,
        "public" => SharingScope.Public,
        "group" => SharingScope.Group,
        _ => throw new InvalidOperationException(
            $"The sharing scope '{sharing}' is not one this build knows. The platform store has "
            + "been written by a different version of the server. Refusing rather than "
            + "defaulting: one direction hides a store we do not understand and the other "
            + "publishes data."),
    };
}
