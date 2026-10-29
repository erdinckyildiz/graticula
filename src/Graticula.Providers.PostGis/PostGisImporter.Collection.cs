using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>A column of a feature collection an analysis reads, by the name it is stored under.</summary>
/// <param name="Name">The column's name, already made a column name.</param>
/// <param name="Type">Its type.</param>
public sealed record CollectionColumn(string Name, FieldType Type);

/// <summary>One row of a feature collection: its values in column order, and its shape as WKB in the collection's reference.</summary>
/// <param name="Values">The values.</param>
/// <param name="Wkb">The shape, or null for none.</param>
public sealed record CollectionRow(IReadOnlyList<object?> Values, byte[]? Wkb);

/// <summary>What an analysis made, read back to be returned rather than kept — ADR-222 condition 4.</summary>
/// <param name="Columns">Each column's name and PostgreSQL type, the object id first.</param>
/// <param name="Rows">The rows, at most the limit asked.</param>
/// <param name="Srid">The reference the shapes are in, or 0 for a table without shapes.</param>
public sealed record AnalysisRows(
    IReadOnlyList<(string Name, string Type)> Columns, IReadOnlyList<CollectionRow> Rows, int Srid);

/// <summary>
/// Feature collections in and out of an analysis — ADR-222 condition 4: a collection sent as an input is written to a
/// table of its own for the run, and a result asked for without a name is read back from the run's table and returned.
/// </summary>
/// <remarks>
/// <b>A table, not a value list in the statement</b>, so every tool reads a collection exactly as it reads a hosted
/// layer — the same filter, the same index, the same SQL — and nothing about a tool knows where its input came from.
/// The table is the run's own, named from the job, and dropped when the run ends however it ends.
/// </remarks>
public sealed partial class PostGisImporter
{
    /// <summary>The table a run's <paramref name="index"/>th collection is written to.</summary>
    /// <param name="job">The job.</param>
    /// <param name="index">1 for the first input, 2 for the second.</param>
    /// <returns>The table's name in the hosted schema.</returns>
    public static string CollectionTableFor(Guid job, int index) =>
        "analysis_" + job.ToString("N")[..20] + "_in" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes a feature collection to a table the run reads as a layer.</summary>
    /// <param name="table">The table, from <see cref="CollectionTableFor"/>.</param>
    /// <param name="columns">Its columns.</param>
    /// <param name="srid">The reference its shapes are in.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table as a layer definition.</returns>
    public async Task<LayerDefinition> WriteCollectionAsync(
        string table, IReadOnlyList<CollectionColumn> columns, int srid, IReadOnlyList<CollectionRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        RefuseOutsideHosted(HostedSchema, table, "make");

        string definitions = string.Concat(columns.Select(c => $"{LayerDefinition.Quote(c.Name)} {SqlTypeForField(c.Type)}, "));
        string names = string.Concat(columns.Select(c => LayerDefinition.Quote(c.Name) + ", "));
        string parameters = string.Concat(columns.Select((_, i) => $"@c{i.ToString(CultureInfo.InvariantCulture)}, "));

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHostedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, $"drop table if exists {Qualified(table)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction,
            $"create table {Qualified(table)} (objectid integer generated always as identity primary key, {definitions}"
            + $"geom geometry(Geometry, {Srid(srid)}))",
            cancellationToken).ConfigureAwait(false);

        string insert =
            $"insert into {Qualified(table)} ({names}geom) values ({parameters}ST_GeomFromWKB(@g, {Srid(srid)}))";

        // In batches, so a collection of thousands is a few round trips rather than thousands.
        foreach (CollectionRow[] chunk in rows.Chunk(500))
        {
            await using NpgsqlBatch batch = new(connection, transaction);

            foreach (CollectionRow row in chunk)
            {
                NpgsqlBatchCommand command = new(insert);

                for (int i = 0; i < columns.Count; i++)
                {
                    command.Parameters.AddWithValue($"c{i.ToString(CultureInfo.InvariantCulture)}", row.Values[i] ?? DBNull.Value);
                }

                command.Parameters.AddWithValue("g", (object?)row.Wkb ?? DBNull.Value);
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, $"create index on {Qualified(table)} using gist (geom)", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"analyze {Qualified(table)}", cancellationToken).ConfigureAwait(false);

        return new LayerDefinition(table, HostedSchema, table, "geom", srid, "objectid", "objectid", isHosted: true);
    }

    /// <summary>
    /// Puts an analysis's table in place of a hosted layer's, under the layer's table name, in one transaction — what
    /// <c>outputName</c>'s <c>overwrite</c> asks for: the service, the layer and the item stay, the data is the new run's.
    /// </summary>
    /// <param name="made">The run's table.</param>
    /// <param name="replaced">The layer's table.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">The layer keeps attachments or a history, which the new data has no
    /// rows for.</exception>
    public async Task ReplaceWithAnalysisAsync(string made, string replaced, CancellationToken cancellationToken)
    {
        RefuseOutsideHosted(HostedSchema, made, "move");
        RefuseOutsideHosted(HostedSchema, replaced, "replace");

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Attachments and history belong to the old rows; the catalogue would go on offering them over rows they are not
        // about, so a layer that keeps either is not overwritten by an analysis.
        await using (NpgsqlCommand kept = new(
            "select to_regclass(@a) is not null or to_regclass(@h) is not null", connection, transaction))
        {
            kept.Parameters.AddWithValue("a", $"{HostedSchema}.{replaced}{PostGisAttachmentStore.Suffix}");
            kept.Parameters.AddWithValue("h", $"{HostedSchema}.{replaced}{PostGisFeatureHistory.Suffix}");

            if (await kept.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new InvalidOperationException(
                    "The layer named keeps attachments or a history of its edits, which an analysis's result has no rows "
                    + "for; it is not overwritten. Write the result under a new name.");
            }
        }

        await ExecuteAsync(connection, transaction, $"drop table {Qualified(replaced)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, $"alter table {Qualified(made)} rename to {LayerDefinition.Quote(replaced)}", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a hosted layer's features to a file as GeoJSON in WGS 84, streamed row by row — what a user tool's run is
    /// handed for a layer input (ADR-223 §5.5).
    /// </summary>
    /// <param name="input">The layer, its filter and extent.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="limit">The most features written; one more is read so the caller can tell the limit was passed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many features were read, up to the limit and one.</returns>
    public async Task<int> WriteGeoJsonAsync(AnalysisInput input, string path, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "read");

        string geometry = LayerDefinition.Quote(input.Layer.RequireGeometryColumn());
        string sql =
            // Not the shape, and not the row's id: a tool's output carries its fields, and an id among them would collide
            // with the one its import gives every row.
            $"select json_build_object('type', 'Feature', 'properties', to_jsonb(s) - {Literal(input.Layer.RequireGeometryColumn())} "
            + $"- {Literal(input.Layer.IntegerIdentityColumn ?? input.Layer.IdentityColumn)}, "
            + $"'geometry', ST_AsGeoJSON(ST_Transform(s.{geometry}, 4326))::json)::text "
            + $"from (select * from {Qualified(input.Layer.TableName)}{Filter(input, geometry)}) s limit @limit";

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("limit", limit + 1);
        Bind(command, input);

        await using System.IO.StreamWriter writer = new(path, append: false, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync("{\"type\":\"FeatureCollection\",\"features\":[").ConfigureAwait(false);
        int count = 0;

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(count++ == 0 ? reader.GetString(0) : "," + reader.GetString(0)).ConfigureAwait(false);
            }
        }

        await writer.WriteAsync("]}").ConfigureAwait(false);
        return count;
    }

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The largest value of a column an analysis wrote, or null when the table holds none.</summary>
    /// <param name="table">The run's table.</param>
    /// <param name="column">The column, one the run named itself.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The largest value.</returns>
    public async Task<double?> LargestAsync(string table, string column, CancellationToken cancellationToken)
    {
        RefuseOutsideHosted(HostedSchema, table, "read");
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"select max({LayerDefinition.Quote(column)})::double precision from {Qualified(table)}", connection);
        object? largest = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return largest is double value ? value : null;
    }

    /// <summary>Reads what an analysis wrote, to return it as a feature collection.</summary>
    /// <param name="table">The run's table.</param>
    /// <param name="limit">The most rows read; one more is read so the caller can tell the limit was passed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The columns and rows, polygons wound as ArcGIS winds them.</returns>
    public async Task<AnalysisRows> ReadAnalysisAsync(string table, int limit, CancellationToken cancellationToken)
    {
        RefuseOutsideHosted(HostedSchema, table, "read");

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<(string Name, string Type)> columns = [];
        int srid;

        await using (NpgsqlCommand described = new(
            """
            select a.attname, format_type(a.atttypid, a.atttypmod)
              from pg_attribute a
             where a.attrelid = (quote_ident(@schema) || '.' || quote_ident(@table))::regclass
               and a.attnum > 0 and not a.attisdropped and a.attname <> 'geom'
             order by a.attname <> 'objectid', a.attnum
            """, connection))
        {
            described.Parameters.AddWithValue("schema", HostedSchema);
            described.Parameters.AddWithValue("table", table);
            await using NpgsqlDataReader reader = await described.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // A group table has no shapes, and so no reference: 0 says so.
        await using (NpgsqlCommand reference = new(
            "select coalesce((select srid from geometry_columns where f_table_schema = @schema and f_table_name = @table "
            + "and f_geometry_column = 'geom'), 0)", connection))
        {
            reference.Parameters.AddWithValue("schema", HostedSchema);
            reference.Parameters.AddWithValue("table", table);
            srid = Convert.ToInt32(await reference.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        List<CollectionRow> rows = [];
        string select =
            $"select {string.Concat(columns.Select(c => LayerDefinition.Quote(c.Name) + ", "))}"
            + (srid == 0 ? "null::bytea " : "ST_AsBinary(ST_ForcePolygonCW(geom)) ")
            + $"from {Qualified(table)} order by objectid limit @limit";

        await using (NpgsqlCommand read = new(select, connection))
        {
            read.Parameters.AddWithValue("limit", limit + 1);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                object?[] values = new object?[columns.Count];

                for (int i = 0; i < columns.Count; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(new CollectionRow(values, reader.IsDBNull(columns.Count) ? null : (byte[])reader.GetValue(columns.Count)));
            }
        }

        return new AnalysisRows(columns, rows, srid);
    }
}
