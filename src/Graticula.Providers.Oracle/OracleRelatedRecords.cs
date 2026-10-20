using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>
/// Follows a declared relationship between two layers of one Oracle database — ADR-205 — in one query, as
/// <c>PostGisRelatedRecords</c> does in PostgreSQL.
/// </summary>
/// <remarks>
/// Every identifier is the layer's own, checked when the relationship was declared and quoted again here (ADR-008
/// §4.6); the object ids are bound as one JSON array, as the reader binds its refined ids.
/// </remarks>
public sealed class OracleRelatedRecords : IRelatedRecords
{
    private readonly string _connectionString;
    private readonly LayerDefinition _origin;
    private readonly LayerDefinition _related;
    private readonly bool _sameDatabase;

    /// <summary>Creates the reader.</summary>
    /// <param name="locator">The origin layer's <c>oracle:</c> locator.</param>
    /// <param name="origin">The layer the caller starts from.</param>
    /// <param name="related">The layer being reached.</param>
    /// <param name="sameDatabase">Whether both are in the same database.</param>
    public OracleRelatedRecords(string locator, LayerDefinition origin, LayerDefinition related, bool sameDatabase)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(related);

        _connectionString = OracleConnections.For(locator, pooling: true);
        _origin = origin;
        _related = related;
        _sameDatabase = sameDatabase;
    }

    /// <inheritdoc/>
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
                $"'{_origin.Name}' and '{_related.Name}' are in different databases, so a relationship between them "
                + "cannot be followed in one query. Both layers must read from the same data source.");
        }

        string id = LayerDefinition.Quote(_origin.IntegerIdentityColumn ?? _origin.IdentityColumn);
        StringBuilder columns = new();

        foreach (FieldDescription field in fields)
        {
            columns.Append(", r.").Append(LayerDefinition.Quote(field.Name));
        }

        string sql =
            $"select o.{id} as oid{columns} from {_origin.QuotedTable} o "
            + $"join {_related.QuotedTable} r on r.{LayerDefinition.Quote(relatedKey)} = o.{LayerDefinition.Quote(originKey)} "
            + $"where o.{id} in (select j.v from json_table(:ids, '$[*]' columns (v number path '$')) j) "
            + "order by 1 fetch first :limit rows only";

        await using OracleConnection connection =
            await OracleConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await using OracleCommand command = new(sql, connection) { BindByName = true };

        command.Parameters.Add("ids", OracleDbType.Clob, JsonSerializer.Serialize(objectIds), System.Data.ParameterDirection.Input);
        command.Parameters.Add("limit", OracleDbType.Int32, limit, System.Data.ParameterDirection.Input);

        Dictionary<long, List<IReadOnlyDictionary<string, object?>>> grouped = [];
        List<long> order = [];

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long oid = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

            if (!grouped.TryGetValue(oid, out List<IReadOnlyDictionary<string, object?>>? rows))
            {
                rows = [];
                grouped[oid] = rows;
                order.Add(oid);
            }

            Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

            for (int i = 0; i < fields.Count; i++)
            {
                attributes[fields[i].Name] = reader.IsDBNull(i + 1)
                    ? null
                    : OracleFeatureSource.Normalise(reader.GetValue(i + 1), fields[i].Type);
            }

            rows.Add(attributes);
        }

        return [.. order.Select(oid => new RelatedGroup(oid, grouped[oid]))];
    }
}
