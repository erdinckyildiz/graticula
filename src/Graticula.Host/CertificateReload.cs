using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Watches the certificate file and installs a replacement on the next handshake, without a
/// restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §2b, and condition 1.</b>
/// *Certificate installation and rotation must not require a restart* — the load-bearing
/// requirement in that ADR, and architectural rather than convenient: ADR-007 §4.4 keeps
/// service contexts warm, and a restart to load a certificate evicts every one of them. A
/// rotation would trigger exactly the cold-start storm the runtime exists to avoid, on a
/// schedule, for a reason unrelated to any service.
/// </para>
/// <para>
/// <b>A file watch, where §2b said the admin API — and that is a decision rather than a
/// shortcut.</b> §2b's list is *no configuration file edit, no signal, no restart, no
/// container replacement*, and replacing the certificate file is none of the four: it is the
/// thing every certificate tool already does. `cert-manager` writes a secret into a mounted
/// path, `certbot --deploy-hook` copies a file, and an operator with a new PFX copies a file.
/// An upload endpoint would need all of that *plus* a new authorization story, a new
/// disclosure surface, and validation of an uploaded blob before it can replace a working
/// certificate. It is a larger decision and it is still open: this class is the mechanism it
/// would use, and §3.4 step 3 of [ADR-017](../../docs/adr/ADR-017-admin-api.md) still records
/// the route as absent.
/// </para>
/// <para>
/// <b>Only when the operator supplied the path.</b> A generated development certificate
/// (`ServerIdentity.LoadOrCreate`) is rotated by deleting it and restarting, and watching it
/// would mean this server reacting to its own writes.
/// </para>
/// <para>
/// <b>A bad replacement changes nothing.</b> Every failure — a half-written file, a wrong
/// password, a certificate with no private key — leaves the running certificate in place and
/// logs what happened. The alternative is a server that stops answering because someone was
/// halfway through a copy, which is a worse outage than the expiry the rotation was for.
/// </para>
/// </remarks>
internal sealed class CertificateReload : BackgroundService
{
    /// <summary>
    /// How long to wait after a change before reading the file.
    /// </summary>
    /// <remarks>
    /// <b>A file arrives in pieces, and a watcher sees the first one.</b> Reading on the
    /// first event gets a truncated PKCS#12 about as often as not; the retry below covers
    /// what this misses, and this keeps the ordinary case from logging a failure it recovers
    /// from a second later. Also collapses the flurry of events a single copy produces.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

    /// <summary>How many times to retry a file that will not load, and how far apart.</summary>
    private const int Attempts = 4;

    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromSeconds(2);

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;

    private readonly string _path;
    private readonly string? _password;
    private readonly ILogger<CertificateReload> _logger;

    public CertificateReload(string path, string? password, ILogger<CertificateReload> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        _password = password;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        string name = Path.GetFileName(_path);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            Log.CertificateNotWatchable(_logger, _path);

            return;
        }

        // <b>The directory rather than the file.</b> Replacing a certificate is usually a
        // rename or a delete-and-write, and a watcher bound to a file stops watching when
        // that file goes away -- which is the exact moment it is needed.
        using FileSystemWatcher watcher = new(directory, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        // <b>A semaphore rather than the events themselves.</b> `FileSystemWatcher` raises on
        // its own thread and a single copy raises several times; this collapses them into one
        // pass and keeps the reload off that thread.
        using SemaphoreSlim changed = new(0, 1);

        void Signal(object? sender, FileSystemEventArgs e)
        {
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // A pass is already pending and will read whatever is on disk when it runs.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        watcher.Changed += Signal;
        watcher.Created += Signal;
        watcher.Renamed += Signal;

        Log.WatchingCertificate(_logger, _path);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Settle, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ReloadAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the file and installs it, or leaves the running certificate alone and says why.
    /// </summary>
    internal async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                X509Certificate2 replacement =
                    X509CertificateLoader.LoadPkcs12FromFile(_path, _password);

                // <b>The same certificate is not a rotation.</b> One file copy raises several
                // watcher events and the settle window does not always catch the last of
                // them, so the first rehearsal rotated twice and logged *replaced
                // CN=second with CN=second* -- harmless, and a line that would send an
                // operator looking for a second change that never happened.
                if (ServingCertificate.Current is { } running
                    && string.Equals(running.Thumbprint, replacement.Thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                X509Certificate2? previous = ServingCertificate.Rotate(replacement);

                Log.CertificateRotated(
                    _logger,
                    _path,
                    replacement.Subject,
                    replacement.NotAfter.ToUniversalTime().ToString("u", Culture),
                    previous is null
                        ? "nothing"
                        : $"{previous.Subject} (valid until "
                          + $"{previous.NotAfter.ToUniversalTime().ToString("u", Culture)})");

                return true;
            }
            catch (Exception e) when (e is CryptographicException or IOException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                if (attempt == Attempts)
                {
                    Log.CertificateReloadFailed(_logger, _path, Attempts, e);

                    return false;
                }

                try
                {
                    await Task.Delay(BetweenAttempts, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }
}
