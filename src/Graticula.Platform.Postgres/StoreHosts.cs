using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A store named by more than one host — a primary and its standby — and the one place that knows how to connect to
/// it: ADR-319 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a function rather than the connection string alone.</b> Npgsql 9 refuses <c>Target Session Attributes</c> in a
/// multi-host string handed to a data source builder (<i>"When creating a multi-host data source,
/// TargetSessionAttributes cannot be specified"</i>, measured 2026-11-23): the target is chosen by
/// <c>BuildMultiHost().WithTargetSession(…)</c>. So every pool over the store is built here, and a string given to
/// <c>new NpgsqlConnection(…)</c> — which does accept the key — is given it by <see cref="ForConnection"/>.
/// </para>
/// <para>
/// <b>A string that names one host passes through every function here unchanged</b>, so the baseline deployment
/// (CLAUDE.md §6) builds exactly what it built before.
/// </para>
/// </remarks>
public static class StoreHosts
{
    /// <summary>
    /// The multi-host sources already built, by their connection string and target: the wrapper Npgsql hands back
    /// does nothing when disposed, so a short-lived caller that built one per request would leak a pool each time.
    /// </summary>
    private static readonly ConcurrentDictionary<string, NpgsqlMultiHostDataSource> Built = new(StringComparer.Ordinal);

    /// <summary>Whether a connection string names more than one host.</summary>
    /// <param name="connectionString">The string.</param>
    /// <returns>True for a primary-and-standby store.</returns>
    public static bool IsMultiHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Host is { } host && host.Contains(',', StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The hosts a connection string names, each with its port — <c>Host=a:5432,b:5433</c>, or <c>Host=a,b;Port=5432</c>.
    /// </summary>
    /// <param name="connectionString">The string.</param>
    /// <returns>The hosts in the order named.</returns>
    public static IReadOnlyList<(string Host, int Port)> Of(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        List<(string, int)> hosts = [];

        foreach (string named in (builder.Host ?? "localhost").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string host = named;
            int port = builder.Port;

            if (named.StartsWith('['))
            {
                int close = named.IndexOf(']', StringComparison.Ordinal);
                host = named[1..close];
                if (close + 1 < named.Length && named[close + 1] == ':')
                {
                    port = int.Parse(named[(close + 2)..], CultureInfo.InvariantCulture);
                }
            }
            else if (named.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 && named.LastIndexOf(':') == colon)
            {
                host = named[..colon];
                port = int.Parse(named[(colon + 1)..], CultureInfo.InvariantCulture);
            }

            hosts.Add((host, port));
        }

        return hosts;
    }

    /// <summary>How a host is named in the epoch's suspect list and in the store's report: <c>host:port</c>, lower case.</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <returns>The address.</returns>
    public static string Address(string host, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{host.ToLowerInvariant()}:{port}");

    /// <summary>
    /// The string for <c>new NpgsqlConnection(…)</c>: a multi-host store with <c>Target Session Attributes=read-write</c>
    /// unless the string names another target, so a listener or a lock is taken on the primary and never on the
    /// standby, where a <c>NOTIFY</c> is never heard. One host: unchanged.
    /// </summary>
    /// <param name="connectionString">The string.</param>
    /// <returns>The string to connect with.</returns>
    public static string ForConnection(string connectionString)
    {
        if (!IsMultiHost(connectionString))
        {
            return connectionString;
        }

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        builder.TargetSessionAttributes ??= "read-write";
        RecheckQuickly(builder, connectionString);
        return builder.ConnectionString;
    }

    /// <summary>
    /// libpq's form of the hosts, for <c>pg_dump</c> and <c>pg_restore</c>: <c>PGHOST</c> and <c>PGPORT</c> as lists, and
    /// <c>PGTARGETSESSIONATTRS</c> — <c>read-write</c> for a multi-host store, null for one host.
    /// </summary>
    /// <param name="login">The connection.</param>
    /// <returns>The three environment values.</returns>
    public static (string Hosts, string Ports, string? Target) ForLibpq(NpgsqlConnectionStringBuilder login)
    {
        ArgumentNullException.ThrowIfNull(login);

        if (!IsMultiHost(login.ConnectionString))
        {
            return (login.Host ?? "localhost", login.Port.ToString(CultureInfo.InvariantCulture), null);
        }

        IReadOnlyList<(string Host, int Port)> hosts = Of(login.ConnectionString);
        return (
            string.Join(',', hosts.Select(h => h.Host)),
            string.Join(',', hosts.Select(h => h.Port.ToString(CultureInfo.InvariantCulture))),
            login.TargetSessionAttributes ?? "read-write");
    }

    /// <summary>
    /// Builds a pool. One host: <c>builder.Build()</c>, as everywhere before. More than one: Npgsql's multi-host source,
    /// chosen by the target the string named (<c>read-write</c> when it named none), with ADR-319 §5.3's check that no
    /// connection is made to a primary on a timeline older than the newest this process has seen.
    /// </summary>
    /// <param name="builder">The builder, configured.</param>
    /// <returns>The pool.</returns>
    public static NpgsqlDataSource Build(NpgsqlDataSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string original = builder.ConnectionString;

        if (!IsMultiHost(original))
        {
            return builder.Build();
        }

        TargetSessionAttributes target = Target(builder.ConnectionStringBuilder.TargetSessionAttributes);
        builder.ConnectionStringBuilder.TargetSessionAttributes = null;
        RecheckQuickly(builder.ConnectionStringBuilder, original);

        string key = builder.ConnectionStringBuilder.ConnectionString;
        NpgsqlMultiHostDataSource source = Built.GetOrAdd(key, _ =>
        {
            builder.UsePhysicalConnectionInitializer(StoreEpoch.Check, StoreEpoch.CheckAsync);
            return builder.BuildMultiHost();
        });

        return source.WithTargetSession(target);
    }

    /// <summary>Npgsql's default of ten seconds keeps a just-promoted standby marked a standby for that long.</summary>
    private static void RecheckQuickly(NpgsqlConnectionStringBuilder builder, string original)
    {
        if (!original.Contains("Recheck", StringComparison.OrdinalIgnoreCase))
        {
            builder.HostRecheckSeconds = 1;
        }
    }

    private static TargetSessionAttributes Target(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        null or "" or "read-write" => TargetSessionAttributes.ReadWrite,
        "any" => TargetSessionAttributes.Any,
        "read-only" => TargetSessionAttributes.ReadOnly,
        "primary" => TargetSessionAttributes.Primary,
        "standby" => TargetSessionAttributes.Standby,
        "prefer-primary" => TargetSessionAttributes.PreferPrimary,
        "prefer-standby" => TargetSessionAttributes.PreferStandby,
        _ => throw new ArgumentException($"'{named}' is not a target session attribute Npgsql knows."),
    };
}
