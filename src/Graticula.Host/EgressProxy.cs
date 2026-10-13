using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// A proxy on the loopback that every remote DuckDB connection goes through, which refuses a private destination at
/// the moment of connecting — ADR-067 condition 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The residue it closes.</b> A remote location's address is checked when it is registered and when it is
/// opened, and DuckDB then connects on its own: it follows a redirect from an allowed host without asking, and it
/// resolves a name when it connects, so a name that moved to a private address after the check — or an allowed host
/// that redirects to the cloud's metadata address — took the read inward. DuckDB 1.5.5 has no setting for
/// redirects. It does send everything through <c>http_proxy</c>: HTTPS as <c>CONNECT host:port</c>, each hop of a
/// redirect included, and plain HTTP as an absolute-URI request (measured, 2026-10-13). This proxy resolves the
/// destination itself, refuses it when any address is private, loopback or link-local — the same rule as the
/// registration check, <see cref="RemoteGeoParquetLocations.IsPrivate"/> — and connects to the address it checked,
/// so there is no window between the check and the connection.
/// </para>
/// <para>
/// <b>Plain HTTP is forwarded one request per connection.</b> A proxy connection may carry several requests to
/// different hosts; forwarding the bytes after the first would send the second to the first one's host. The request
/// is rewritten to origin form with <c>Connection: close</c>, so the upstream closes after answering and the client
/// opens a new connection for the next.
/// </para>
/// <para>
/// <b>Not started when the deployment allows private addresses</b> (<c>Graticula:RemoteDataAllowPrivate</c>):
/// that deployment has said its object store is on its own network, and the proxy would refuse exactly that.
/// </para>
/// </remarks>
internal sealed class EgressProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<IPAddress, bool> _refused;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Starts the proxy on an ephemeral loopback port.</summary>
    /// <param name="refused">Which addresses are refused; <see cref="RemoteGeoParquetLocations.IsPrivate"/> by default.</param>
    public EgressProxy(Func<IPAddress, bool>? refused = null)
    {
        _refused = refused ?? RemoteGeoParquetLocations.IsPrivate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    /// <summary><c>127.0.0.1:port</c>, as DuckDB's <c>http_proxy</c> takes it.</summary>
    public string Address => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream down = client.GetStream();

                (string head, byte[] rest) = await ReadHeadAsync(down).ConfigureAwait(false);

                if (head.Length == 0)
                {
                    return;
                }

                string[] request = head[..head.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ', 3);

                if (request.Length < 3)
                {
                    await AnswerAsync(down, "400 Bad Request").ConfigureAwait(false);
                    return;
                }

                if (request[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await TunnelAsync(down, request[1]).ConfigureAwait(false);
                }
                else
                {
                    await ForwardAsync(down, request, head, rest).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A connection dropped by either side; there is no one left to answer.
            }
        }
    }

    /// <summary>An HTTPS destination: checked, connected to the address checked, then the bytes in both directions.</summary>
    private async Task TunnelAsync(NetworkStream down, string target)
    {
        int colon = target.LastIndexOf(':');

        if (colon <= 0 || !int.TryParse(target.AsSpan(colon + 1), out int port))
        {
            await AnswerAsync(down, "400 Bad Request").ConfigureAwait(false);
            return;
        }

        if (await PermittedAsync(target[..colon].Trim('[', ']')).ConfigureAwait(false) is not { } address)
        {
            await AnswerAsync(down, "403 Forbidden").ConfigureAwait(false);
            return;
        }

        using TcpClient up = new();
        await up.ConnectAsync(address, port, _stop.Token).ConfigureAwait(false);

        await down.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _stop.Token).ConfigureAwait(false);

        await PipeAsync(down, up.GetStream()).ConfigureAwait(false);
    }

    /// <summary>A plain HTTP request: checked, rewritten to origin form with <c>Connection: close</c>, forwarded once.</summary>
    private async Task ForwardAsync(NetworkStream down, string[] request, string head, byte[] rest)
    {
        if (!Uri.TryCreate(request[1], UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            await AnswerAsync(down, "400 Bad Request").ConfigureAwait(false);
            return;
        }

        if (await PermittedAsync(uri.IdnHost).ConfigureAwait(false) is not { } address)
        {
            await AnswerAsync(down, "403 Forbidden").ConfigureAwait(false);
            return;
        }

        StringBuilder rewritten = new();
        rewritten.Append(request[0]).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(request[2]).Append("\r\n");

        foreach (string line in head.Split("\r\n").Skip(1))
        {
            if (line.Length == 0
                || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rewritten.Append(line).Append("\r\n");
        }

        rewritten.Append("Connection: close\r\n\r\n");

        using TcpClient up = new();
        await up.ConnectAsync(address, uri.Port, _stop.Token).ConfigureAwait(false);

        NetworkStream upward = up.GetStream();
        await upward.WriteAsync(Encoding.ASCII.GetBytes(rewritten.ToString()), _stop.Token).ConfigureAwait(false);

        if (rest.Length > 0)
        {
            await upward.WriteAsync(rest, _stop.Token).ConfigureAwait(false);
        }

        await PipeAsync(down, upward).ConfigureAwait(false);
    }

    /// <summary>The address to connect to, or null when the host does not resolve or any address it has is refused.</summary>
    private async Task<IPAddress?> PermittedAsync(string host)
    {
        IPAddress[] addresses;

        try
        {
            addresses = IPAddress.TryParse(host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return null;
        }

        // Every address, not the first: a name with one public and one private address is a name that may be
        // answered with either.
        return addresses.Length == 0 || addresses.Any(_refused) ? null : addresses[0];
    }

    private async Task PipeAsync(NetworkStream down, NetworkStream up)
    {
        using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        Task one = CopyAsync(down, up, either.Token);
        Task two = CopyAsync(up, down, either.Token);

        await Task.WhenAny(one, two).ConfigureAwait(false);
        await either.CancelAsync().ConfigureAwait(false);
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            int read;

            while ((read = await from.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Either side closed.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The request head, and whatever arrived after it in the same reads.</summary>
    private static async Task<(string Head, byte[] After)> ReadHeadAsync(NetworkStream down)
    {
        byte[] buffer = new byte[16 * 1024];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await down.ReadAsync(buffer.AsMemory(filled), CancellationToken.None).ConfigureAwait(false);

            if (read == 0)
            {
                return (string.Empty, []);
            }

            filled += read;

            int end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);

            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(buffer, 0, end + 4), buffer[(end + 4)..filled]);
            }
        }

        return (string.Empty, []);
    }

    private static Task AnswerAsync(NetworkStream down, string status) =>
        down.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
