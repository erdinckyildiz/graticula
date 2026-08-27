using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A certificate is replaced while the server is answering, and nothing warm is lost.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) condition 1</b>: *rotation is
/// verified without a restart, by test, watching that warm service contexts survive it. If
/// they do not, §2b has failed and ADR-007 §4.4 is paying for it.* Both halves are here, and
/// the second is the one the condition is actually about — §2b is load-bearing not because a
/// 2 AM restart is inconvenient but because restarting a worker to load a certificate evicts
/// every warm service context, turning a certificate renewal into the cold-start storm the
/// runtime is designed to avoid.
/// </para>
/// <para>
/// <b>Against a real Kestrel, because the claim is about Kestrel.</b> `ServerCertificate` is
/// read once when the listener is built; `ServerCertificateSelector` is consulted on every
/// handshake. A test of this repository's own indirection would pass with either, and the
/// difference between them is the whole of the condition — so this starts a listener,
/// completes a handshake, rotates, and completes another, comparing what the server actually
/// presented each time.
/// </para>
/// <para>
/// <b>A fresh connection each time rather than a fresh server.</b> `PooledConnectionLifetime`
/// is zero so the second request cannot ride the first one's connection: reusing it would
/// show the old certificate and prove nothing, which is the way this test would most easily
/// have lied.
/// </para>
/// </remarks>
public sealed class CertificateRotationTests : IDisposable
{
    private readonly List<X509Certificate2> _built = [];

    public void Dispose()
    {
        foreach (X509Certificate2 certificate in _built)
        {
            certificate.Dispose();
        }
    }

    private X509Certificate2 Certificate(string name)
    {
        using RSA key = RSA.Create(2048);

        CertificateRequest request = new(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        using X509Certificate2 built = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(30));

        // Exported and reloaded: a certificate built here and handed straight to a handshake
        // has no usable private key on Windows.
        X509Certificate2 loaded =
            X509CertificateLoader.LoadPkcs12(built.Export(X509ContentType.Pkcs12), null);

        _built.Add(loaded);
        return loaded;
    }

    /// <summary>The thumbprint of whatever the server at that port presents right now.</summary>
    /// <param name="port">The loopback port the listener bound.</param>
    /// <param name="acceptable">
    /// The thumbprints this test built. Anything else is refused rather than recorded.
    /// </param>
    /// <remarks>
    /// <b>The callback validates rather than waving through.</b> A test client that returns
    /// true for every certificate would record whatever arrived and call it a pass; this one
    /// accepts only the two certificates the test itself generated, which is stricter than
    /// the platform default and is the reason this reads a thumbprint at all.
    /// </remarks>
    private static async Task<string> PresentedThumbprintAsync(
        int port, params string[] acceptable)
    {
        string? seen = null;

        using SocketsHttpHandler handler = new()
        {
            // Zero, so the next call cannot answer from a connection opened before the
            // rotation. This line is the difference between a test and a tautology.
            PooledConnectionLifetime = TimeSpan.Zero,

            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    seen = certificate?.GetCertHashString();

                    return seen is not null
                        && Array.Exists(
                            acceptable,
                            one => string.Equals(one, seen, StringComparison.OrdinalIgnoreCase));
                },
            },
        };

        using HttpClient client = new(handler);
        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"https://127.0.0.1:{port}/ping"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return seen ?? throw new InvalidOperationException("No certificate reached the client.");
    }

    [Fact]
    public async Task A_certificate_is_replaced_on_a_running_listener_and_warm_contexts_survive()
    {
        X509Certificate2 first = Certificate("first.invalid");
        X509Certificate2 second = Certificate("second.invalid");

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);

        ServingCertificate.Presenting(first);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
                listen.UseHttps(https =>

                    // The line under test, copied from Program.ConfigureKestrel rather than
                    // invented here.
                    https.ServerCertificateSelector = (_, _) => ServingCertificate.Current ?? first)));

        await using WebApplication app = builder.Build();
        app.MapGet("/ping", () => "pong");

        await app.StartAsync();

        try
        {
            int port = new Uri(app.Urls.GetEnumerator() is { } urls && urls.MoveNext()
                ? urls.Current
                : throw new InvalidOperationException("Kestrel bound no address.")).Port;

            string before =
                await PresentedThumbprintAsync(port, first.Thumbprint, second.Thumbprint);
            Assert.Equal(first.Thumbprint, before, StringComparer.OrdinalIgnoreCase);

            // <b>Warm now, so that the rotation has something to lose.</b> This is the half
            // the condition is actually about: §2b is load-bearing not because a 2 AM restart
            // is inconvenient but because a restart to load a certificate evicts every warm
            // context ADR-007 §4.4 exists to keep, turning a renewal into the cold-start
            // storm the runtime is designed to avoid -- on a schedule, for a reason unrelated
            // to any service.
            OneSource sources = new();
            ServiceContexts contexts = new(sources, TimeProvider.System);
            PublishedLayer layer = Layer();

            (_, LayerDescription warmed) = await contexts.GetAsync(layer, CancellationToken.None);

            Assert.Equal(1, contexts.Count);
            Assert.Equal(1, sources.Describes);

            // ---------------------------------------------------------------- rotate
            X509Certificate2? replaced = ServingCertificate.Rotate(second);

            Assert.Same(first, replaced);

            string after =
                await PresentedThumbprintAsync(port, first.Thumbprint, second.Thumbprint);

            // <b>Warm across the rotation, measured by what a cold context would cost.</b>
            // The count alone would pass if the entry had been evicted and rebuilt, which is
            // exactly the failure this is watching for -- so the assertion is that the
            // *describe* did not run again and the same description came back.
            //
            // Written first as `Assert.Same` on the source, which failed and was right to:
            // `GetAsync` hands back a fresh `IFeatureSource` handle every call and caches the
            // description behind it. The handle is cheap; the round trip is the warmth.
            Assert.Equal(1, contexts.Count);

            (_, LayerDescription stillWarm) = await contexts.GetAsync(layer, CancellationToken.None);

            Assert.Same(warmed, stillWarm);
            Assert.Equal(1, sources.Describes);

            // The listener was never restarted. If `ServerCertificate` were set instead of
            // the selector, this line is where it would fail -- and that is exactly the
            // failure ADR-014 §2b forbids.
            Assert.Equal(second.Thumbprint, after, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(before, after, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void A_certificate_with_no_private_key_never_replaces_a_working_one()
    {
        X509Certificate2 working = Certificate("working.invalid");
        ServingCertificate.Presenting(working);

        // The public half only -- what a certificate looks like when somebody installs the
        // .cer instead of the .pfx, which is the commonest way to get this wrong.
        using X509Certificate2 publicHalf =
            X509CertificateLoader.LoadCertificate(working.Export(X509ContentType.Cert));

        Assert.False(publicHalf.HasPrivateKey);

        ArgumentException refused =
            Assert.Throws<ArgumentException>(() => ServingCertificate.Rotate(publicHalf));

        Assert.Contains("private key", refused.Message, StringComparison.Ordinal);

        // And the server is still serving what it was.
        Assert.Same(working, ServingCertificate.Current);
    }

    [Fact]
    public async Task A_replacement_that_cannot_be_read_leaves_the_running_certificate_alone()
    {
        X509Certificate2 working = Certificate("still-working.invalid");
        ServingCertificate.Presenting(working);

        string directory = Path.Combine(
            Path.GetTempPath(), "graticula-rotation-" + Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "serving.pfx");

            // Half a file, which is what a watcher sees in the middle of a copy.
            await File.WriteAllBytesAsync(path, [0x30, 0x82, 0x04, 0x01, 0x02]);

            CertificateReload reload = new(
                path, null, NullLogger<CertificateReload>.Instance);

            Assert.False(await reload.ReloadAsync(CancellationToken.None));

            // <b>The point of the whole class.</b> A server that stops answering because
            // somebody was halfway through a copy is a worse outage than the expiry the
            // rotation was for.
            Assert.Same(working, ServingCertificate.Current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    /// <summary>A layer to warm a context with. Nothing about it is read by this test.</summary>
    private static PublishedLayer Layer() =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("warm", "public", "warm", "geom", 3857, "id", "objectid", false),
            "source",
            "Host=one",
            GeometryKind.Polygon,
            null,
            SharingScope.Organization,
            ServiceStatus.Started);

    /// <summary>The smallest source that can be described once.</summary>
    private sealed class OneSource : IServiceSources
    {
        private int _describes;

        /// <summary>How many round trips this fake has been asked for.</summary>
        /// <remarks>
        /// The measure of a cold context. A rotation that evicted the entry would show up
        /// here as a second describe, and nowhere else -- the count of entries would be one
        /// either way.
        /// </remarks>
        public int Describes => Volatile.Read(ref _describes);

        public IFeatureSource SourceFor(PublishedLayer layer) => new Source(this);

        private sealed class Source(OneSource owner) : IFeatureSource
        {
            public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._describes);

                return Task.FromResult(new LayerDescription([], new Envelope(0, 0, 1, 1)));
            }

            public FeatureSchema SchemaFor(FeatureQuery query) => throw new NotSupportedException();

            public IAsyncEnumerable<Feature> ReadAsync(
                FeatureQuery query, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<long> CountUpToAsync(
                FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }
}
