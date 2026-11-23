using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host.Tests;

/// <summary>
/// A certificate authority made in the test, and the client certificates it issues — ADR-315 condition 1. Nothing here is
/// read from a file and nothing is written outside the test's own temporary directory.
/// </summary>
public sealed class TestPki : IDisposable
{
    private long _serial = 1000;

    /// <summary>A new authority with its own key.</summary>
    /// <param name="name">Its common name.</param>
    public TestPki(string name)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}, O=Graticula test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddYears(2));
    }

    /// <summary>The authority, with its key.</summary>
    public X509Certificate2 Authority { get; }

    /// <summary>The authority as PEM, as an operator's <c>Authorities</c> file holds it.</summary>
    public string Pem => Authority.ExportCertificatePem();

    /// <summary>Writes the authority to a file in <paramref name="directory"/> and returns its path.</summary>
    public string WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"authorities-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, Pem);
        return path;
    }

    /// <summary>A client certificate, with a key a TLS client can use.</summary>
    /// <param name="commonName">The subject's CN, or null for none.</param>
    /// <param name="email">An RFC 822 name in the alternative names, or null.</param>
    /// <param name="upn">A user principal name in the alternative names, or null.</param>
    /// <param name="crl">A CRL distribution point, or null.</param>
    /// <param name="clientAuth">Whether its extended key usage is client authentication; false makes it server only.</param>
    /// <param name="notAfter">When it ends; a year from now when null.</param>
    public X509Certificate2 Issue(
        string? commonName, string? email = null, string? upn = null, Uri? crl = null, bool clientAuth = true,
        DateTimeOffset? notAfter = null)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            commonName is null ? "O=Graticula test, OU=No common name" : $"CN={commonName}, O=Graticula test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(clientAuth ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Authority, true, false));

        if (email is not null || upn is not null)
        {
            SubjectAlternativeNameBuilder names = new();
            if (email is not null) names.AddEmailAddress(email);
            if (upn is not null) names.AddUserPrincipalName(upn);
            request.CertificateExtensions.Add(names.Build());
        }

        if (crl is not null)
        {
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crl.AbsoluteUri]));
        }

        byte[] serial = new BigInteger(Interlocked.Increment(ref _serial)).ToByteArray(isUnsigned: true, isBigEndian: true);
        DateTimeOffset end = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        DateTimeOffset start = end < DateTimeOffset.UtcNow ? end.AddDays(-20) : DateTimeOffset.UtcNow.AddDays(-1);

        using X509Certificate2 issued = request.Create(Authority, start, end, serial);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);

        // Through PKCS#12 so the key is one SChannel can use for a client handshake; an ephemeral key is refused on Windows.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>A CRL from this authority listing <paramref name="revoked"/>.</summary>
    public byte[] Crl(params X509Certificate2[] revoked)
    {
        CertificateRevocationListBuilder builder = new();

        foreach (X509Certificate2 certificate in revoked)
        {
            builder.AddEntry(certificate, DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        return builder.Build(Authority, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256);
    }

    /// <inheritdoc/>
    public void Dispose() => Authority.Dispose();

    /// <summary>
    /// Serves one CRL over plain HTTP on the loopback, as a CA's distribution point does — enough HTTP for a chain engine.
    /// </summary>
    public sealed class CrlServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        /// <summary>Starts serving <see cref="Body"/> at every path.</summary>
        public CrlServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        /// <summary>The CRL served.</summary>
        public byte[] Body { get; set; } = [];

        /// <summary>A distribution point on this server, unique per call so no cache answers for another test.</summary>
        public Uri Point() => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{Guid.NewGuid():N}.crl");

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[8192];
                        StringBuilder head = new();

                        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            int read = await stream.ReadAsync(buffer);
                            if (read == 0) return;
                            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }

                        byte[] body = Body;
                        byte[] answer = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(answer);
                        await stream.WriteAsync(body);
                    }
                });
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    /// <summary>A port nothing listens on, for a distribution point that cannot be reached.</summary>
    public static Uri Unreachable()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/{Guid.NewGuid():N}.crl");
    }

    /// <summary>A browser: a client that keeps cookies, follows nothing, and presents <paramref name="certificate"/>.</summary>
    public static HttpClient Browser(int port, X509Certificate2? certificate, CookieContainer? jar = null)
    {
#pragma warning disable CA5400, CA2000
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = jar ?? new CookieContainer(),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            ClientCertificateOptions = ClientCertificateOption.Manual,
        };
#pragma warning restore CA5400, CA2000

        if (certificate is not null)
        {
            handler.ClientCertificates.Add(certificate);
        }

        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Settings dictionary helper.</summary>
    public static Dictionary<string, string> Settings(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        foreach ((string key, string value) in pairs) settings[key] = value;
        return settings;
    }
}
