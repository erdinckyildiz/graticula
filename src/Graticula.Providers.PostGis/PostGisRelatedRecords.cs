using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>Related rows for one origin feature.</summary>
/// <param name="ObjectId">Which feature they belong to.</param>
/// <param name="Records">Its related rows, as attribute maps.</param>
public sealed record RelatedGroup(long ObjectId, IReadOnlyList<IReadOnlyDictionary<string, object?>> Records);

/// <summary>
/// Follows a declared relationship in one query.
/// </summary>
/// <remarks>
/// <para>
/// <b>One query for every requested object id, which ADR-013 §3 states as the
/// requirement.</b> The obvious implementation loops over the ids and issues a
/// query each; a client asking for the owners of two hundred parcels would then
/// make two hundred round trips. This joins once and groups the answer.
/// </para>
/// <para>
/// <b>Both tables may be in different databases, and then this cannot work.</b>
/// A join is a single statement in one database. Relating a hosted layer to a
/// registered one is a declaration this refuses at query time rather than at
/// declaration time — because a data source can be re-registered elsewhere after
/// the relationship was made, so the check has to be where the query is.
/// </para>
/// </remarks>
public sealed class PostGisRelatedRecords
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _origin;
    private readonly LayerDefinition _related;
    private readonly bool _sameDatabase;
    private int _queriesIssued;

    /// <summary>Creates the reader.</summary>
    /// <param name="dataSource">The pool for the origin layer's database.</param>
    /// <param name="origin">The layer the caller starts from.</param>
    /// <param name="related">The layer being reached.</param>
    /// <param name="sameDatabase">Whether both live in the same database.</param>
    public PostGisRelatedRecords(
        NpgsqlDataSource dataSource,
        LayerDefinition origin,
        LayerDefinition related,
        bool sameDatabase)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(related);

        _dataSource = dataSource;
        _origin = origin;
        _related = related;
        _sameDatabase = sameDatabase;
    }

    /// <summary>
    /// How many statements this reader has executed.
    /// </summary>
    /// <remarks>
    /// <b>Instrumentation with a job.</b> ADR-013 §3 requires one query for a
    /// whole batch rather than one per object id, and that requirement is
    /// invisible in the result — a loop returns identical records. Counting from
    /// the database is not an option either: <c>pg_stat_database</c> counts
    /// every connection, so a test reading it is measuring whatever else is
    /// running. This is the only place the number is unambiguous.
    /// </remarks>
    public int QueriesIssued => _queriesIssued;

    /// <summary>
    /// Reads the related rows for a set of origin features.
    /// </summary>
    /// <param name="originKey">The join column on the origin side.</param>
    /// <param name="relatedKey">The join column on the related side.</param>
    /// <param name="objectIds">Which origin features.</param>
    /// <param name="fields">The related layer's columns, as the whitelist.</param>
    /// <param name="limit">The most rows to return in total.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One group per origin feature that had any.</returns>
    public async Task<IReadOnlyList<RelatedGroup>> QueryAsync(
        string originKey,
        string relatedKey,
        IReadOnlyList<long> objectIds,
        IReadOnlyList<FieldDescription> fields,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        ArgumentNullException.ThrowIfNull(fields);

        if (!_sameDatabase)
        {
            throw new InvalidOperationException(
                $"'{_origin.Name}' and '{_related.Name}' are in different databases, so a "
                + "relationship between them cannot be followed in one query. Both layers must "
                + "read from the same data source.");
        }

        // Every identifier here is either ours or came from the layer's own
        // column list — checked when the relationship was declared and quoted
        // again now. An identifier cannot be a bound parameter, so the whitelist
        // is the safety (ADR-008 §4.6).
        System.Text.StringBuilder columns = new();

        foreach (FieldDescription field in fields)
        {
            columns.Append(", r.").Append(LayerDefinition.Quote(field.Name));
        }

        string sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             select o.{LayerDefinition.Quote(_origin.ObjectIdColumn ?? _origin.IdentityColumn)} as __oid{columns}
             from {Qualified(_origin)} o
             join {Qualified(_related)} r
               on r.{LayerDefinition.Quote(relatedKey)} = o.{LayerDefinition.Quote(originKey)}
             where o.{LayerDefinition.Quote(_origin.ObjectIdColumn ?? _origin.IdentityColumn)} = any(@ids)
             order by __oid
             limit {limit}
             """);

        Dictionary<long, List<IReadOnlyDictionary<string, object?>>> grouped = [];
        List<long> order = [];

        System.Threading.Interlocked.Increment(ref _queriesIssued);

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = objectIds,
        });

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long oid = reader.GetInt64(0);

            if (!grouped.TryGetValue(oid, out List<IReadOnlyDictionary<string, object?>>? rows))
            {
                rows = [];
                grouped[oid] = rows;
                order.Add(oid);
            }

            Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

            for (int i = 0; i < fields.Count; i++)
            {
                attributes[fields[i].Name] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
            }

            rows.Add(attributes);
        }

        return [.. order.Select(oid => new RelatedGroup(oid, grouped[oid]))];
    }

    private static string Qualified(LayerDefinition layer) =>
        $"{LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName)}";
}
