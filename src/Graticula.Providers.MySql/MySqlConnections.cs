using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>Turns a <see cref="MySqlLocator"/> into the driver's connection string — ADR-208.</summary>
/// <remarks>
/// <b>Here and only here</b>, because this is the one project that may name the driver. The locator carries fields;
/// the driver's builder quotes whatever needs quoting.
/// </remarks>
internal static partial class MySqlConnections
{
    /// <summary>The driver's connection string for a stored locator.</summary>
    /// <param name="stored">A <c>mysql:</c> locator, unsealed.</param>
    /// <param name="pooling">Whether to pool; a registration probe does not, a layer's source does.</param>
    /// <param name="timeoutSeconds">How long to wait for a connection.</param>
    /// <param name="userVariables">
    /// Whether a statement may name a session variable (<c>@graticula_editor</c>, which a history trigger reads) — the
    /// writer's connections only: anywhere else an <c>@name</c> with no parameter stays the error it should be.
    /// </param>
    /// <returns>The connection string.</returns>
    public static string For(string stored, bool pooling, int timeoutSeconds = 15, bool userVariables = false)
    {
        MySqlSource source = MySqlLocator.Of(stored);

        MySqlConnectionStringBuilder builder = new()
        {
            Server = source.Host,
            Port = (uint)source.Port,
            Database = source.Database,
            UserID = source.User,
            Pooling = pooling,
            ConnectionTimeout = (uint)Math.Max(1, timeoutSeconds),

            // A row's identity and a GUID come back as the types their columns declare, and a zero date is a null.
            GuidFormat = MySqlGuidFormat.None,
            ConvertZeroDateTime = true,
            AllowUserVariables = userVariables,
        };

        if (!string.IsNullOrEmpty(source.Password))
        {
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// A connection with the session pinned to what every statement here is written for: <c>ANSI</c> mode, so a
    /// double-quoted name is a name and <c>||</c> concatenates as in PostgreSQL; strict writes; and UTC, so a
    /// <c>TIMESTAMP</c> reads back the instant it holds.
    /// </summary>
    public static async Task<MySqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using MySqlCommand session = new(
                "set session sql_mode = 'ANSI,STRICT_TRANS_TABLES,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION', time_zone = '+00:00'",
                connection);

            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Whether the connected user may insert, update and delete rows of a table — all three, at the global, database or
    /// table level, as <c>CanWriteAsync</c> asks of Oracle; two of three is not editable, for its reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from <c>SHOW GRANTS</c>, not from <c>information_schema</c></b>, because only the first sees a role: a user
    /// whose rights come through a role finds no row of them in <c>schema_privileges</c> (measured on 8.4), while
    /// <c>SHOW GRANTS</c> without <c>FOR</c> writes out the privileges of the session's active roles as its own.
    /// </para>
    /// <para>
    /// <b>A database-level grant names a pattern, not a database</b>: <c>_</c> and <c>%</c> are wildcards in it, and the
    /// official image grants its user <c>`graticula\_test`.*</c>. Found by CI, whose container was the first to grant
    /// the escaped form; an equality against <c>schema_privileges</c> called the layer read-only.
    /// </para>
    /// </remarks>
    public static async Task<bool> CanWriteAsync(
        MySqlConnection connection, string database, string table, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = new("show grants", connection);
        List<string> grants = [];

        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                grants.Add(reader.GetString(0));
            }
        }

        return Writes(grants, database, table);
    }

    /// <summary>
    /// Whether these <c>SHOW GRANTS</c> lines give INSERT, UPDATE and DELETE on a table — on every column, so a
    /// column-level grant does not count; a <c>GRANT role TO user</c> line, which names no object, is passed over.
    /// </summary>
    internal static bool Writes(IEnumerable<string> grants, string database, string table)
    {
        HashSet<string> held = new(StringComparer.Ordinal);

        foreach (string grant in grants)
        {
            Match line = GrantLine().Match(grant);

            if (!line.Success || !Covers(line.Groups["on"].Value.Trim(), database, table))
            {
                continue;
            }

            foreach (string privilege in TopLevel(line.Groups["privileges"].Value))
            {
                string named = privilege.Trim().ToUpperInvariant();

                if (named == "ALL" || named == "ALL PRIVILEGES")
                {
                    held.UnionWith(["INSERT", "UPDATE", "DELETE"]);
                }
                else if (!named.Contains('(', StringComparison.Ordinal))
                {
                    held.Add(named);
                }
            }
        }

        return held.Contains("INSERT") && held.Contains("UPDATE") && held.Contains("DELETE");
    }

    /// <summary>Whether a grant's object — <c>*.*</c>, <c>`db`.*</c> or <c>`db`.`table`</c> — covers the table.</summary>
    private static bool Covers(string on, string database, string table)
    {
        if (on == "*.*")
        {
            return true;
        }

        Match named = GrantObject().Match(on);

        if (!named.Success)
        {
            return false;
        }

        string db = Unquote(named.Groups["db"].Value);

        if (named.Groups["table"].Value == "*")
        {
            return Regex.IsMatch(database, "^" + Pattern(db) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        string tab = Unquote(named.Groups["table"].Value);

        return string.Equals(db, database, StringComparison.OrdinalIgnoreCase) && string.Equals(tab, table, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A quoted name as the server wrote it: in backticks, or — <b>on a session in <c>ANSI</c> mode, which every session
    /// here is</b> — in double quotes, with the quote doubled inside.
    /// </summary>
    private static string Unquote(string quoted) =>
        quoted[1..^1].Replace(new string(quoted[0], 2), quoted[0].ToString(), StringComparison.Ordinal);

    /// <summary>A database pattern from a grant as a regular expression: <c>_</c> one character, <c>%</c> any, <c>\</c> escaping.</summary>
    private static string Pattern(string grant)
    {
        System.Text.StringBuilder pattern = new();

        for (int i = 0; i < grant.Length; i++)
        {
            char c = grant[i];

            if (c == '\\' && i + 1 < grant.Length)
            {
                pattern.Append(Regex.Escape(grant[++i].ToString()));
            }
            else
            {
                pattern.Append(c switch
                {
                    '_' => ".",
                    '%' => ".*",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        return pattern.ToString();
    }

    /// <summary>A privilege list split at the commas outside a column list.</summary>
    private static IEnumerable<string> TopLevel(string privileges)
    {
        int depth = 0;
        int from = 0;

        for (int i = 0; i < privileges.Length; i++)
        {
            switch (privileges[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return privileges[from..i];
                    from = i + 1;
                    break;
            }
        }

        yield return privileges[from..];
    }

    [GeneratedRegex(@"^GRANT\s+(?<privileges>.+?)\s+ON\s+(?:TABLE\s+)?(?<on>\*\.\*|(?:`(?:[^`]|``)*`|""(?:[^""]|"""")*"")\.(?:\*|(?:`(?:[^`]|``)*`|""(?:[^""]|"""")*"")))\s+TO\s", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GrantLine();

    [GeneratedRegex(@"^(?<db>(?:`(?:[^`]|``)*`|""(?:[^""]|"""")*""))\.(?<table>\*|(?:`(?:[^`]|``)*`|""(?:[^""]|"""")*""))$")]
    private static partial Regex GrantObject();
}
