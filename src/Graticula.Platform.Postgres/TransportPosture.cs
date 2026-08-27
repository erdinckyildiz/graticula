using System;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// What a connection string actually asks of the transport, in an operator's terms.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-190](../../docs/architecture-debt.md), and
/// [ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §3 already required this</b>:
/// *remote connections default to the driver's verify mode, not merely require — `require`
/// without verification accepts any certificate and is encryption-without-authentication,
/// which is a weaker guarantee than it appears.* Nothing enforced it and nothing said it.
/// </para>
/// <para>
/// <b>Npgsql 9 follows libpq, and the word changed meaning underneath the belief.</b>
/// Npgsql 7 and earlier validated on `Require` and `Trust Server Certificate=true` turned it
/// off; Npgsql 8 realigned with libpq, where `Require` means *fail if the server will not
/// encrypt* and nothing more. Measured 2026-08-27 against a server offering a certificate
/// that expired the day before: `VerifyCA` and `VerifyFull` refuse it and **`Require`
/// accepts it**.
/// </para>
/// <para>
/// <b>A caution, not a refusal, and that is the decision.</b> Refusing `Require` outright
/// would break every registration that already uses it and would be this server overruling
/// libpq's own vocabulary. What it can do is say so at the moment an operator is deciding —
/// the probe exists to tell them what they have before they publish, and it reported a
/// `Require` source and a `VerifyFull` one identically.
/// </para>
/// <para>
/// <b>Loopback is exempt, deliberately.</b> §3's own sentence is that TLS is *required for
/// remote data sources and optional for local ones, since a unix socket or loopback
/// connection to a co-located datastore gains little from encryption and pays a handshake
/// for it* — measured at 2.8 ms in `benchmarks/tls-handshake`. Warning about the baseline
/// deployment, which is this server beside its own PostGIS, is how a warning becomes noise.
/// </para>
/// </remarks>
internal static class TransportPosture
{
    /// <summary>
    /// What to tell an operator about this connection's transport, or null when there is
    /// nothing to say.
    /// </summary>
    /// <param name="builder">The parsed connection string.</param>
    /// <returns>One sentence, or null.</returns>
    public static string? Caution(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (IsLocal(builder.Host))
        {
            return null;
        }

        return builder.SslMode switch
        {
            SslMode.VerifyFull or SslMode.VerifyCA => null,

            SslMode.Require =>
                "This source is reachable and its transport is weaker than it looks. "
                + "'Ssl Mode=Require' encrypts and does **not** check the server's "
                + "certificate: an expired one, a self-signed one and one issued to another "
                + "host are all accepted, so anyone who can answer on that host and port is "
                + "handed these credentials. ADR-014 3 asks for verification on a remote "
                + "source. Use 'Ssl Mode=VerifyFull', or 'VerifyCA' if the certificate's name "
                + "does not match the host you connect to.",

            SslMode.Prefer =>
                "This source is reachable and may not be encrypted at all. 'Ssl Mode=Prefer' "
                + "uses TLS when the server offers it and plain text when it does not, "
                + "without telling anybody which happened. Use 'Ssl Mode=VerifyFull' for a "
                + "remote source.",

            SslMode.Disable or SslMode.Allow =>
                "This source is reachable over an unencrypted connection to another host. "
                + "The credentials in its connection string, and every row it returns, cross "
                + "the network in the clear. Use 'Ssl Mode=VerifyFull'.",

            _ => null,
        };
    }

    /// <summary>
    /// Whether this host is the machine we are on, where §3 makes TLS optional.
    /// </summary>
    /// <remarks>
    /// <b>By name, because that is what a connection string carries.</b> A host that resolves
    /// to a loopback address through DNS is not treated as local: the operator wrote a name
    /// that can point elsewhere tomorrow, and a caution that disappears because of today's
    /// resolution is worse than one that is occasionally unnecessary.
    /// </remarks>
    private static bool IsLocal(string? host) =>
        string.IsNullOrWhiteSpace(host)
        || host.StartsWith('/')
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal);
}
