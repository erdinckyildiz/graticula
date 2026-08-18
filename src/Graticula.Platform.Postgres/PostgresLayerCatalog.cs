using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>Reads published layers from the platform store.</summary>
public sealed class PostgresLayerCatalog
{
    private const string Columns = """
        l.id, l.name, l.schema_name, l.table_name, l.geometry_column, l.srid,
        l.identity_column, l.object_id_column,

        -- Derived, never read from l.is_hosted. That column was written false by
        -- every insert since version 1, so anything trusting it concluded that
        -- nothing in the world was hosted — which silently disabled every vector
        -- tile service (Q-67). Hosted means the data lives in the datastore, and
        -- the datastore is a registered source flagged as such, so the fact has
        -- exactly one home and cannot drift from the other one.
        d.is_datastore, l.geometry_type,
        d.name, d.connection_secret, d.key_version,

        -- <b>From the service, never from the layer.</b> The layer's own
        -- sharing, status and owner columns survive migration 11 and are read
        -- by nothing: a service holding three layers with three scopes cannot
        -- answer "who may see this service", so the question is asked of the
        -- container. Reading l.sharing here would be the is_hosted mistake a
        -- second time, on the column that decides who sees what.
        s.owner_principal_id, s.sharing, s.status, l.attachment_quota_bytes,
        s.id, l.layer_index, s.name, s.folder, s.kind, s.description,
        l.parent_layer_index, l.cache_seconds,

        -- The stored style, or null for the generated one (ADR-028).
        s.style,

        -- The configured capability ceiling (ADR-031). All four are null on a
        -- service nobody has configured, which is every service that existed
        -- before migration 16 — so reading them changes no document.
        s.serves_features, s.serves_tiles, s.capability_ceiling, s.statement_timeout_ms,

        -- What one request may cost this service (Q-113, migration 17). Null
        -- throughout on a service nobody has configured, which is every service
        -- that existed before it.
        s.max_record_count, s.default_record_count, s.max_response_bytes,
        s.max_request_bytes, s.max_edits_per_transaction,

        -- <b>Appended, and appended deliberately.</b> Every reader below takes its
        -- columns by ordinal, so inserting one in the middle would silently shift
        -- every field after it — the shape of defect that reads a sharing scope as a
        -- status. New columns go on the end (ADR-033, migration 23).
        l.symbology,

        -- On the end, per the rule three lines above, which the first version of this
        -- column broke by sitting beside the other cost ceilings it belongs with. It is
        -- read by name and so was `symbology`, so nothing shifted — but a rule that is
        -- followed only when it happens to be harmless is not being followed.
        s.request_deadline_seconds,

        -- <b>Which groups this service is shared with — ADR-036.</b> Aggregated rather than joined,
        -- for the reason the caller's own groups are: a join would multiply every layer row by every
        -- group and make the reader deduplicate. Empty for all but a `group`-scoped service, and the
        -- read path does not consult it otherwise.
        (select coalesce(array_agg(gi.group_id), '{}')
           from sharing_group_item gi where gi.service_id = s.id) as shared_with_groups
        """;

    /// <summary>The joins a layer read needs: a layer, its source, its service.</summary>
    private const string From =
        "from layer l "
        + "join data_source d on d.id = l.data_source_id "
        + "join service s on s.id = l.service_id";

    /// <summary>
    /// The same joins driven from the service, so an empty one is still a service.
    /// </summary>
    /// <remarks>
    /// <b>Left, and it matters the moment somebody creates a service before
    /// adding layers to it.</b> An inner join makes that service invisible — the
    /// administrator who just created it sees nothing in the catalogue and
    /// reasonably concludes the creation failed.
    /// </remarks>
    private const string ServiceFrom =
        "from service s "
        + "left join layer l on l.service_id = s.id "
        + "left join data_source d on d.id = l.data_source_id";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SecretProtector _secrets;

    /// <summary>Creates a catalogue reader.</summary>
    public PostgresLayerCatalog(NpgsqlDataSource dataSource, SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(secrets);

        _dataSource = dataSource;
        _secrets = secrets;
    }

    /// <summary>Every published layer.</summary>
    public async Task<IReadOnlyList<PublishedLayer>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} order by s.name, l.layer_index");

        List<PublishedLayer> layers = [];
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            layers.Add(Map(reader));
        }

        return layers;
    }

    /// <summary>One layer by published name, or <see langword="null"/>.</summary>
    public async Task<PublishedLayer?> FindAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} where l.name = @name order by s.name limit 1");
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>One layer by its catalogue id, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The layer, or null.</returns>
    /// <remarks>
    /// Relationships name layers by id rather than by name, because a service
    /// can be renamed and a declaration that pointed at a name would then point
    /// at nothing — or worse, at whatever took the name next.
    /// </remarks>
    public async Task<PublishedLayer?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} where l.id = @id");

        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>A nullable text column by name, for the columns added since.</summary>
    private static string? Nullable(NpgsqlDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private PublishedLayer Map(NpgsqlDataReader reader)
    {
        string layerName = reader.GetString(1);

        LayerDefinition definition = new(
            name: layerName,
            schemaName: reader.GetString(2),
            tableName: reader.GetString(3),
            geometryColumn: reader.GetString(4),
            srid: reader.GetInt32(5),
            identityColumn: reader.GetString(6),
            objectIdColumn: reader.IsDBNull(7) ? null : reader.GetString(7),
            isHosted: reader.GetBoolean(8));

        if (!Enum.TryParse(reader.GetString(9), out GeometryKind geometryType))
        {
            // The column has a check constraint, so this means the constraint
            // and this enum have drifted apart — a schema change that forgot
            // its code.
            throw new InvalidOperationException(
                $"Layer '{layerName}' declares geometry type '{reader.GetString(9)}', which this "
                + "build does not know. The schema's check constraint and GeometryKind have "
                + "diverged.");
        }

        // Decrypted here rather than carried around sealed: the connection
        // string exists to be used, and passing a ciphertext plus a protector to
        // whoever needs it just spreads the key further.
        string connectionString = _secrets.Unprotect(
            reader.GetFieldValue<byte[]>(11), reader.GetInt32(12));

        return new PublishedLayer(
            reader.GetGuid(0),
            definition,
            reader.GetString(10),
            connectionString,
            geometryType,
            reader.IsDBNull(13) ? null : reader.GetGuid(13),
            ParseSharing(reader.GetString(14)),
            ParseStatus(reader.GetString(15)),
            reader.GetInt64(16),
            reader.GetGuid(17),
            reader.GetInt32(18),
            reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(23) ? null : reader.GetInt32(23),
            reader.IsDBNull(24) ? null : TimeSpan.FromSeconds(reader.GetInt32(24)),

            // The service's cost ceilings, carried on the layer for the reason
            // PublishedLayer.Cost documents: the query path resolves a layer and
            // never the service, and this read already joined it.
            ReadCost(reader),

            // <b>By name, not by ordinal, and that is a correction rather than a
            // style preference.</b> Every read above counts columns by hand, which
            // works because those ordinals were written with the query and have not
            // moved. This one was hand-counted twice and got 34 and 35 — the wrong
            // one first, which served a stored symbology as *generated* on the public
            // document while the admin endpoint derived it correctly, so the two
            // faces disagreed and neither looked broken. Npgsql caches the lookup, so
            // the cost is a dictionary hit and the benefit is that adding a column
            // cannot silently shift this one.
            Nullable(reader, "symbology"),

            // By name, like the two columns above it, and for the same reason.
            reader.IsDBNull(reader.GetOrdinal("statement_timeout_ms"))
                ? null
                : TimeSpan.FromMilliseconds(
                    reader.GetInt32(reader.GetOrdinal("statement_timeout_ms"))),

            // ADR-036: the owning service's group shares, so the read path decides in one place.
            reader.GetFieldValue<Guid[]>(reader.GetOrdinal("shared_with_groups")));
    }

    /// <summary>The group layers held by the services named.</summary>
    /// <remarks>
    /// <para>
    /// <b>A second query rather than a third join.</b> Group layers and feature
    /// layers are different tables with no relationship to each other beyond
    /// their service, so joining both would multiply the rows: three layers and
    /// two groups would come back as six rows and the layers would be read
    /// twice. Two queries and a dictionary is the honest shape.
    /// </para>
    /// <para>
    /// <b>Filtered by the services the caller actually found, and it was not.</b>
    /// <c>FindServiceAsync</c> passed null, which meant no <c>where</c> clause at
    /// all — so resolving one service for one feature request read <em>every</em>
    /// group layer in the catalogue and threw all but one away. Correct, and
    /// O(all services) on the most-used path in the product against a stated
    /// scale target of 100 to 1,000 services. Found by instrumenting the query
    /// path for D-30: the catalogue read was 1.8 ms where the data query beside
    /// it was 0.7 ms, and this was one of the reasons.
    /// </para>
    /// <para>
    /// <b>No services means no query.</b> A lookup that found nothing used to
    /// run this anyway, so a 404 cost two round trips to establish that the
    /// second one had nothing to say.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, List<GroupLayer>>> GroupsAsync(
        List<Guid> serviceIds, CancellationToken cancellationToken)
    {
        Dictionary<Guid, List<GroupLayer>> groups = [];

        if (serviceIds.Count == 0)
        {
            return groups;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select id, service_id, layer_index, name, parent_layer_index from group_layer "
            + "where service_id = any(@services) order by layer_index");

        // <b>An array parameter rather than a built-up IN list.</b> One
        // statement shape whatever the count, so the plan cache is not defeated
        // by the number of services a caller happens to be reading.
        command.Parameters.AddWithValue("services", serviceIds);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid service = reader.GetGuid(1);

            if (!groups.TryGetValue(service, out List<GroupLayer>? list))
            {
                list = [];
                groups[service] = list;
            }

            list.Add(new GroupLayer(
                reader.GetGuid(0),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        return groups;
    }

    /// <summary>
    /// The folder register's names, for the directory's folder list.
    /// </summary>
    /// <remarks>
    /// <b>On the serving catalogue rather than the admin one, and it is the smallest read in
    /// this class.</b> The root directory is a public surface answered on every browse, so it
    /// takes names and nothing else — no counts, no ownership, no join. The admin API's
    /// richer listing is a different question asked by a different caller.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Folder names, in the case they were created with.</returns>
    public async Task<IReadOnlyList<string>> ListFolderNamesAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("select name from folder order by lower(name)");

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<string> names = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Every service, with its layers.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services, ordered by name.</returns>
    /// <remarks>
    /// <b>One query, then grouped in memory.</b> A query per service would be
    /// the N+1 the catalogue endpoint cannot afford — it runs on every
    /// <c>/rest/services</c> — and the join returns one row per layer, which at
    /// the 100–1,000 services this product targets is a few thousand rows.
    /// </remarks>
    public async Task<IReadOnlyList<PublishedService>> ListServicesAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {ServiceFrom} order by s.name, l.layer_index");

        return await ReadServicesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One service by folder and name, or null.</summary>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The service, or null.</returns>
    /// <remarks>
    /// <b>Matched case-insensitively</b>, because ArcGIS writes its own folder
    /// as <c>Hosted</c> and a client copying that convention must not meet a 404
    /// over a capital letter.
    /// </remarks>
    public async Task<PublishedService?> FindServiceAsync(
        string? folder, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {ServiceFrom} "
            // <b>Written to match the index, expression for expression.</b>
            // Migration 15's unique index is on
            // (coalesce(lower(folder), ''), lower(name)); a predicate that says
            // the same thing in different words cannot use it, and this one used
            // to — the folder half was coalesce-then-lower against an index that
            // was lower-then-coalesce, so the whole index was unusable and the
            // lookup scanned every service on every request.
            + "where coalesce(lower(s.folder), '') = coalesce(lower(@folder), '') "
            + "  and lower(s.name) = lower(@name) "
            + "order by l.layer_index");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue(
            "folder", (object?)folder ?? DBNull.Value);

        IReadOnlyList<PublishedService> services =
            await ReadServicesAsync(command, cancellationToken).ConfigureAwait(false);

        return services.Count == 0 ? null : services[0];
    }

    private async Task<IReadOnlyList<PublishedService>> ReadServicesAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        Dictionary<Guid, List<PublishedLayer>> byService = [];
        Dictionary<Guid, (string Name, string? Folder, string Kind, string? Description,
            Guid? Owner, SharingScope Sharing, ServiceStatus Status, string? Style,
            ServiceCapabilityLimits Limits, Guid[] SharedWith)> heads = [];
        List<Guid> order = [];

        // <b>Its own scope, so the reader is closed before the group query
        // runs.</b> Disposing it by hand and letting `await using` dispose it
        // again put the connector in a state Npgsql reports as "Received
        // backend message BindComplete while expecting ReadyForQueryMessage.
        // Please file a bug" — which is a real bug, in this file, and the
        // message sends you looking in the wrong place.
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid owning = reader.GetGuid(17);

                if (!byService.TryGetValue(owning, out List<PublishedLayer>? layers))
                {
                    layers = [];
                    byService[owning] = layers;
                    order.Add(owning);

                    heads[owning] = (
                        reader.GetString(19),
                        reader.IsDBNull(20) ? null : reader.GetString(20),
                        reader.GetString(21),
                        reader.IsDBNull(22) ? null : reader.GetString(22),
                        reader.IsDBNull(13) ? null : reader.GetGuid(13),
                        ParseSharing(reader.GetString(14)),
                        ParseStatus(reader.GetString(15)),
                        reader.IsDBNull(25) ? null : reader.GetString(25),
                        ReadLimits(reader),

                        // <b>By name, like the two columns before it.</b> Appended after every
                        // ordinal above was written, and hand-counting the last index is what
                        // served a stored symbology as *generated* (ADR-033 §5i).
                        reader.GetFieldValue<Guid[]>(
                            reader.GetOrdinal("shared_with_groups")));
                }

                // A left join, so a service with no layers arrives as one row of
                // nulls. That is a service, not a broken row.
                if (!reader.IsDBNull(0))
                {
                    layers.Add(Map(reader));
                }
            }
        }

        // The services the first query actually returned, which is what the
        // group query needs to be about.
        Dictionary<Guid, List<GroupLayer>> groups =
            await GroupsAsync(order, cancellationToken).ConfigureAwait(false);

        List<PublishedService> services = [];

        foreach (Guid id in order)
        {
            var head = heads[id];

            services.Add(new PublishedService(
                id, head.Name, head.Folder, head.Kind, head.Description,
                head.Owner, head.Sharing, head.Status, byService[id],
                groups.TryGetValue(id, out List<GroupLayer>? mine) ? mine : [],
                head.Style,
                head.Limits,
                head.SharedWith));
        }

        return services;
    }

    /// <summary>Reads a service's configured capability ceiling (ADR-031).</summary>
    /// <remarks>
    /// <b>All four columns null is the ordinary case and must stay cheap.</b> Every
    /// service that existed before migration 16 reads this way, and
    /// <see cref="ServiceCapabilityLimits.Unset"/> is a singleton, so the common path
    /// allocates nothing.
    /// </remarks>
    private static ServiceCapabilityLimits ReadLimits(NpgsqlDataReader reader)
    {
        bool? features = reader.IsDBNull(26) ? null : reader.GetBoolean(26);
        bool? tiles = reader.IsDBNull(27) ? null : reader.GetBoolean(27);
        string[]? ceiling = reader.IsDBNull(28) ? null : reader.GetFieldValue<string[]>(28);
        int? timeout = reader.IsDBNull(29) ? null : reader.GetInt32(29);

        ServiceCostCeilings cost = ReadCost(reader);

        // <b>Cost is read before the shortcut, and getting that order wrong was a
        // real bug for the length of one edit.</b> The two axes are independent: a
        // service may bound what a request costs without configuring any capability,
        // so returning `Unset` on the capability columns alone would silently discard
        // every cost ceiling on that service.
        if (features is null && tiles is null && ceiling is null && timeout is null)
        {
            return cost.IsUnset
                ? ServiceCapabilityLimits.Unset
                : ServiceCapabilityLimits.Unset.With(cost);
        }

        return new ServiceCapabilityLimits(
            features,
            tiles,
            ceiling,
            timeout is { } ms ? TimeSpan.FromMilliseconds(ms) : null)
            .With(cost);
    }

    /// <summary>Reads a service's cost ceilings (Q-113, migration 17).</summary>
    /// <remarks>
    /// Read unconditionally rather than behind the same all-null shortcut as the
    /// capability set, because a service may configure cost and not capability — the
    /// two are separate axes and the shortcut above already returned for the case
    /// where neither is set.
    /// </remarks>
    private static ServiceCostCeilings ReadCost(NpgsqlDataReader reader)
    {
        int? maxRows = reader.IsDBNull(30) ? null : reader.GetInt32(30);
        int? defaultRows = reader.IsDBNull(31) ? null : reader.GetInt32(31);
        long? responseBytes = reader.IsDBNull(32) ? null : reader.GetInt64(32);
        long? requestBytes = reader.IsDBNull(33) ? null : reader.GetInt64(33);
        int? edits = reader.IsDBNull(34) ? null : reader.GetInt32(34);

        // <b>By name, for the reason `symbology` is read by name.</b> Every ordinal above was
        // written with the query and has not moved; this one was appended after them, and
        // hand-counting the last index is what served a stored symbology as *generated* while the
        // admin endpoint reported it correctly (ADR-033 §5i).
        int deadlineOrdinal = reader.GetOrdinal("request_deadline_seconds");

        TimeSpan? deadline = reader.IsDBNull(deadlineOrdinal)
            ? null
            : TimeSpan.FromSeconds(reader.GetInt32(deadlineOrdinal));

        return maxRows is null && defaultRows is null && responseBytes is null
            && requestBytes is null && edits is null && deadline is null
            ? ServiceCostCeilings.Unset
            : new ServiceCostCeilings(
                maxRows, defaultRows, responseBytes, requestBytes, edits, deadline);
    }

    /// <summary>Reads the sharing scope, refusing an unknown one.</summary>
    /// <remarks>
    /// <b>Throws rather than defaulting.</b> The column has a check constraint
    /// listing exactly three values, so an unknown one means the store was
    /// written by a different version. Defaulting to <c>Private</c> would be the
    /// safe direction for that row and would hide a store we do not understand;
    /// defaulting to anything else would publish data. Refusing is the only
    /// option that is neither silent nor dangerous.
    /// </remarks>
    /// <summary>Reads the status, refusing an unknown one.</summary>
    /// <remarks>
    /// Throws for the same reason <see cref="ParseSharing"/> does: the column
    /// has a check constraint listing exactly two values, so a third means the
    /// store was written by a version this one does not understand. Defaulting
    /// to started would serve a service somebody deliberately stopped.
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
            + "been written by a different version of the server."),
    };
}
