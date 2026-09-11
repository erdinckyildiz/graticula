using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;

namespace Graticula.Host;

/// <summary>
/// Runs the grant-announcement subscription for the server's life — D-249.
/// </summary>
/// <remarks>
/// <b>A wrapper and nothing else</b>, because the listener lives beside the store it listens to
/// and that project does not know what a host is. Every failure is the listener's to absorb: it
/// turns the anonymous cache off and retries, and nothing it meets is allowed to stop the server.
/// </remarks>
internal sealed class GrantsListening : BackgroundService
{
    private readonly PostgresGrantsListener _listener;

    /// <summary>Creates the service.</summary>
    /// <param name="listener">What it runs.</param>
    public GrantsListening(PostgresGrantsListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listener = listener;
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _listener.RunAsync(stoppingToken);
}
