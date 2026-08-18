using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>Where an imported dataset ended up.</summary>
/// <param name="SchemaName">The schema.</param>
/// <param name="TableName">The table.</param>
/// <param name="Rows">How many rows were written.</param>
/// <param name="SourceSrid">What the file was in.</param>
/// <param name="StoredSrid">What it is stored in.</param>
/// <param name="ProjEngine">Which PROJ transformed it, when it was transformed.</param>
public sealed record ImportResult(
    string SchemaName,
    string TableName,
    int Rows,
    int SourceSrid,
    int StoredSrid,
    string? ProjEngine);

/// <summary>
/// Creates a table in the datastore and loads a parsed dataset into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stored in Web Mercator, transformed once on the way in.</b> GeoJSON is
/// 4326 by specification and vector tiles are served on the 3857 grid, so
/// hosted data would otherwise arrive un-tileable. Reprojecting at ingest
/// answers that once, at a moment when the cost is paid a single time and the
/// result can be inspected — rather than on every tile request, which is
/// [Q-96](../../../docs/open-questions.md) and still open for registered data.
/// It is also what ArcGIS Online does with hosted feature layers.
/// </para>
/// <para>
/// <b>The source SRID is recorded rather than discarded.</b> 4326 to 3857 is a
/// closed formula with no datum shift, so nothing is lost here — but the same
/// path will be asked to accept a national grid one day, and a stored dataset
/// that cannot say what it was is a dataset nobody can check.
/// </para>
/// <para>
/// <b>Identifiers are generated, never taken from the caller.</b>
/// <see href="../../../docs/security.md">security.md</see>: filenames are data,
/// never paths — and a table name is the same problem in a different dialect.
/// The layer's display name is whatever the caller asked for; the table it lives
/// in is derived and sanitised here.
/// </para>
/// </remarks>
public sealed class PostGisImporter
{
    /// <summary>The schema hosted data lives in.</summary>
    /// <remarks>
    /// <b>Separate from <c>public</c> on purpose.</b> Hosted tables are ours to
    /// create and drop; a registered database's tables are the customer's. Mixing
    /// them in one schema means an unpublish that deletes a table has to be very
    /// sure which kind it is looking at, and being very sure is a worse position
    /// than not being able to reach the wrong one.
    /// </remarks>
    public const string HostedSchema = "hosted";

    /// <summary>What tiles are served on, and therefore what hosted data is stored in.</summary>
    public const int StoredSrid = 3857;

    /// <summary>
    /// Whether an imported layer keeps the reference it arrived in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner correction, 2026-08-15: "the imported shapefiles need to stay in
    /// their own projection. If we use a 3857 basemap, it shall be projected on
    /// the fly."</b> Until then every import was transformed to Web Mercator on
    /// the way in, and the original coordinates were gone.
    /// </para>
    /// <para>
    /// <b>That was lossy in a way the response actively denied.</b> A layer
    /// imported as EPSG:5254 came back saying <em>"EPSG:4326 to EPSG:3857 is a
    /// closed formula with no datum shift, so nothing was lost"</em> — a
    /// sentence about 4326 printed over a national-grid import where it is
    /// false. TUREF to WGS 84 is a datum transformation, and survey data does
    /// not survive one unremarked.
    /// </para>
    /// <para>
    /// <b>The reason it was done is real and is answered elsewhere.</b> Tiles
    /// are cut on a Web Mercator grid, so something must transform. Doing it
    /// once at import is cheaper per tile and destroys the source; doing it per
    /// tile costs 3.5× on a cache miss (Q-96) and keeps it. The cache pays that
    /// once per tile, and the data is the thing that cannot be recreated.
    /// </para>
    /// </remarks>
    public const bool KeepsNativeReference = true;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates an importer over the datastore.</summary>
    /// <param name="dataSource">The datastore pool. Must be able to see PostGIS.</param>
    public PostGisImporter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Creates the table and loads the data, or leaves nothing behind.
    /// </summary>
    /// <param name="dataset">What to load.</param>
    /// <param name="requestedName">The name the caller asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Where it went.</returns>
    /// <remarks>
    /// <b>One transaction.</b> A partial import leaves a table containing some of
    /// somebody's data with nothing to say which part — and the caller, seeing a
    /// failure, would reasonably upload it again. Either the whole dataset is
    /// there or the schema is untouched.
    /// </remarks>
    public async Task<ImportResult> ImportAsync(
        ImportedDataset dataset, string requestedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);

        string table = TableNameFor(requestedName);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, transaction,
            $"create schema if not exists {LayerDefinition.Quote(HostedSchema)}",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, transaction, CreateTable(table, dataset), cancellationToken)
            .ConfigureAwait(false);

        int rows = await InsertAsync(connection, transaction, table, dataset, cancellationToken)
            .ConfigureAwait(false);

        // The index and the statistics, inside the same transaction. Without the
        // index every tile request is a sequential scan; without ANALYZE,
        // ST_EstimatedExtent returns nothing and the service publishes with a
        // whole-world extent, which looks like a broken layer.
        await ExecuteAsync(
            connection, transaction,
            $"create index on {Qualified(table)} using gist (geom)",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // ANALYZE cannot run inside a transaction block that is still open, and
        // it is not worth failing an otherwise complete import over.
        await ExecuteAsync(connection, null, $"analyze {Qualified(table)}", cancellationToken)
            .ConfigureAwait(false);

        // Nothing was transformed, so there is no engine to name and no
        // provenance to report. The reference in and the reference stored are
        // the same one.
        return new ImportResult(HostedSchema, table, rows, dataset.Srid, dataset.Srid, null);
    }

    /// <summary>
    /// Asks PostGIS what it makes of a table this importer wrote.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than on the endpoint, because the connection belongs here.</b>
    /// Handing a caller the data source so it can run one query is how a port stops being
    /// one — and the thing that wrote the table is the honest place to ask what got
    /// written. D-53 is the reason it is asked at all.
    /// </remarks>
    /// <param name="schema">The schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The counts and the reasons.</returns>
    public Task<GeometryValidity> ValidityOfAsync(
        string schema, string table, CancellationToken cancellationToken) =>
        GeometryValidity.MeasureAsync(_dataSource, schema, table, "geom", cancellationToken);

    /// <summary>
    /// Creates an empty feature class from a schema somebody designed.
    /// </summary>
    /// <param name="fields">The columns, already validated.</param>
    /// <param name="geometryType">What the layer will hold.</param>
    /// <param name="srid">The spatial reference to store in.</param>
    /// <param name="requestedName">The name the caller asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Where it went.</returns>
    /// <remarks>
    /// <para>
    /// <b>Hosted means the datastore holds it, not that a file produced it.</b>
    /// Importing a file is one way to fill a hosted feature class and designing
    /// its schema is another — a survey layer, an incident log, anything
    /// collected rather than converted starts empty and is filled through
    /// <c>applyEdits</c>. Both end in the same place, which is why they share
    /// this file rather than being two subsystems that happen to make tables.
    /// </para>
    /// <para>
    /// <b>Empty is a complete layer, not a half-finished one.</b> Its extent is
    /// unknown until it has a feature, which the metadata handles by falling
    /// back to the world — so a client can add it, draw nothing, and start
    /// editing.
    /// </para>
    /// </remarks>
    public async Task<ImportResult> DefineAsync(
        IReadOnlyList<FieldDescription> fields,
        GeometryKind geometryType,
        int srid,
        string requestedName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);

        string table = TableNameFor(requestedName);

        StringBuilder sql = new();
        sql.Append(CultureInfo.InvariantCulture, $"create table {Qualified(table)} (")
           .Append(" objectid integer generated always as identity primary key,")
           .Append(CultureInfo.InvariantCulture,
               $" geom geometry({GeometryTypeName(geometryType)}, {srid})");

        foreach (FieldDescription field in fields)
        {
            sql.Append(", ")
               .Append(LayerDefinition.Quote(ColumnNameFor(field.Name)))
               .Append(' ')
               .Append(SqlTypeForField(field.Type))
               .Append(field.Nullable ? string.Empty : " not null");
        }

        sql.Append(')');

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(
                connection, transaction,
                $"create schema if not exists {LayerDefinition.Quote(HostedSchema)}",
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, sql.ToString(), cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(
                connection, transaction,
                $"create index on {Qualified(table)} using gist (geom)",
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // No ANALYZE: there is nothing to analyse, and running it on an empty
        // table would record statistics saying the layer is empty — which stay
        // recorded until autovacuum notices otherwise, so the first thousand
        // features would be queried against a plan built for none.
        return new ImportResult(HostedSchema, table, 0, srid, srid, null);
    }

    private static string SqlTypeForField(FieldType type) => type switch
    {
        FieldType.Boolean => "boolean",
        FieldType.SmallInteger => "smallint",
        FieldType.Integer => "integer",
        FieldType.BigInteger => "bigint",
        FieldType.Single => "real",
        FieldType.Double => "double precision",
        FieldType.Date => "timestamptz",
        FieldType.Guid => "uuid",
        _ => "text",
    };

    /// <summary>Drops a hosted table.</summary>
    /// <param name="schemaName">Its schema.</param>
    /// <param name="tableName">Its name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Refuses to touch anything outside the hosted schema.</b> The caller
    /// passes a schema because the catalogue stores one, and the one thing this
    /// must never do is drop a table in a registered customer database because a
    /// row said so.
    /// </remarks>
    public async Task DropAsync(string schemaName, string tableName, CancellationToken cancellationToken)
    {
        if (!string.Equals(schemaName, HostedSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to drop '{schemaName}.{tableName}': only tables in the "
                + $"'{HostedSchema}' schema were created by this server, and a table we did not "
                + "create is somebody else's data.");
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, null, $"drop table if exists {Qualified(tableName)}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Qualified(string table) =>
        $"{LayerDefinition.Quote(HostedSchema)}.{LayerDefinition.Quote(table)}";

    /// <summary>
    /// A table name derived from what the caller asked for, and safe by
    /// construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lower-cased, non-identifier characters replaced, truncated well inside
    /// PostgreSQL's 63-byte limit, and suffixed with eight random hex characters.
    /// </para>
    /// <para>
    /// <b>The suffix is not decoration.</b> Two people uploading "roads" must not
    /// collide, and a name derived only from the request means the second import
    /// either fails or — far worse — is asked whether to replace the first.
    /// </para>
    /// </remarks>
    private static string TableNameFor(string requested)
    {
        StringBuilder safe = new();

        foreach (char c in requested.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                safe.Append(c);
            }
            else if (safe.Length > 0 && safe[^1] != '_')
            {
                safe.Append('_');
            }

            if (safe.Length >= 40)
            {
                break;
            }
        }

        string stem = safe.ToString().Trim('_');

        // A name that was entirely punctuation, or began with a digit, still has
        // to produce a legal identifier.
        if (stem.Length == 0 || !char.IsAsciiLetter(stem[0]))
        {
            stem = "layer_" + stem;
        }

        return $"{stem}_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}";
    }

    /// <summary>The DDL, with every column type decided by the inference pass.</summary>
    private static string CreateTable(string table, ImportedDataset dataset)
    {
        StringBuilder sql = new();

        sql.Append(CultureInfo.InvariantCulture, $"create table {Qualified(table)} (\n")
           .Append("  objectid integer generated always as identity primary key,\n")
           .Append(CultureInfo.InvariantCulture,
               $"  geom geometry({GeometryTypeName(dataset.GeometryType)}, {dataset.Srid})");

        foreach (InferredColumn column in dataset.Columns)
        {
            sql.Append(",\n  ")
               .Append(LayerDefinition.Quote(ColumnNameFor(column.Name)))
               .Append(' ')
               .Append(SqlTypeFor(column));
        }

        sql.Append("\n)");
        return sql.ToString();
    }

    /// <summary>
    /// A column name safe to quote, derived from the property name.
    /// </summary>
    /// <remarks>
    /// PostgreSQL will accept almost anything inside double quotes, including a
    /// newline. It is accepted here and everywhere afterwards — every query this
    /// server writes quotes its identifiers — but a column called
    /// <c>"drop table"</c> in an error message is a support call nobody needs.
    /// </remarks>
    private static string ColumnNameFor(string property)
    {
        StringBuilder safe = new();

        foreach (char c in property.ToLowerInvariant())
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');

            if (safe.Length >= 60)
            {
                break;
            }
        }

        string name = safe.ToString();

        return name.Length == 0 || !char.IsAsciiLetter(name[0]) ? "f_" + name : name;
    }

    private static string SqlTypeFor(InferredColumn column) => column.Type switch
    {
        FieldType.Boolean => "boolean",
        FieldType.Integer => "integer",
        FieldType.BigInteger => "bigint",
        FieldType.Double => "double precision",

        // <b>text, not varchar(n).</b> Sizing from the longest value in this file
        // guarantees the next import of the same data fails on a longer one, and
        // PostgreSQL stores them identically.
        _ => "text",
    };

    private static string GeometryTypeName(GeometryKind kind) => kind switch
    {
        GeometryKind.Point => "Point",
        GeometryKind.MultiPoint => "MultiPoint",
        GeometryKind.LineString => "LineString",
        GeometryKind.MultiLineString => "MultiLineString",
        GeometryKind.Polygon => "Polygon",
        GeometryKind.MultiPolygon => "MultiPolygon",
        _ => "Geometry",
    };

    /// <summary>Writes every row with the binary COPY protocol.</summary>
    /// <remarks>
    /// <b>COPY rather than inserts.</b> A million single-row inserts is a million
    /// round trips; binary COPY is one stream. The geometry is transformed on the
    /// server with <c>ST_Transform</c> after the copy rather than during it,
    /// because COPY cannot call a function — so the column is loaded in the
    /// source SRID and moved once, in place.
    /// </remarks>
    private static async Task<int> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        ImportedDataset dataset,
        CancellationToken cancellationToken)
    {
        StringBuilder columns = new("geom");

        foreach (InferredColumn column in dataset.Columns)
        {
            columns.Append(", ").Append(LayerDefinition.Quote(ColumnNameFor(column.Name)));
        }

        // Loaded in the file's own SRID; the column declares the stored one, so
        // the geometry is written to a temporary column-compatible value only
        // after transformation. Simplest correct order: load into a text column
        // of WKB, then transform in one statement.
        await ExecuteAsync(
            connection, transaction,
            $"alter table {Qualified(table)} add column import_wkb bytea",
            cancellationToken).ConfigureAwait(false);

        StringBuilder copyColumns = new("import_wkb");

        foreach (InferredColumn column in dataset.Columns)
        {
            copyColumns.Append(", ").Append(LayerDefinition.Quote(ColumnNameFor(column.Name)));
        }

        int rows = 0;

        await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            $"copy {Qualified(table)} ({copyColumns}) from stdin (format binary)",
            cancellationToken).ConfigureAwait(false))
        {
            foreach (ImportedFeature feature in dataset.Features)
            {
                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);

                if (feature.Geometry is null)
                {
                    await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(
                        WkbWriter.ToArray(feature.Geometry), NpgsqlDbType.Bytea, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (InferredColumn column in dataset.Columns)
                {
                    await WriteValueAsync(writer, column, feature, cancellationToken)
                        .ConfigureAwait(false);
                }

                rows++;
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // One statement for the whole table: read the WKB, tag it with the
        // <b>The reference is stamped on, and the coordinates are left
        // alone.</b> Owner correction 2026-08-15: a layer keeps the projection
        // it arrived in, and the tile path transforms per request instead
        // (Q-96). Transforming here was cheaper per tile and destroyed the only
        // copy of the survey coordinates — and for a national grid it is a datum
        // change, not a formula.
        await ExecuteAsync(
            connection, transaction,
            $"""
             update {Qualified(table)}
             set geom = ST_SetSRID(ST_GeomFromWKB(import_wkb), {dataset.Srid})
             where import_wkb is not null
             """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, transaction,
            $"alter table {Qualified(table)} drop column import_wkb",
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    private static async Task WriteValueAsync(
        NpgsqlBinaryImporter writer,
        InferredColumn column,
        ImportedFeature feature,
        CancellationToken cancellationToken)
    {
        if (!feature.Values.TryGetValue(column.Name, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (column.Type)
        {
            case FieldType.Boolean:
                await writer.WriteAsync(value.GetBoolean(), NpgsqlDbType.Boolean, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case FieldType.Integer:
                await writer.WriteAsync(value.GetInt32(), NpgsqlDbType.Integer, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case FieldType.BigInteger:
                await writer.WriteAsync(value.GetInt64(), NpgsqlDbType.Bigint, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case FieldType.Double:
                await writer.WriteAsync(value.GetDouble(), NpgsqlDbType.Double, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                // A string arrives as a string; anything else — an object, an
                // array, a number in a column that turned out to be text —
                // arrives as the JSON that was uploaded. Losing it would be
                // worse than storing it verbatim.
                await writer.WriteAsync(
                    value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
                    NpgsqlDbType.Text,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ProjVersionAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("select postgis_proj_version()", connection);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
