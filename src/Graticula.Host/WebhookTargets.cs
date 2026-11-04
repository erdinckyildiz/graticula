using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// Where a webhook may post, checked when it is made and again at every connection — ADR-266 §4, the server-side request
/// forgery a webhook is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A webhook is a URL this server fetches on an administrator's word</b>, from inside the deployment's network, and
/// tells the administrator how it went. Pointed at the cloud's metadata address or at the database beside it, it would
/// reach what the deployment did not publish. So a target must be HTTPS, must not be a private, loopback, link-local,
/// unique-local, shared-address or multicast address (<see cref="RemoteGeoParquetLocations.IsPrivate"/>, the rule the
/// remote GeoParquet reads already keep), and may name no user and password.
/// </para>
/// <para>
/// <b>Checked when it is made, and again when each connection is opened, against the address connected to.</b> A name
/// that resolved outward on Monday may resolve inward on Tuesday, and a check that is not the connection leaves the
/// window between them. <see cref="Handler"/> resolves the name itself, refuses it when any address it answers is
/// refused, and connects to an address it checked.
/// </para>
/// <para>
/// <b>No redirect is followed.</b> A redirect is a second URL the receiver chooses, and following none closes the hop
/// rather than checking it; a receiver that answers 3xx has not accepted the delivery, and the delivery log says so.
/// </para>
/// <para>
/// <b><c>Graticula:WebhooksAllowPrivate</c>, off by default</b>, lets an administrator post to a receiver on the
/// deployment's own network — an integration server beside it. With it on, plain HTTP is allowed to such an address
/// and only to such an address: a receiver on the public internet is still HTTPS, because a payload with a signature in
/// clear is one anybody on the path can read. (ADR-266 §4, INFERRED: the owner has not said that either is wanted.)
/// </para>
/// </remarks>
internal static class WebhookTargets
{
    /// <summary>How long a connection may take to open.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a whole delivery may take, from connecting to the receiver's status line.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The longest URL kept, the column's own bound.</summary>
    internal const int MaximumLength = 2048;

    /// <summary>
    /// Whether <paramref name="text"/> is a URL a webhook may post to, by its shape and — unless the deployment allows
    /// private addresses — by every address its name resolves to now.
    /// </summary>
    /// <param name="text">The URL.</param>
    /// <param name="allowPrivate">Whether a private address is allowed.</param>
    /// <param name="url">The URL, when it is one.</param>
    /// <param name="error">Why not, a sentence a caller can be shown.</param>
    /// <param name="resolve">Resolves a name; the system resolver when null. For tests.</param>
    /// <returns>Whether it may be posted to.</returns>
    internal static bool TryCheck(
        string? text, bool allowPrivate, out Uri? url, out string? error, Func<string, IPAddress[]>? resolve = null)
    {
        url = null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLength
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            error = $"'url' must be an absolute https:// URL of at most {MaximumLength} characters.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            error = "A webhook posts over HTTPS; the URL must start with https://.";
            return false;
        }

        if (parsed.UserInfo.Length > 0)
        {
            error = "A webhook URL may not carry a user name or password; a receiver checks the signature instead.";
            return false;
        }

        string host = parsed.IdnHost;

        if (host.Length == 0)
        {
            error = "The URL names no host.";
            return false;
        }

        IPAddress[] addresses;

        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
                ? [literal]
                : (resolve ?? Dns.GetHostAddresses)(host);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            error = $"'{host}' does not resolve, so nothing can be delivered to it.";
            return false;
        }

        if (addresses.Length == 0)
        {
            error = $"'{host}' does not resolve to any address.";
            return false;
        }

        if (Refusal(parsed.Scheme == Uri.UriSchemeHttp, addresses, allowPrivate) is { } refused)
        {
            error = $"'{host}' {refused}";
            return false;
        }

        url = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Why these addresses may not be posted to, or null when they may — the one rule <see cref="TryCheck"/> and the
    /// connection share.
    /// </summary>
    /// <param name="plainHttp">Whether the URL is <c>http://</c>.</param>
    /// <param name="addresses">Every address the name resolved to.</param>
    /// <param name="allowPrivate">Whether a private address is allowed.</param>
    /// <returns>The end of a sentence that starts with the host, or null.</returns>
    internal static string? Refusal(bool plainHttp, IPAddress[] addresses, bool allowPrivate)
    {
        // Every address, not the first: a name that answers one public and one private address is connected to
        // whichever the resolver puts first on the day, and the check must hold for both.
        if (!allowPrivate && addresses.FirstOrDefault(RemoteGeoParquetLocations.IsPrivate) is { } inward)
        {
            return $"resolves to {inward}, which is a private, loopback or link-local address. A webhook posts from "
                + "inside this server's network, so an address there is refused unless the deployment sets "
                + "Graticula:WebhooksAllowPrivate.";
        }

        if (plainHttp && !allowPrivate)
        {
            return "is addressed with http://; a webhook posts over HTTPS.";
        }

        if (plainHttp && addresses.FirstOrDefault(a => !RemoteGeoParquetLocations.IsPrivate(a)) is { } outward)
        {
            return $"resolves to {outward}, a public address, and is addressed with http://. Plain HTTP is allowed only to "
                + "a receiver on the deployment's own network; one on the internet is HTTPS.";
        }

        return null;
    }

    /// <summary>
    /// The handler every delivery is sent through: no redirects, no proxy, no cookies, bounded timeouts and headers, and a
    /// connection opened only to an address the rule allows.
    /// </summary>
    /// <param name="allowPrivate">Whether a private address is allowed.</param>
    /// <returns>The handler.</returns>
    internal static SocketsHttpHandler Handler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 16 * 1024,
        ResponseDrainTimeout = TimeSpan.FromSeconds(2),
        ConnectCallback = (context, cancellation) => ConnectAsync(context, allowPrivate, cancellation),
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, bool allowPrivate, CancellationToken cancellation)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false);

        bool plainHttp = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttp;

        if (addresses.Length == 0)
        {
            throw new WebhookTargetRefusedException($"'{host}' does not resolve to any address.");
        }

        if (Refusal(plainHttp, addresses, allowPrivate) is { } refused)
        {
            throw new WebhookTargetRefusedException($"'{host}' {refused}");
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // To the addresses just checked, never to a name resolved again.
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellation).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>A delivery refused at the moment of connecting, because its target is one a webhook may not reach.</summary>
internal sealed class WebhookTargetRefusedException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why.</param>
    public WebhookTargetRefusedException(string message)
        : base(message)
    {
    }
}
