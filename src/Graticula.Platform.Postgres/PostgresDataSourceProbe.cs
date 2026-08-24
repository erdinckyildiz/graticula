using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using System.Net.Sockets;
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
        catch (Exception e) when (e is NpgsqlException or SocketException or TimeoutException)
        {
            // Everything that stops us reaching a session: DNS, refused, TLS,
            // wrong password, no such database. The administrator's next move is
            // network or credentials, and nothing about the data is known.
            //
            // <b>`SocketException` is not an `NpgsqlException`, and that was D-102.</b> A host that
            // does not resolve throws `SocketException (11001): No such host is known` **bare** — not
            // wrapped — so for as long as this caught only `NpgsqlException` the single most likely
            // mistake on the registration form escaped the probe entirely, reached the global handler,
            // and came back as *the reason is in the server log; it is not repeated here*. The debt row
            // blamed `ErrorResponse`'s catch-all and a missing classification there; the cause was
            // three layers earlier, which is why that row said a row is a lead rather than a finding.
            // `TimeoutException` is beside it for the host that resolves and never answers.
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
    /// Driven from the catalogue's own tables, filtered by <c>has_table_privilege</c> —
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
        // <b>Read from the catalogue directly rather than through
        // <c>geometry_columns</c>, and the reason is a measured failure.</b> That view is a
        // PostGIS compatibility shim, and its definition scans **every constraint in the
        // database** looking for pre-2.0 declarations like `CHECK (srid(geom) = 4326)`,
        // pulling the number out with `split_part(..., ' = ', 2)::integer`. Any constraint
        // whose text merely resembles that pattern poisons the whole view: on 2026-08-17 a
        // third-party schema in the same database carried
        // `CHECK ((st_srid(geometry) = srid))` — comparing against a *column* named `srid` —
        // and every read of `geometry_columns` in that database failed with
        // `22P02: invalid input syntax for type integer: "srid"`. Registering a data source
        // and listing publishable tables both stopped working, in a database where nothing
        // of ours was wrong.
        //
        // <b>That is the ordinary case for us, not an exotic one.</b> A registered source is
        // somebody else's database: their constraints, their conventions, their other
        // products. Depending on a view that any of those can break is depending on their
        // discipline instead of on the catalogue.
        //
        // The typmod is where a modern PostGIS geometry keeps its SRID and type, which is
        // exactly what the view reads for anything declared since 2.0. What is lost is a
        // column declared as bare `geometry` whose SRID lives only in an old check
        // constraint — and those are precisely the databases where the view breaks anyway.
        // Such a column reports srid 0 and is filtered out below, which is the same answer
        // the view gave when its parse failed: nothing.
        const string Sql = """
            select
              n.nspname,
              c.relname,
              a.attname,
              postgis_typmod_srid(a.atttypmod) as srid,
              nullif(upper(postgis_typmod_type(a.atttypmod)), 'GEOMETRY') as geometry_type,
              (
                -- A unique integer column, preferring a single-column primary
                -- key. int8 is excluded: ADR-013 §2a wants an ArcGIS object id,
                -- and JavaScript loses integer precision above 2^53.
                select ai.attname
                from pg_index i
                join pg_attribute ai
                  on ai.attrelid = i.indrelid and ai.attnum = any (i.indkey)
                where i.indrelid = c.oid
                  and i.indisunique
                  and i.indnkeyatts = 1
                  and ai.atttypid in ('int2'::regtype, 'int4'::regtype)
                order by i.indisprimary desc, ai.attnum
                limit 1
              ) as object_id_column,
              (
                -- <b>The table's own key, whatever its type — D-50.</b> Reported even
                -- when it cannot be an object id, because an operator choosing an
                -- identity column wants to see the key the table was designed around
                -- before deciding against it. A composite key reports nothing: naming
                -- one of its columns would be worse than naming none.
                select ai.attname
                from pg_index i
                join pg_attribute ai
                  on ai.attrelid = i.indrelid and ai.attnum = any (i.indkey)
                where i.indrelid = c.oid and i.indisprimary and i.indnkeyatts = 1
                limit 1
              ) as primary_key_column,
              (
                -- <b>Every candidate, not the first — D-50.</b> The single suggestion
                -- above made the console able to offer one nomination and only when an
                -- ArcGIS-shaped id already existed; everything else was typed from
                -- memory, and a wrong-but-existing column silently becomes the feature
                -- identity. Same rule as the column above, without the `limit`.
                select coalesce(array_agg(x.attname order by x.pk desc, x.attnum), '{}')
                from (
                  select distinct ai.attname, ai.attnum, bool_or(i.indisprimary) as pk
                  from pg_index i
                  join pg_attribute ai
                    on ai.attrelid = i.indrelid and ai.attnum = any (i.indkey)
                  where i.indrelid = c.oid
                    and i.indisunique
                    and i.indnkeyatts = 1
                    and ai.atttypid in ('int2'::regtype, 'int4'::regtype)
                  group by ai.attname, ai.attnum
                ) x
              ) as identity_candidates,
              pg_catalog.has_table_privilege(c.oid, 'INSERT, UPDATE, DELETE') as writable
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            join pg_type t on t.oid = a.atttypid and t.typname = 'geometry'
            where c.relkind in ('r', 'v', 'm', 'f', 'p')
              and not pg_is_other_temp_schema(c.relnamespace)
              and n.nspname not in ('pg_catalog', 'information_schema', 'topology')
              -- <b>The same filter the view applied, and it is the reason this is not a
              -- disclosure change.</b> A table this caller cannot select from is not
              -- reported, so the list still shows what the credential may read and nothing
              -- more.
              and pg_catalog.has_table_privilege(c.oid, 'SELECT')
              and postgis_typmod_srid(a.atttypmod) > 0
            order by n.nspname, c.relname
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7),
                !reader.IsDBNull(8) && reader.GetBoolean(8)));
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
    private static string Describe(Exception exception) => exception switch
    {
        // <b>Named, because *no such host* is a typo and *connection refused* is a firewall.</b> The
        // generic sentence below is true for both and sends an administrator to look at the wrong
        // thing half the time.
        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            "No host by that name. Check the spelling and, if it is a container name, that this "
            + "server is on the same network as the database.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "The host is there and refused the connection on that port. Either the port is wrong or "
            + "the database is not listening on it from this address.",
        SocketException e =>
            $"Could not reach the host: {e.Message} ({e.SocketErrorCode}).",
        TimeoutException =>
            "The host accepted nothing within the probe's timeout. A firewall that drops packets "
            + "silently looks exactly like this, and so does a database that is starting up.",
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
