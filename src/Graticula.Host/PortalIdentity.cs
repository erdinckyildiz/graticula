using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// The organisation's id — <c>portals/self.id</c>, every item's <c>orgId</c> — kept once in the platform store, so it is
/// the same at every address the portal answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's second review, N4 (2026-10-21).</b> The id was a hash of the request's origin, path base
/// included, so the same deployment was three organisations: <c>/</c> and <c>/arcgis/</c> one, <c>/studio/</c> another,
/// <c>/portal/</c> a third — and an internal and an external host name two more. An item reported a different
/// <c>orgId</c> depending on the path it was read through, and Pro must publish through <c>/studio</c> while browsers use
/// <c>/</c>. A client comparing <c>item.orgId</c> with <c>portal.id</c> — organisation sharing, *my organisation*
/// filters — was comparing two organisations. In ArcGIS the id is a fixed property of the portal.
/// </para>
/// <para>
/// <b>Made at random the first time and kept in <c>server_setting</c></b>, the table of named server-wide values
/// (migration 54). Read before the server takes requests, so every request sees the same one. <b>It changes once</b>
/// for an existing deployment, which had as many ids as addresses: whichever a client had cached, it has a new one
/// after this — INFERRED as acceptable, since no single old id could be kept for all of them (ADR-216).
/// </para>
/// <para>
/// <b>Two nodes starting together could each make one</b>; each reads the stored value back after writing, so they agree
/// unless their writes interleave exactly. Nothing here has run on two nodes.
/// </para>
/// </remarks>
internal sealed partial class PortalIdentity(IServiceProvider services, ILogger<PortalIdentity> log) : IHostedService
{
    private static readonly System.Buffers.SearchValues<char> Hex = System.Buffers.SearchValues.Create("0123456789ABCDEF");

    /// <summary>The setting's name in <c>server_setting</c>.</summary>
    public const string Setting = "portal_id";

    private static string? _current;

    /// <summary>The stored id, or null before it is read — and on a server with no platform store.</summary>
    public static string? Current => Volatile.Read(ref _current);

    /// <summary>Whether a value is an id this portal makes: sixteen upper-case hexadecimal digits.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: 16 } && value.AsSpan().IndexOfAnyExcept(Hex) < 0;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IServerSettingStore>() is not { } settings)
        {
            return;
        }

        try
        {
            if ((await settings.ReadAsync(Setting, cancellationToken).ConfigureAwait(false))?.Value is { } kept && IsWellFormed(kept))
            {
                Volatile.Write(ref _current, kept);
                return;
            }

            string made = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            await settings.WriteAsync(Setting, made, changedBy: null, cancellationToken).ConfigureAwait(false);

            string? stored = (await settings.ReadAsync(Setting, cancellationToken).ConfigureAwait(false))?.Value;
            Volatile.Write(ref _current, IsWellFormed(stored) ? stored : made);
            LogMade(log, Current!);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            // A store that cannot be read leaves the id derived from the address, as it always was, and says so.
            LogUnread(log, e);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2357, Level = LogLevel.Information, Message = "The organisation's id was made: {PortalId}. It is the same at every address from now on.")]
    private static partial void LogMade(ILogger logger, string portalId);

    [LoggerMessage(EventId = 2358, Level = LogLevel.Warning, Message = "The organisation's id could not be read from the platform store; it is derived from each request's address until it can be.")]
    private static partial void LogUnread(ILogger logger, Exception exception);
}
