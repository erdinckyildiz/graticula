using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The proxy every remote DuckDB connection goes through — ADR-067 condition 2.
/// </summary>
/// <remarks>
/// <b>Driven over a socket, as DuckDB drives it</b>: a <c>CONNECT</c> for HTTPS and an absolute-URI request for plain
/// HTTP, the two shapes measured on DuckDB 1.5.5. The destinations are on the loopback, so the refusing rule is the
/// real one in the first two tests and a permissive one where the tunnel itself is what is being tested.
/// </remarks>
public sealed class EgressProxyTests
{
    [Fact]
    public async Task A_tunnel_to_a_private_address_is_refused()
    {
        using EgressProxy proxy = new();

        string answer = await SendAsync(proxy, "CONNECT 127.0.0.1:443 HTTP/1.1\r\nHost: 127.0.0.1:443\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_request_to_a_private_address_is_refused()
    {
        using EgressProxy proxy = new();

        string answer = await SendAsync(
            proxy, "GET http://169.254.169.254/latest/meta-data/ HTTP/1.1\r\nHost: 169.254.169.254\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permitted_tunnel_carries_bytes_both_ways_to_the_address_it_checked()
    {
        using TcpListener echo = new(IPAddress.Loopback, 0);
        echo.Start();
        int port = ((IPEndPoint)echo.LocalEndpoint).Port;

        Task echoing = Task.Run(async () =>
        {
            using TcpClient one = await echo.AcceptTcpClientAsync();
            NetworkStream stream = one.GetStream();
            byte[] buffer = new byte[64];
            int read = await stream.ReadAsync(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, read));
        });

        using EgressProxy proxy = new(refused: _ => false);
        using TcpClient client = await ConnectAsync(proxy);
        NetworkStream down = client.GetStream();

        await down.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(down), StringComparison.Ordinal);
        await ReadLineAsync(down);   // the blank line ending the proxy's answer

        await down.WriteAsync("ping"u8.ToArray());
        byte[] back = new byte[4];
        await down.ReadExactlyAsync(back);

        Assert.Equal("ping", Encoding.ASCII.GetString(back));
        await echoing;
    }

    [Fact]
    public async Task A_permitted_plain_request_is_forwarded_once_in_origin_form_and_asked_to_close()
    {
        using TcpListener origin = new(IPAddress.Loopback, 0);
        origin.Start();
        int port = ((IPEndPoint)origin.LocalEndpoint).Port;

        Task<string> received = Task.Run(async () =>
        {
            using TcpClient one = await origin.AcceptTcpClientAsync();
            NetworkStream stream = one.GetStream();
            byte[] buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            return Encoding.ASCII.GetString(buffer, 0, read);
        });

        using EgressProxy proxy = new(refused: _ => false);

        string answer = await SendAsync(
            proxy, $"GET http://127.0.0.1:{port}/files/a.parquet HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: keep-alive\r\n\r\n");

        string request = await received;

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        Assert.StartsWith("GET /files/a.parquet HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Connection: close", request, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-alive", request, StringComparison.Ordinal);
    }

    private static async Task<TcpClient> ConnectAsync(EgressProxy proxy)
    {
        string[] parts = proxy.Address.Split(':');
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        return client;
    }

    private static async Task<string> SendAsync(EgressProxy proxy, string request)
    {
        using TcpClient client = await ConnectAsync(proxy);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using StreamReader reader = new(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        StringBuilder line = new();
        byte[] one = new byte[1];

        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] == '\n')
            {
                break;
            }

            line.Append((char)one[0]);
        }

        return line.ToString().TrimEnd('\r');
    }
}
