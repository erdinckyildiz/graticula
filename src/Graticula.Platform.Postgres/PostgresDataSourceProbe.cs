using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Probes a candidate PostGIS data source.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §3.3. Creates nothing, and distinguishes the three failure classes
/// the ADR names, because one generic failure covering all of them is what makes
/// registration hostile: they are fixed by different people, in different
/// systems, with different tools.
/// </para>
/// <para>
/// <b>Every step is a question we would otherwise ask at the first client
/// request</b>, when the person who could fix it has moved on.
/// </para>
/// </remarks>
public sealed class PostgresDataSourceProbe : IDataSourceProbe
{
    /// <summary>
    /// How long the probe may take.
    /// </summary>
    /// <remarks>
    /// Short and separate from the serving timeout. An administrator typing a
    /// wrong hostname should be told within seconds, and the alternative is a
    /// form that appears to hang.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = (int)Timeout.TotalSeconds,
                CommandTimeout = (int)Timeout.TotalSeconds,
            };
        }
        catch (ArgumentException e)
        {
            return Failed(
                ProbeOutcome.CannotConnect,
                $"The connection string could not be parsed: {e.Message}");
        }

        await using NpgsqlDataSource source = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();

        NpgsqlConnection connection;

        try
        {
            connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException e)
        {
            // Everything that stops us reaching a session: DNS, refused, TLS,
            // wrong password, no such database. The administrator's next move is
            // network or credentials, and nothing about the data is known.
            return Failed(ProbeOutcome.CannotConnect, Describe(e));
        }

        await using (connection)
        {
            string serverVersion = connection.PostgreSqlVersion.ToString();

            string? postgis = await ScalarAsync<string>(
                connection,
                "select extversion from pg_extension where extname = 'postgis'",
                cancellationToken).ConfigureAwait(false);

            if (postgis is null)
            {
                // Connected and readable, but this is not a spatial database.
                // Reported as unusable geometry rather than as a connection
                // problem, because the credential is fine.
                return new ProbeResult(
                    ProbeOutcome.UnusableGeometry,
                    "Connected, but PostGIS is not installed in this database. Run "
                    + "'create extension postgis;' as a superuser, or point at a database that "
                    + "already has it.",
                    serverVersion,
                    null,
                    []);
            }

            try
            {
                IReadOnlyList<SourceTable> tables =
                    await ListTablesAsync(connection, cancellationToken).ConfigureAwait(false);

                if (tables.Count == 0)
                {
                    return new ProbeResult(
                        ProbeOutcome.UnusableGeometry,
                        "Connected, with PostGIS present, but this credential can see no table "
                        + "with a geometry column carrying a declared SRID. Either there is "
                        + "nothing to publish, or select has not been granted on the tables you "
                        + "expect — geometry_columns only shows what you may read.",
                        serverVersion,
                        postgis,
                        []);
                }

                return new ProbeResult(
                    ProbeOutcome.Usable,
                    $"Connected. PostGIS {postgis}, {tables.Count} publishable "
                    + $"table{(tables.Count == 1 ? string.Empty : "s")} visible to this credential.",
                    serverVersion,
                    postgis,
                    tables);
            }
            catch (PostgresException e) when (e.SqlState == "42501")
            {
                // insufficient_privilege. Told apart from the above because the
                // credential is correct and a different person fixes it.
                return new ProbeResult(
                    ProbeOutcome.InsufficientPrivilege,
                    "Connected and authenticated, but this role lacks the privileges needed to "
                    + $"inspect the catalogue: {e.MessageText}. A DBA grants this; no password "
                    + "change will help.",
                    serverVersion,
                    postgis,
                    []);
            }
        }
    }

    /// <summary>
    /// Everything publishable that this credential can actually see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven from <c>geometry_columns</c>, which is itself privilege-filtered —
    /// so "no rows" already means "nothing you may read", not "nothing exists".
    /// That is why an empty result is reported as a hint about grants rather
    /// than as an empty database.
    /// </para>
    /// <para>
    /// <b>The object-id candidate is the interesting column.</b> ADR-013 §2a
    /// needs a unique integer, and finding it here means a layer that cannot be
    /// served through the ArcGIS surface is known at registration rather than at
    /// the first query.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<SourceTable>> ListTablesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = """
            select
              g.f_table_schema,
              g.f_table_name,
              g.f_geometry_column,
              g.srid,
              nullif(g.type, 'GEOMETRY') as geometry_type,
              (
                -- A unique integer column, preferring a single-column primary
                -- key. int8 is excluded: ADR-013 §2a wants an ArcGIS object id,
                -- and JavaScript loses integer precision above 2^53.
                select a.attname
                from pg_index i
                join pg_attribute a
                  on a.attrelid = i.indrelid and a.attnum = any (i.indkey)
                where i.indrelid = c.oid
                  and i.indisunique
                  and i.indnkeyatts = 1
                  and a.atttypid in ('int2'::regtype, 'int4'::regtype)
                order by i.indisprimary desc, a.attnum
                limit 1
              ) as object_id_column,
              pg_catalog.has_table_privilege(c.oid, 'INSERT, UPDATE, DELETE') as writable
            from geometry_columns g
            join pg_class c on c.relname = g.f_table_name
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = g.f_table_schema
            where g.srid > 0
            order by g.f_table_schema, g.f_table_name
            """;

        await using NpgsqlCommand command = new(Sql, connection);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<SourceTable> tables = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new SourceTable(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                !reader.IsDBNull(6) && reader.GetBoolean(6)));
        }

        return tables;
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
        where T : class
    {
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as T;
    }

    private static ProbeResult Failed(ProbeOutcome outcome, string message) =>
        new(outcome, message, null, null, []);

    /// <summary>
    /// A connection failure in words an administrator can act on.
    /// </summary>
    /// <remarks>
    /// The SQL state is the reliable part; Npgsql's own message is appended
    /// rather than replaced, because the provider's actual error is what
    /// ADR-017 §3.3 step 2 promises and a wrapped one helps nobody.
    /// </remarks>
    private static string Describe(NpgsqlException exception) => exception switch
    {
        PostgresException { SqlState: "28P01" } =>
            "The password was rejected by the server. The host is reachable; the credential is wrong.",
        PostgresException { SqlState: "28000" } =>
            "The server refused this role, usually because pg_hba.conf does not allow it from our "
            + "address. The password may well be correct.",
        PostgresException { SqlState: "3D000" } =>
            "That database does not exist on this server.",
        PostgresException e =>
            $"The server refused the connection ({e.SqlState}): {e.MessageText}",
        _ =>
            $"Could not reach the server: {exception.Message}",
    };
}
