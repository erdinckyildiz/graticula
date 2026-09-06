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

        -- The declared time column (Q-129, migration 35), on the end per the rule
        -- above and read by name below like the three before it.
        l.time_field,

        -- <b>Which groups this service is shared with — ADR-036.</b> Aggregated rather than joined,
        -- for the reason the caller's own groups are: a join would multiply every layer row by every
        -- group and make the reader deduplicate. Empty for all but a `group`-scoped service, and the
        -- read path does not consult it otherwise.
        (select coalesce(array_agg(gi.group_id), '{}')
           from sharing_group_item gi where gi.service_id = s.id) as shared_with_groups,

        -- The reference this service is served in, or null for each layer's own
        -- (ADR-057 §5c, migration 39). On the end, per the rule above.
        s.srid as service_srid,
        s.srid_wkt as service_srid_wkt
        """;

    /// <summary>The joins a layer read needs: a layer, its source, its service.</summary>
    private const string From =
        "from layer l "
        + "join data_source d on d.id = l.data_source_id "
        + "join service s on s.id = l.service_id";

    /// <remarks>
    /// <b>Left, and it matters the moment somebody creates a service before
    /// adding layers to it.</b> An inner join makes that service invisible — the
    /// administrator who just created it sees nothing in the catalogue and
    /// reasonably concludes the creation failed.
    /// </remarks>
    private const string ServiceFrom =
        "from service s "
        + "left join layer l on l.service_id = s.id "
        + "left join data_source d on d.id = l.data_source_id "

        ;

    /// <summary>
    /// The predicate that keeps an image service out of a face that has layers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An image service is not a feature service with nothing in it.</b> The left join
    /// above exists so a service an administrator has just created appears before its first
    /// layer does — which is right, and it also let every ImageServer through as a layerless
    /// FeatureServer the day coverages arrived. The directory listed one, a client followed it
    /// to <c>/FeatureServer/0</c>, and got a 404 that reads as a broken service rather than as
    /// one of another kind.
    /// </para>
    /// <para>
    /// <b>It lived in the join until 2026-09-05, and the join has two callers asking different
    /// questions.</b> <c>FindServiceAsync</c> resolves an address for the faces that serve
    /// layers, and wants this. <c>ListServicesAsync</c> is what <i>My content</i> enumerates,
    /// and did not — so a coverage somebody published was absent from their own content, with
    /// no error and nothing on the screen to ask about. Measured on the owner's own server the
    /// day it was found: <b>three image services, none of them listed</b>.
    /// </para>
    /// <para>
    /// <b>So the filter moved to the question rather than being duplicated at it.</b>
    /// <c>CatalogFallback</c> keeps one remembered listing on the stated grounds that
    /// <c>ListServicesAsync</c> takes no arguments — <i>every face that enumerates asks for the
    /// same thing and then filters it by who is asking</i> — and a parameter here would have
    /// given that one slot two answers. Kind is now filtered the way sharing already is: in the
    /// face, against a listing that holds everything.
    /// </para>
    /// </remarks>
    private const string NotImagery = "where s.kind is distinct from 'ImageServer'";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>The sources this process has failed to open, by name.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _unopenableSources = new(StringComparer.Ordinal);
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
    /// <summary>Whether anything published on this server is readable by somebody else.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// True when there is nothing published at all, or when at least one layer's service is
    /// shared beyond its owner. False only when there are layers and every one is private.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Counted rather than listed, and that is the whole point —
    /// [D-152](../../docs/architecture-debt.md).</b> Startup asked this by calling
    /// <see cref="ListAsync"/>, which maps every layer, which decrypts every registered data
    /// source's connection string. So a store holding one credential sealed with a key this
    /// process does not have made <c>SecretProtectionException</c> escape <c>Main</c>: no
    /// listener, no refusal document, a stack trace and exit — **for a check whose entire
    /// purpose is deciding whether one sentence is worth printing**.
    /// </para>
    /// <para>
    /// <b>The failure was total and the cause was partial.</b> Layers on the other sources were
    /// fine and public layers need no credential at all; the server would have served every one
    /// of them. It also put the repair out of reach, because the admin API that fixes a
    /// credential lives inside the server that would not start.
    /// </para>
    /// <para>
    /// <b>Two counts rather than one query per layer.</b> Sharing has been the service's column
    /// since migration 11, so the question is answerable from <c>service</c> alone once the
    /// layer count is known — and neither count touches <c>data_source</c>, which is where the
    /// credential is.
    /// </para>
    /// </remarks>
    public async Task<bool> AnythingSharedAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select count(*), count(*) filter (where s.sharing <> 'private') "
            + "from layer l join service s on s.id = l.service_id");

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        long layers = reader.GetInt64(0);
        long shared = reader.GetInt64(1);

        return layers == 0 || shared > 0;
    }

    /// <summary>
    /// The same joins driven from the service, so an empty one is still a service.
    /// </summary>
    public async Task<IReadOnlyList<PublishedLayer>> ListAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<PublishedLayer> layers, _) =
            await ListWhatCanBeReadAsync(cancellationToken).ConfigureAwait(false);

        return layers;
    }

    /// <summary>Every layer whose credential this process can open, and how many it could not.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The readable layers, and the names of the data sources that could not be opened.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-154](../../docs/architecture-debt.md): one unopenable credential took the whole
    /// directory with it.</b> Listing decrypts every layer's connection string on its way to a
    /// <see cref="PublishedLayer"/>, so a single source sealed with a key this process does not
    /// hold made <c>/rest/services</c> answer 503 — while every layer on every other source was
    /// servable and the directory that would let a client find them was the thing that refused.
    /// A key rotation, a restored backup, or a source registered by another install all produce
    /// exactly that state.
    /// </para>
    /// <para>
    /// <b>Omitted rather than tolerated, and counted rather than skipped quietly.</b> A layer
    /// whose credential cannot be opened cannot be served, so listing it would advertise
    /// something that answers 503 — the <em>capability report</em> problem in miniature. But an
    /// omission nobody can see is how a directory comes to disagree with the catalogue, so the
    /// sources are named and returned, and the caller decides what to do with that: the log
    /// says it, and an operator reading <c>/admin/health</c> can be told, while an anonymous
    /// client is told nothing it could not already infer from the layer being absent.
    /// </para>
    /// <para>
    /// <b>Only this failure is caught.</b> Anything else is a bug and propagates, which is the
    /// same rule <see href="../../docs/adr/ADR-026-serving-through-a-platform-store-outage.md">
    /// ADR-026</see> applies to its own blind path.
    /// </para>
    /// </remarks>
    public async Task<(IReadOnlyList<PublishedLayer> Layers, IReadOnlyList<string> Unopenable)>
        ListWhatCanBeReadAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} order by s.name, l.layer_index");

        List<PublishedLayer> layers = [];
        SortedSet<string> unopenable = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                layers.Add(Map(reader));
            }
            catch (SecretProtectionException)
            {
                // <b>Column 10 is `d.name` and it is not sealed</b>, so a listing can still
                // say which registration needs attention without opening anything. Counted
                // once per source rather than once per layer: an operator fixes a credential,
                // not a hundred layers.
                unopenable.Add(reader.GetString(10));
                _unopenableSources.TryAdd(reader.GetString(10), 0);
            }
        }

        return (layers, [.. unopenable]);
    }

    /// <summary>Every layer published under this name.</summary>
    /// <param name="name">The bare layer name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>All of them, in the order <see cref="FindAsync"/> would have chosen from.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-109](../../../docs/architecture-debt.md): a bare layer name is not unique, and
    /// nothing said so.</b> <see cref="FindAsync"/> answers `order by s.name limit 1`, so two
    /// layers of one name in different services resolve to whichever service sorts first —
    /// silently. The `limit 1` is not the defect; it stops a duplicate throwing. The silence is.
    /// </para>
    /// <para>
    /// <b>So a caller that is about to act on a named layer asks this instead.</b> One row is
    /// the ordinary case and costs the same query; more than one is a question for the operator,
    /// and the answer they need is *which service*, which is why the whole row comes back rather
    /// than a count.
    /// </para>
    /// <para>
    /// <b>One archive publishes fifty-five layers under names their owner chose</b>, and an Esri
    /// estate has `Segment_Boundary` in three of them. Before the geodatabase import a collision
    /// needed two deliberate publishes; now it needs one upload.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PublishedLayer>> NamedAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {From} where l.name = @name order by s.name");
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<PublishedLayer> found = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Map(reader));
        }

        return found;
    }

    /// <summary>One layer by published name, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <b>Ambiguous by construction, and <see cref="NamedAsync"/> is what a writer should use.</b>
    /// [D-109](../../../docs/architecture-debt.md): the name is not unique and this takes the
    /// first. That is the right answer for a read that only needs *a* layer of this name — a
    /// cache eviction, a shape lookup — and the wrong one for anything that changes something.
    /// </remarks>
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
            integerIdentityColumn: reader.IsDBNull(7) ? null : reader.GetString(7),
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
            reader.GetFieldValue<Guid[]>(reader.GetOrdinal("shared_with_groups")),

            // Q-129, by name for the reason `symbology` is read by name.
            Nullable(reader, "time_field"),

            // <b>The service's capability ceiling, beside its cost ceilings and for the same
            // reason — D-179.</b> The cost half was carried onto the layer and this half was
            // not, so every reader holding only a layer had no ceiling to apply: the layer
            // document advertised the full privileged set and the write path enforced nothing,
            // while the service document — the one place that resolves a service — got it
            // right. One row, two facts, and only one of them travelled.
            reader.IsDBNull(reader.GetOrdinal("capability_ceiling"))
                ? null
                : reader.GetFieldValue<string[]>(reader.GetOrdinal("capability_ceiling")),

            // <b>The service's reference, travelling for the same reason the ceiling above
            // does.</b> The query handler resolves a layer and never the service over it, so a
            // service that names its own reference would be ignored by the one path that
            // decides what a query answers in — the D-179 shape a second time, on the column
            // that decides which coordinates a client gets.
            reader.IsDBNull(reader.GetOrdinal("service_srid"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("service_srid")))
        {
            // <b>The other half of the same column, and it travels or it does not exist.</b>
            // A service may name its reference by writing it out (migration 41), and a layer
            // that carried only the code would answer in its own reference while the service
            // document said otherwise — which is what D-179 was.
            ServedWkt = reader.IsDBNull(reader.GetOrdinal("service_srid_wkt"))
                ? null
                : reader.GetString(reader.GetOrdinal("service_srid_wkt")),
        };
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
    /// <summary>Data sources whose credential this process could not open while listing.</summary>
    /// <remarks>
    /// <b>[D-154](../../docs/architecture-debt.md).</b> Written by the listing rather than
    /// returned by it, because the listing's callers are a fallback and a directory and neither
    /// wants a second return value — and because an operator fixes a credential rather than a
    /// hundred layers, so the interesting thing is the set of source names and not a count of
    /// what each one hid. It accumulates rather than resetting: a source that failed once is
    /// worth naming until somebody looks, and the set is small by construction.
    /// </remarks>
    public IReadOnlyCollection<string> UnopenableSources => [.. _unopenableSources.Keys];


    /// <summary>Every service, with its layers.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services, ordered by name.</returns>
    /// <remarks>
    /// <b>One query, then grouped in memory.</b> A query per service would be
    /// the N+1 the catalogue endpoint cannot afford — it runs on every
    /// <c>/rest/services</c> — and the join returns one row per layer, which at
    /// the 100–1,000 services this product targets is a few thousand rows.
    /// <para>
    /// <b>Faces that serve layers only.</b> <see cref="NotImagery"/> keeps image services out,
    /// for the reason written there; <see cref="ListEveryKindAsync"/> is the door for the
    /// caller that wants them.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PublishedService>> ListServicesAsync(
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} {ServiceFrom} {NotImagery} order by s.name, l.layer_index");

        return await ReadServicesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every service, including the kinds that serve no layers.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services, ordered by name.</returns>
    /// <remarks>
    /// <para>
    /// <b>A second door because there is a second question, and only one caller asks it.</b>
    /// <see cref="ListServicesAsync"/> answers *what can this face serve*, and every face that
    /// serves layers is right to be handed a listing with no image services in it — that filter
    /// is <see cref="NotImagery"/> and the 404 it prevents is written there. *My content* asks
    /// something else: *what does this person own*. A coverage is content, and until 2026-09-05
    /// it was absent from the answer with no error and nothing on screen to ask about. Measured
    /// on the owner's own server the day it was found: <b>three image services, none listed</b>.
    /// </para>
    /// <para>
    /// <b>Not a parameter on the other one, deliberately.</b>
    /// <c>CatalogFallback</c> remembers a single listing on the stated grounds that
    /// <see cref="ListServicesAsync"/> takes no arguments — *every face that enumerates asks for
    /// the same thing and then filters it by who is asking* — so a flag there would give one
    /// cache slot two answers. This method has no cache in front of it and no face behind it:
    /// the two content endpoints call the catalogue directly.
    /// </para>
    /// <para>
    /// <b>An image service comes back with no layers, because it has none.</b> A caller that
    /// counts them must not read that as *empty*; a coverage has a raster in it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PublishedService>> ListEveryKindAsync(
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
            // `NotImagery` opens the clause, so these are `and`. This resolver answers for
            // the faces that serve layers, which is the caller the filter was written for.
            + NotImagery
            + "  and coalesce(lower(s.folder), '') = coalesce(lower(@folder), '') "
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

        // <b>Services that had layers and lost all of them — D-154, the same day.</b> Omitting
        // an unreadable layer leaves its service behind with nothing in it, and the first
        // version of this listed those: two empty `FeatureServer` documents with `layers: []`
        // and names that lead nowhere, which is exactly the *advertise something that cannot be
        // served* the omission was meant to prevent. **An empty service is a real state** — one
        // is created empty and filled later — so the distinction is not emptiness but how it
        // arrived: this set names the services whose layers existed and could not be opened.
        HashSet<Guid> hidden = [];
        Dictionary<Guid, (string Name, string? Folder, string Kind, string? Description,
            Guid? Owner, SharingScope Sharing, ServiceStatus Status, string? Style,
            ServiceCapabilityLimits Limits, Guid[] SharedWith, int? Srid,
            string? SridWkt)> heads = [];
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
                            reader.GetOrdinal("shared_with_groups")),

                        // <b>By name, like the two above it.</b> Ordinals past the
                        // capability block are a trap the comment on `Columns` names, and
                        // this one is on the end for the same reason.
                        reader.IsDBNull(reader.GetOrdinal("service_srid"))
                            ? null
                            : reader.GetInt32(reader.GetOrdinal("service_srid")),

                        // <b>The other half of the same choice.</b> A service names its
                        // reference by code or by writing it out (migration 41), and a head
                        // that carried only the code would describe a service whose layers
                        // answer in something else — D-179 again, on the column that decides
                        // which coordinates a client gets.
                        reader.IsDBNull(reader.GetOrdinal("service_srid_wkt"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("service_srid_wkt")));
                }

                // A left join, so a service with no layers arrives as one row of
                // nulls. That is a service, not a broken row.
                if (!reader.IsDBNull(0))
                {
                    // <b>A layer whose credential cannot be opened is left out of the
                    // service rather than taking the whole listing down — D-154.</b> One
                    // source sealed with a key this process does not hold made
                    // `/rest/services` answer 503 while every layer on every other source was
                    // servable. It cannot be served, so listing it would advertise something
                    // that answers 503; it is omitted, and the source is named in
                    // `UnopenableSources` so an operator learns which registration needs
                    // attention. Only this failure is caught — anything else is a bug and
                    // propagates, the same rule ADR-026 applies to its own blind path.
                    try
                    {
                        layers.Add(Map(reader));
                    }
                    catch (SecretProtectionException)
                    {
                        _unopenableSources.TryAdd(reader.GetString(10), 0);
                        hidden.Add(owning);
                    }
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
            // <b>A service emptied by omission is not listed — D-154.</b> Its layers exist and
            // this process cannot open their credential, so it can serve nothing; listing it
            // gives a client a `FeatureServer` document with `layers: []` and a name that leads
            // nowhere, which is the thing omitting the layers was meant to prevent. **A service
            // that is empty because nobody has published into it yet is untouched** — it never
            // had a layer to lose, so it is not in `hidden`.
            if (hidden.Contains(id) && byService[id].Count == 0)
            {
                continue;
            }

            var head = heads[id];

            services.Add(new PublishedService(
                id, head.Name, head.Folder, head.Kind, head.Description,
                head.Owner, head.Sharing, head.Status, byService[id],
                groups.TryGetValue(id, out List<GroupLayer>? mine) ? mine : [],
                head.Style,
                head.Limits,
                head.SharedWith,
                head.Srid));
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
