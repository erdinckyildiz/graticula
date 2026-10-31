using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Who is calling, from the socket unless a proxy this deployment trusts says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-12](../../docs/architecture-debt.md): the per-address rate limit used the socket address,
/// never <c>X-Forwarded-For</c>.</b> Behind a reverse proxy every request appears to come from the
/// proxy, so the address limit is one shared bucket — <b>fifty failures anywhere disable sign-in
/// for everybody</b>. The per-account limit is unaffected, and a correct password still works by
/// design, but the address limit stops doing what it is for.
/// </para>
/// <para>
/// <b>And the fix could not simply be *read the header*, which is why the row stood for eleven
/// days.</b> Trusting <c>X-Forwarded-For</c> without a trusted-proxy list lets any caller choose
/// their own rate-limit bucket, which makes the limit zero. The row's own words: too coarse is
/// recoverable; forgeable is not.
/// </para>
/// <para>
/// <b>So the header is read only from a peer the deployment named.</b> `Graticula:TrustedProxies`
/// is empty by default, and with it empty this returns exactly what it returned before — the
/// socket address, for every request. A deployment behind a proxy names the proxy, and only then
/// does a header mean anything.
/// </para>
/// <para>
/// <b>Right to left, skipping trusted hops.</b> <c>X-Forwarded-For</c> is appended to, so the
/// rightmost entry is the one the nearest proxy wrote and the leftmost is whatever the original
/// client claimed — which anybody can write. Walking from the right and stopping at the first
/// address that is not a trusted proxy gives the last hop this deployment can vouch for, which is
/// the strongest thing the header can honestly say.
/// </para>
/// </remarks>
internal static class CallerAddress
{
    /// <summary>Where the resolved address is kept for the rest of the request.</summary>
    private const string Key = "graticula.caller-address";

    /// <summary>The header proxies append to.</summary>
    private const string Header = "X-Forwarded-For";

    /// <summary>
    /// Reads the trusted-proxy list from configuration, refusing anything unparsable.
    /// </summary>
    /// <param name="value">The configured text: addresses or CIDR ranges, comma separated.</param>
    /// <returns>The networks to trust, possibly empty.</returns>
    /// <remarks>
    /// <b>An unparsable entry is a startup failure rather than a silently ignored one.</b> A
    /// deployment that mistypes its proxy's address would otherwise run with the header untrusted
    /// and no sign that anything is wrong — which looks exactly like the state this replaces, and
    /// would be discovered when somebody's sign-in is rate-limited by a stranger.
    /// </remarks>
    public static IReadOnlyList<IPNetwork> Trusted(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<IPNetwork> networks = [];

        foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                  | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out IPNetwork network))
            {
                networks.Add(network);
                continue;
            }

            if (IPAddress.TryParse(entry, out IPAddress? single))
            {
                networks.Add(new IPNetwork(
                    single,
                    single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? 128
                        : 32));

                continue;
            }

            throw new InvalidOperationException(
                $"Graticula:TrustedProxies contains '{entry}', which is neither an IP address nor "
                + "a CIDR range. It is refused rather than ignored: a mistyped proxy address would "
                + "leave the deployment behaving exactly as it did before it was set, and the "
                + "symptom is somebody's sign-in being rate-limited by a stranger.");
        }

        return networks;
    }

    /// <summary>
    /// Works out who is calling and remembers it for the rest of the request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="trusted">The proxies this deployment trusts.</param>
    /// <returns>The caller's address, or null when there is no socket address at all.</returns>
    public static IPAddress? Resolve(HttpContext context, IReadOnlyList<IPNetwork> trusted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trusted);

        IPAddress? socket = context.Connection.RemoteIpAddress;

        IPAddress? resolved = Behind(socket, context, trusted);

        context.Items[Key] = resolved;

        return resolved;
    }

    /// <summary>
    /// The scheme and host the client used, where a trusted proxy ended its connection — ADR-012 §5 phase 3: behind a
    /// proxy that ends TLS the node sees <c>http</c> and the proxy's address for the node, and every absolute address it
    /// writes (<c>owningSystemUrl</c>, <c>tokenServicesUrl</c>, a picture's <c>href</c>) would say so.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="trusted">The proxies this deployment trusts — <c>Graticula:TrustedProxies</c>.</param>
    /// <remarks>
    /// <para>
    /// <b>Only from a proxy the deployment names</b>, the rule <see cref="Resolve"/> keeps for the address: from anybody
    /// else a forwarded header is a caller choosing what this server believes, so with no proxy named nothing changes.
    /// </para>
    /// <para>
    /// <b>The first value of each</b> — the one the outermost proxy wrote, which is the one the client reached — and
    /// <c>X-Forwarded-Proto</c> only when it is <c>http</c> or <c>https</c>. The host is taken as the proxy says it; the
    /// tenant a host reaches (ADR-204) is then the client's host, as it is when a proxy keeps <c>Host</c>.
    /// </para>
    /// </remarks>
    public static void ApplyForwarded(HttpContext context, IReadOnlyList<IPNetwork> trusted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trusted);

        if (trusted.Count == 0 || context.Connection.RemoteIpAddress is not { } socket || !IsTrusted(socket, trusted))
        {
            return;
        }

        string proto = First(context.Request.Headers["X-Forwarded-Proto"].ToString());

        if (proto is "http" or "https")
        {
            context.Request.Scheme = proto;
        }

        string host = First(context.Request.Headers["X-Forwarded-Host"].ToString());

        if (host.Length > 0 && host.Length <= 255 && !host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
        {
            context.Request.Host = new HostString(host);
        }

        static string First(string header) => header.Split(',')[0].Trim().ToLowerInvariant();
    }

    /// <summary>The caller's address, as resolved earlier in this request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The address, or null when there is none.</returns>
    /// <remarks>
    /// <b>Falls back to the socket when nothing resolved it</b>, so a code path reached outside
    /// the pipeline — a test, a future host — sees the same answer the old code gave rather than
    /// nothing at all.
    /// </remarks>
    public static IPAddress? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(Key, out object? found) && found is IPAddress address
            ? address
            : context.Connection.RemoteIpAddress;
    }

    private static IPAddress? Behind(
        IPAddress? socket, HttpContext context, IReadOnlyList<IPNetwork> trusted)
    {
        if (socket is null || trusted.Count == 0 || !IsTrusted(socket, trusted))
        {
            // <b>A caller who is not a trusted proxy is themselves, whatever they wrote in the
            // header.</b> This is the clause that makes the header unforgeable: the only way to
            // have one read is to be an address the deployment named.
            return socket;
        }

        if (!context.Request.Headers.TryGetValue(Header, out var written))
        {
            return socket;
        }

        // One flat list in order, because a proxy may append several at once and clients send the
        // header more than once.
        List<IPAddress> hops = [];

        foreach (string? line in written)
        {
            foreach (string part in (line ?? string.Empty).Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(Bare(part), out IPAddress? hop))
                {
                    hops.Add(hop);
                }
            }
        }

        for (int i = hops.Count - 1; i >= 0; i--)
        {
            if (!IsTrusted(hops[i], trusted))
            {
                return hops[i];
            }
        }

        // Every hop is a proxy we trust, so the first of them is as far back as this goes.
        return hops.Count > 0 ? hops[0] : socket;
    }

    /// <summary>Strips a port, and the brackets an IPv6 entry carries when it has one.</summary>
    /// <remarks>
    /// <b>Because a proxy may write `203.0.113.7:41234` or `[2001:db8::1]:41234`.</b> Neither
    /// parses as an address, and treating the whole entry as unparsable would silently skip the
    /// hop that matters.
    /// </remarks>
    private static string Bare(string entry)
    {
        if (entry.StartsWith('['))
        {
            int close = entry.IndexOf(']', StringComparison.Ordinal);

            return close > 0 ? entry[1..close] : entry;
        }

        int colon = entry.IndexOf(':', StringComparison.Ordinal);

        // One colon is host:port; several mean it is a bare IPv6 address.
        return colon > 0 && entry.IndexOf(':', colon + 1) < 0 ? entry[..colon] : entry;
    }

    private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trusted)
    {
        IPAddress candidate = address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;

        return trusted.Any(network => network.Contains(candidate)
            || (candidate.AddressFamily != network.BaseAddress.AddressFamily
                && network.Contains(Reshape(candidate, network))));
    }

    /// <summary>Maps an address into the family a network is written in, when that is meaningful.</summary>
    /// <remarks>
    /// <b>A deployment writes `10.0.0.0/8` and Kestrel reports `::ffff:10.0.0.4`.</b> The
    /// unmapping above handles the common case; this covers the reverse, where the network is
    /// written in v6 and the peer arrives as v4.
    /// </remarks>
    private static IPAddress Reshape(IPAddress candidate, IPNetwork network) =>
        network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        && candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? candidate.MapToIPv6()
            : candidate;

    /// <summary>How the configured list reads back, for a startup line an operator can check.</summary>
    /// <param name="trusted">The networks.</param>
    /// <returns>A short description.</returns>
    public static string Describe(IReadOnlyList<IPNetwork> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);

        return trusted.Count == 0
            ? "none"
            : string.Join(
                ", ",
                trusted.Select(n => n.ToString()).Take(8))
              + (trusted.Count > 8
                  ? $" and {(trusted.Count - 8).ToString(CultureInfo.InvariantCulture)} more"
                  : string.Empty);
    }
}
