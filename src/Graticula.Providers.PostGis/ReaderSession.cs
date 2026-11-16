using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>
/// The member a layer statement is read for, sent to PostgreSQL as <c>graticula.username</c> on every statement and
/// every connection a layer pool hands out — ADR-300 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A per-reader view (ADR-300 §5.2) has <c>nullif(current_setting('graticula.username', true), '')</c> in
/// its <c>where</c>, so the database gives each reader their own rows on every face that reads the view by name. The
/// name has to reach the session the statement runs on, and a pooled connection is shared by every request, so it is
/// sent with the work rather than trusted from the connection.
/// </para>
/// <para>
/// <b>How, the way <c>TenantSource</c> sends <c>graticula.tenant</c> (ADR-204).</b> A connectionless command carries
/// <c>SET graticula.username = '…';</c> in its own batch, ahead of the caller's statement — a <c>SET</c> returns no rows
/// and counts none, and Npgsql's reader passes over a statement that returns no rows, so the reader, the scalar and the
/// row count are the caller's statement's. A connection is opened, then the <c>SET</c> runs on it before it is handed
/// back, so every command made on it afterwards — a transaction's, a streamed read's — runs under the name. A connection
/// returned to the pool is reset by Npgsql (<c>DISCARD ALL</c>) before its next use; the layer pools are built with
/// reset on close forced on (<c>LayerConnections</c>), so an operator's connection string cannot turn it off.
/// </para>
/// <para>
/// <b>Empty, not absent, when nobody is signed in.</b> The command path always sends the setting — an anonymous
/// request, a background job and a signed-in member alike — so a statement never runs under a name it did not set,
/// whatever the connection held; it costs no round trip, since it is in the same batch. The connection path sends it
/// only when there is a name, because there it costs a round trip and the reset already left it unset; an unset name is
/// <c>null</c> and an empty one is made <c>null</c> by the view, so either matches nothing.
/// </para>
/// <para>
/// <b>Not for statements with positional parameters</b> (<c>$1</c>): PostgreSQL refuses several statements in one
/// extended-protocol message. Nothing that reads a layer's rows uses them; the projector and the reference checks
/// that do read <c>spatial_ref_sys</c> and are left on the pool.
/// </para>
/// </remarks>
public static class ReaderSession
{
    /// <summary>The PostgreSQL setting a per-reader view reads.</summary>
    public const string Setting = "graticula.username";

    /// <summary>What a view filter writes to name its reader — Survey123's spelling (ADR-300 §5.2).</summary>
    public const string Variable = "${username}";

    /// <summary>
    /// The literal <see cref="Variable"/> stands as while a filter is parsed: a string no reader can be called, so the
    /// view's DDL can find it among the parsed filter's values and put the setting in its place.
    /// </summary>
    public const string Marker = "graticula.username";

    /// <summary>What a per-reader view's DDL reads in place of <see cref="Marker"/>.</summary>
    public const string Expression = "nullif(current_setting('" + Setting + "', true), '')";

    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The name statements are read for now, or null for none.</summary>
    public static string? Name => Current.Value;

    /// <summary>Reads as <paramref name="name"/> until the returned scope is disposed.</summary>
    /// <param name="name">The member's qualified name, or null or empty for nobody.</param>
    /// <returns>The scope; disposing it restores the name before it.</returns>
    public static IDisposable Enter(string? name)
    {
        string? before = Current.Value;
        Current.Value = string.IsNullOrEmpty(name) ? null : name;
        return new Scope(before);
    }

    /// <summary>The <c>SET</c> that names <paramref name="name"/>, as a statement ending in a newline.</summary>
    /// <param name="name">The name, or null for none.</param>
    /// <returns>The statement.</returns>
    /// <remarks>
    /// <b>A literal, because <c>SET</c> takes no parameter</b>, quoted by doubling its quotes — the session runs with
    /// <c>standard_conforming_strings</c> on, PostgreSQL's default since 9.1, so a backslash is an ordinary character.
    /// A name with a NUL in it is refused: PostgreSQL cannot hold one, and no member is called that.
    /// </remarks>
    public static string Statement(string? name)
    {
        if (name is not null && name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A member name cannot contain NUL.", nameof(name));
        }

        return $"SET {Setting} = '{(name ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}';\n";
    }

    /// <summary>
    /// A connectionless command whose batch names the current reader ahead of <paramref name="sql"/>.
    /// </summary>
    /// <param name="source">The layer pool.</param>
    /// <param name="sql">The statement.</param>
    /// <returns>The command.</returns>
    public static NpgsqlCommand CreateReaderCommand(this NpgsqlDataSource source, string sql)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.CreateCommand(Statement(Current.Value) + sql);
    }

    /// <summary>A connection from the layer pool, the current reader named on it before it is returned.</summary>
    /// <param name="source">The layer pool.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection.</returns>
    public static async ValueTask<NpgsqlConnection> OpenReaderConnectionAsync(
        this NpgsqlDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        NpgsqlConnection connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (Current.Value is not { } name)
        {
            return connection;
        }

        try
        {
            await using NpgsqlCommand set = new(Statement(name), connection);
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Scope(string? before) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                Current.Value = before;
                _disposed = true;
            }
        }
    }
}
